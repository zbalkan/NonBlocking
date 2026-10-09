using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Nb = NonBlocking;

namespace NonBlocking.Bench;

/// <summary>
/// Correctness stress for NonBlocking.ConcurrentDictionary under continuous resizing.
/// It checks properties a weakly consistent snapshot must still guarantee, so a change to
/// enumeration, copying or hashing that breaks them fails loudly on x64 and on ARM64:
/// <list type="number">
/// <item>Every key present for the whole run appears in every enumeration.</item>
/// <item>No enumeration yields the same key twice.</item>
/// <item>Every observed value belongs to its key.</item>
/// <item>No observed 16-byte struct value is torn.</item>
/// </list>
/// Roles cover string keys (churn, flapping keys), int keys with 16-byte struct values, long keys
/// with structured and degenerate bit patterns, and in-place updates of atomic primitive values
/// (TryUpdate, AddOrUpdate, conditional remove, GetOrAdd) together with Clear.
/// A watchdog fails the run with per-thread progress if any worker stops making progress,
/// so a livelock reports itself instead of surfacing as an opaque CI timeout.
/// </summary>
internal sealed class Stress
{
    private const int StableStringKeys = 10_000;
    private const int StablePairKeys = 4_096;
    private const int ChurnWindow = 512;
    private const int MaxReported = 20;
    private const int RoleCount = 6;

    // Keys every churn thread removes and re-adds, so a key returns to the table while other
    // threads remove it, re-add it and probe past its old slot.
    private const int FlappingKeys = 64;

    // Churn keys live in [ChurnBase, ChurnBase + ChurnBands * ChurnBand), far from every stable
    // key (0..4091, -1, int.MinValue, 0x4000_0000, 0xC000_0000), and wrap within their band so
    // neither long runs nor high thread ids can overflow into the stable range.
    private const int ChurnBase = 1 << 24;
    private const int ChurnBand = 1 << 20;
    private const int ChurnBands = 64;

    private static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallProbe = TimeSpan.FromSeconds(5);

    /// <summary>Int keys on special paths: the zero-key hash, and keys whose tagged hash equals the tag bits.</summary>
    private static readonly int[] EdgeIntKeys = { 0, 0x4000_0000, int.MinValue, unchecked((int)0xC000_0000), -1 };

    /// <summary>
    /// A 16-byte value bound to its key: B = ~A ^ Owner(key). A torn read mixes halves of two
    /// writes and breaks the relation, and so does a value returned under the wrong key.
    /// </summary>
    private readonly record struct Pair(long A, long B)
    {
        public static Pair For(int key, long a) => new(a, ~a ^ Owner(key));

        public bool BelongsTo(int key) => B == (~A ^ Owner(key));

        private static long Owner(int key) => unchecked((long)(uint)key * (long)0x9E37_79B9_7F4A_7C15);
    }

    private readonly Nb.ConcurrentDictionary<string, string> _strings = new();
    private readonly Nb.ConcurrentDictionary<int, Pair> _pairs = new();
    private readonly Nb.ConcurrentDictionary<long, long> _longs = new();
    private readonly Nb.ConcurrentDictionary<int, long> _atomics = new();
    private readonly Nb.ConcurrentDictionary<int, long> _clearable = new();
    private readonly List<long> _stableLongs = new();
    private readonly int[] _atomicKeys;
    private readonly string[] _stableStrings = new string[StableStringKeys];
    private readonly string[] _flappingStrings = new string[FlappingKeys];
    private readonly List<int> _stablePairKeys = new(EdgeIntKeys);
    private readonly ConcurrentQueue<string> _failures = new();
    private readonly CancellationTokenSource _stop;
    private readonly int _seconds;
    private long[] _progress = Array.Empty<long>();
    private long _enumerations;
    private long _checkedItems;
    private long _writes;

    private Stress(int seconds)
    {
        if (seconds <= 0)
        {
            throw new ArgumentException("Stress duration must be positive.", nameof(seconds));
        }

        _seconds = seconds;
        _stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        for (int i = 0; i < _stableStrings.Length; i++)
        {
            _stableStrings[i] = "s-" + i.ToString(CultureInfo.InvariantCulture);
            _strings[_stableStrings[i]] = ValueFor(_stableStrings[i]);
        }

        for (int i = 0; i < _flappingStrings.Length; i++)
        {
            _flappingStrings[i] = "f-" + i.ToString(CultureInfo.InvariantCulture);
        }

        for (int i = 1; _stablePairKeys.Count < StablePairKeys; i++)
        {
            _stablePairKeys.Add(i);
        }

        foreach (int k in _stablePairKeys)
        {
            _pairs[k] = Pair.For(k, k);
        }

        // Long keys whose bit patterns stress the 64-bit hash: equal halves, groups differing
        // only in bits 62-63, and the extremes.
        _stableLongs.AddRange(new[] { 0L, -1L, long.MinValue, long.MaxValue, 1L << 62, 3L << 62 });
        for (long i = 1; i <= 512; i++)
        {
            _stableLongs.Add((i << 32) | i);
        }

        for (long b = 1; b <= 128; b++)
        {
            for (long v = 0; v < 4; v++)
            {
                _stableLongs.Add((b * 0x9E37_79B9L) | (v << 62));
            }
        }

        foreach (long k in _stableLongs)
        {
            _longs[k] = LongValueFor(k);
        }

        _atomicKeys = new int[1_024 + EdgeIntKeys.Length];
        for (int i = 0; i < 1_024; i++)
        {
            _atomicKeys[i] = i + 1;
        }

        EdgeIntKeys.CopyTo(_atomicKeys, 1_024);
        foreach (int k in _atomicKeys)
        {
            _atomics[k] = Atomic(k, 0);
        }
    }

    private static long LongValueFor(long key) => ~key ^ 0x5DEE_CE66DL;

    // Atomic primitive value bound to its key: the key in the low half, a version in the high half.
    private static long Atomic(int key, long version) => (version << 32) | (uint)key;

    private static bool AtomicBelongs(int key, long value) => (int)value == key;

    public static int Run(int seconds, int threads)
    {
        var stress = new Stress(seconds);
        try
        {
            return stress.Execute(Math.Max(RoleCount, threads));
        }
        finally
        {
            stress._stop.Dispose();
        }
    }

    private int Execute(int threads)
    {
        // Roles rotate so every run has at least one of each, whatever the core count.
        var roles = new Action<int>[] { ChurnStrings, EnumerateStrings, WritePairs, EnumeratePairs, LongKeys, AtomicValues };
        var pool = new Thread[threads];
        _progress = new long[threads];
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            var role = roles[t % roles.Length];
            pool[t] = new Thread(() =>
            {
                try
                {
                    role(id);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A worker crash is a finding; record it with the others instead of killing the process.
                    Fail($"thread {id} threw {ex.GetType().Name}: {ex.Message}");
                }
            })
            { IsBackground = true, Name = $"stress-{id}" };
        }

        var sw = Stopwatch.StartNew();
        foreach (var t in pool)
        {
            t.Start();
        }

        var deadline = TimeSpan.FromSeconds(_seconds) + WatchdogGrace;
        foreach (var t in pool)
        {
            var remaining = deadline - sw.Elapsed;
            if (remaining <= TimeSpan.Zero || !t.Join(remaining))
            {
                return ReportStall(pool, roles);
            }
        }

        Console.Error.WriteLine(
            $"stress: {threads} threads, {sw.Elapsed.TotalSeconds:F1}s, {_enumerations:N0} enumerations, " +
            $"{_checkedItems:N0} items checked, {_writes:N0} writes, {_failures.Count} failure(s)");

        PrintFailures();
        return _failures.IsEmpty ? 0 : 1;
    }

    /// <summary>
    /// Workers missed the deadline. Sample progress twice to separate threads that are slow
    /// from threads that are stuck, report both, and fail. Workers are background threads,
    /// so returning lets the process exit even though they never finished.
    /// </summary>
    private int ReportStall(Thread[] pool, Action<int>[] roles)
    {
        _stop.Cancel();
        var before = (long[])_progress.Clone();
        Thread.Sleep(StallProbe);
        Console.Error.WriteLine($"stress: watchdog fired {WatchdogGrace.TotalSeconds:F0}s after the {_seconds}s run ended");
        for (int t = 0; t < pool.Length; t++)
        {
            if (!pool[t].IsAlive)
            {
                continue;
            }

            long now = Volatile.Read(ref _progress[t]);
            string state = now == before[t] ? "STALLED (no progress)" : "slow (still progressing)";
            Console.Error.WriteLine(
                $"FAIL: thread {t} ({roles[t % roles.Length].Method.Name}) {state}, {now:N0} iterations");
        }

        PrintFailures();
        return 1;
    }

    /// <summary>Prints at most <see cref="MaxReported"/> failures, then how many were left out.</summary>
    private void PrintFailures()
    {
        int shown = 0;
        foreach (var f in _failures)
        {
            if (shown == MaxReported)
            {
                Console.Error.WriteLine($"FAIL: ... {_failures.Count - shown} more not shown");
                break;
            }

            Console.Error.WriteLine("FAIL: " + f);
            shown++;
        }
    }

    private bool Running => !_stop.IsCancellationRequested;

    private void Tick(int tid) => Volatile.Write(ref _progress[tid], _progress[tid] + 1);

    private void Fail(string message)
    {
        _failures.Enqueue(message);
        _stop.Cancel();
    }

    private static string ValueFor(string key) => key + "|v";

    private void ChurnStrings(int tid)
    {
        var window = new Queue<string>(ChurnWindow + 1);
        long n = 0, local = 0;
        while (Running)
        {
            // Unique keys force tombstones, sweeps and churn resizes while enumerators run.
            var key = string.Create(CultureInfo.InvariantCulture, $"c{tid}-{n++}");
            _strings.TryAdd(key, ValueFor(key));
            window.Enqueue(key);
            local++;
            if (window.Count > ChurnWindow)
            {
                _strings.TryRemove(window.Dequeue(), out _);
                local++;
            }

            // Key from n % 64, phase from n / 64: each key alternates remove and add on successive
            // passes. Taking the phase from n's parity would fix it per key, since 64 is even.
            var flap = _flappingStrings[(int)(n % FlappingKeys)];
            if (((n / FlappingKeys) & 1) == 0)
            {
                _strings.TryRemove(flap, out _);
            }
            else
            {
                _strings.TryAdd(flap, ValueFor(flap));
            }

            local++;
            if (_strings.TryGetValue(flap, out var seen) && !string.Equals(seen, ValueFor(flap), StringComparison.Ordinal))
            {
                Fail($"TryGetValue for '{flap}' returned '{seen}'");
            }

            Tick(tid);
        }

        Interlocked.Add(ref _writes, local);
    }

    private void EnumerateStrings(int tid)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long runs = 0, items = 0;
        while (Running)
        {
            seen.Clear();
            foreach (var kv in _strings)
            {
                items++;
                if (!seen.Add(kv.Key))
                {
                    Fail($"string key '{kv.Key}' yielded twice in one enumeration");
                }

                if (!string.Equals(kv.Value, ValueFor(kv.Key), StringComparison.Ordinal))
                {
                    Fail($"string key '{kv.Key}' yielded value '{kv.Value}'");
                }
            }

            foreach (var key in _stableStrings)
            {
                if (!seen.Contains(key))
                {
                    Fail($"stable string key '{key}' missing from an enumeration of {seen.Count} items");
                    break;
                }
            }

            runs++;
            Tick(tid);
        }

        Interlocked.Add(ref _enumerations, runs);
        Interlocked.Add(ref _checkedItems, items);
    }

    private void WritePairs(int tid)
    {
        var rnd = new Random(tid);
        long iterations = 0, local = 0;
        int band = ChurnBase + ((tid % ChurnBands) * ChurnBand);
        int churn = 0;
        while (Running)
        {
            int k = _stablePairKeys[rnd.Next(_stablePairKeys.Count)];
            switch (iterations % 3)
            {
                case 0:
                    _pairs[k] = Pair.For(k, rnd.NextInt64());
                    local++;
                    break;
                case 1:
                    _pairs.AddOrUpdate(k, key => Pair.For(key, key), (key, old) => Pair.For(key, old.A + 1));
                    local++;
                    break;
                default:
                    // Churn so resizes overlap with value writes and reads on the stable keys.
                    int added = band + (churn % ChurnBand);
                    int removed = band + ((churn - ChurnWindow + ChurnBand) % ChurnBand);
                    _pairs.TryAdd(added, Pair.For(added, added));
                    _pairs.TryRemove(removed, out _);
                    churn = (churn + 1) % ChurnBand;
                    local += 2;
                    break;
            }

            if (!_pairs.TryGetValue(k, out var p))
            {
                Fail($"stable int key {k} missing from TryGetValue");
            }
            else if (!p.BelongsTo(k))
            {
                Fail($"TryGetValue for key {k} returned a torn or foreign value: A={p.A:x16} B={p.B:x16}");
            }

            iterations++;
            Tick(tid);
        }

        Interlocked.Add(ref _writes, local);
    }

    private void EnumeratePairs(int tid)
    {
        var seen = new HashSet<int>();
        long runs = 0, items = 0;
        while (Running)
        {
            seen.Clear();
            foreach (var kv in _pairs)
            {
                items++;
                if (!seen.Add(kv.Key))
                {
                    Fail($"int key {kv.Key} yielded twice in one enumeration");
                }

                if (!kv.Value.BelongsTo(kv.Key))
                {
                    Fail($"torn or foreign value for key {kv.Key}: A={kv.Value.A:x16} B={kv.Value.B:x16}");
                }
            }

            foreach (int key in _stablePairKeys)
            {
                if (!seen.Contains(key))
                {
                    Fail($"stable int key {key} missing from an enumeration of {seen.Count} items");
                    break;
                }
            }

            // _clearable is filled and cleared by AtomicValues while this scan runs.
            seen.Clear();
            foreach (var kv in _clearable)
            {
                items++;
                if (!seen.Add(kv.Key))
                {
                    Fail($"key {kv.Key} yielded twice while the dictionary was being cleared");
                }

                if (!AtomicBelongs(kv.Key, kv.Value))
                {
                    Fail($"foreign value {kv.Value:x16} for key {kv.Key} while clearing");
                }
            }

            runs++;
            Tick(tid);
        }

        Interlocked.Add(ref _enumerations, runs);
        Interlocked.Add(ref _checkedItems, items);
    }

    private void LongKeys(int tid)
    {
        var rnd = new Random(tid);
        var seen = new HashSet<long>();
        long churn = (1L << 48) + ((long)tid << 32), iterations = 0, local = 0;
        while (Running)
        {
            long k = _stableLongs[rnd.Next(_stableLongs.Count)];
            _longs[k] = LongValueFor(k);
            _longs.TryAdd(churn, LongValueFor(churn));
            _longs.TryRemove(churn - ChurnWindow, out _);
            churn++;
            local += 3;

            if (!_longs.TryGetValue(k, out long v))
            {
                Fail($"stable long key {k:x16} missing from TryGetValue");
            }
            else if (v != LongValueFor(k))
            {
                Fail($"long key {k:x16} returned foreign value {v:x16}");
            }

            if ((++iterations & 255) == 0)
            {
                seen.Clear();
                foreach (var kv in _longs)
                {
                    if (!seen.Add(kv.Key))
                    {
                        Fail($"long key {kv.Key:x16} yielded twice in one enumeration");
                    }

                    if (kv.Value != LongValueFor(kv.Key))
                    {
                        Fail($"long key {kv.Key:x16} enumerated with foreign value {kv.Value:x16}");
                    }
                }

                foreach (long key in _stableLongs)
                {
                    if (!seen.Contains(key))
                    {
                        Fail($"stable long key {key:x16} missing from an enumeration of {seen.Count} items");
                        break;
                    }
                }

                Interlocked.Increment(ref _enumerations);
            }

            Tick(tid);
        }

        Interlocked.Add(ref _writes, local);
    }

    /// <summary>
    /// long values on 64-bit processes are updated in place inside their box, guarded by a write
    /// status that conditional removes and TryUpdate freeze. Every operation that touches that
    /// path runs here; values carry their key, so a write landing on the wrong key is caught.
    /// </summary>
    private void AtomicValues(int tid)
    {
        var rnd = new Random(tid + 1_000);
        long iterations = 0, local = 0;
        while (Running)
        {
            int k = _atomicKeys[rnd.Next(_atomicKeys.Length)];
            _atomics.TryGetValue(k, out long current);

            // Write calls issued this iteration, as in the other roles: one per call, so the
            // remove-and-re-add branches count two.
            int writes = 1;
            switch (iterations % 6)
            {
                case 0:
                    _atomics[k] = Atomic(k, iterations);
                    break;
                case 1:
                    _atomics.TryUpdate(k, Atomic(k, iterations), current);
                    break;
                case 2:
                    _atomics.AddOrUpdate(k, key => Atomic(key, 0), (key, old) => Atomic(key, (old >> 32) + 1));
                    break;
                case 3:
                    if (_atomics.TryRemove(k, out long removed) && !AtomicBelongs(k, removed))
                    {
                        Fail($"TryRemove for key {k} returned foreign value {removed:x16}");
                    }

                    _atomics.TryAdd(k, Atomic(k, iterations));
                    writes = 2;
                    break;
                case 4:
                    // Conditional remove: matches the old value, which freezes the box first.
                    if (_atomics.TryRemove(new KeyValuePair<int, long>(k, current)))
                    {
                        _atomics.TryAdd(k, Atomic(k, iterations));
                        writes = 2;
                    }

                    break;
                default:
                    long got = _atomics.GetOrAdd(k, key => Atomic(key, 0));
                    if (!AtomicBelongs(k, got))
                    {
                        Fail($"GetOrAdd for key {k} returned foreign value {got:x16}");
                    }

                    break;
            }

            local += writes;
            if (_atomics.TryGetValue(k, out long after) && !AtomicBelongs(k, after))
            {
                Fail($"atomic value for key {k} is foreign: {after:x16}");
            }

            _clearable[k] = Atomic(k, iterations);
            local++;
            if ((++iterations & 1023) == 0)
            {
                _clearable.Clear();
                local++;
            }

            Tick(tid);
        }

        Interlocked.Add(ref _writes, local);
    }
}
