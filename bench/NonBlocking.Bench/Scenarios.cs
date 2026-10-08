using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace NonBlocking.Bench;

/// <summary>
/// Scenario catalogue. Each scenario isolates one cost the DSA assessment identified, so a
/// candidate change can be judged on the path it targets rather than on an aggregate score.
/// </summary>
internal static class Scenarios
{
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["get-str"] = "TryGetValue hits on string keys (read scaling)",
        ["get-int-uniform"] = "TryGetValue hits on int keys spread over [1, 2^30)",
        ["get-int-topbits"] = "TryGetValue hits on int keys that differ only in bits 30-31 (hash tag collapse)",
        ["get-long-uniform"] = "TryGetValue hits on long keys spread over [1, 2^62)",
        ["get-long-topbits"] = "TryGetValue hits on long keys that differ only in bits 62-63 (hash tag collapse)",
        ["insert-long-mirrored"] = "Fill fresh dictionaries with 2,048 long keys whose halves are equal (all fold to one hash)",
        ["miss-int-uniform"] = "As get-int-uniform, but half the lookups are for keys that were never inserted",
        ["miss-int-topbits"] = "As get-int-topbits, but half the lookups miss (probe until an empty slot)",
        ["miss-long-topbits"] = "As get-long-topbits, but half the lookups miss",
        ["small-int-uniform"] = "TryGetValue hits on a 1,024-key int table that stays in cache (exposes hashing cost)",
        ["small-long-uniform"] = "TryGetValue hits on a 1,024-key long table that stays in cache",
        ["grow-int"] = "Fill fresh dictionaries with 100,000 int keys (insert path including resizes)",
        ["grow-str"] = "Fill fresh dictionaries with 100,000 string keys",
        ["get-str-samecmp"] = "As get-str, with the same explicit string comparer for both implementations",
        ["mixed-str"] = "90% TryGetValue / 10% indexer overwrite on string keys",
        ["churn-str"] = "Unique-key TryAdd + TryRemove with a small live set (tombstone growth)",
        ["churn-long"] = "As churn-str with long keys: no key objects to collect, so table work without GC effects",
        ["readd-str"] = "TryRemove then TryAdd of the same existing string key (slot reuse for a returning key)",
        ["enum-write"] = "One thread enumerates while the others overwrite values (snapshot cost)",
    };

    // The implementation is validated before dispatch: the fill scenarios pick their dictionary
    // type themselves, and an unknown name must not silently run as the BCL.
    //
    // One process measures every thread count. Keys are generated once and shared by all counts;
    // read-only scenarios also fill their map once, since lookups leave it unchanged. Scenarios
    // that write build a fresh map per count, so each count starts from the same state.
    public static void Run(string scenario, string impl, int[] threadCounts, RunSettings s, Action<Measurement> emit)
    {
        if (impl is not ("nb" or "bcl"))
        {
            throw new ArgumentException($"Unknown implementation '{impl}'. Use 'nb' or 'bcl'.", nameof(impl));
        }

        var counts = new Counts(threadCounts, s, emit);
        switch (scenario)
        {
            case "get-str": Reads(scenario, impl, counts, (KeySets.Strings(s.Size), null)); break;
            case "get-int-uniform": Reads(scenario, impl, counts, (KeySets.UniformInts(s.Size, s.Seed), null)); break;
            case "get-int-topbits": Reads(scenario, impl, counts, (KeySets.TopBitVariantInts(s.Size, s.Seed), null)); break;
            case "get-long-uniform": Reads(scenario, impl, counts, (KeySets.UniformLongs(s.Size, s.Seed), null)); break;
            case "get-long-topbits": Reads(scenario, impl, counts, (KeySets.TopBitVariantLongs(s.Size, s.Seed), null)); break;
            case "miss-int-uniform": Reads(scenario, impl, counts, KeySets.WithMisses(n => KeySets.UniformInts(n, s.Seed), s.Size, misses: true)); break;
            case "miss-int-topbits": Reads(scenario, impl, counts, KeySets.WithMisses(n => KeySets.TopBitVariantInts(n, s.Seed), s.Size, misses: true)); break;
            case "miss-long-topbits": Reads(scenario, impl, counts, KeySets.WithMisses(n => KeySets.TopBitVariantLongs(n, s.Seed), s.Size, misses: true)); break;
            case "small-int-uniform": Reads(scenario, impl, counts, KeySets.WithMisses(n => KeySets.UniformInts(n, s.Seed), 1_024, misses: false)); break;
            case "small-long-uniform": Reads(scenario, impl, counts, KeySets.WithMisses(n => KeySets.UniformLongs(n, s.Seed), 1_024, misses: false)); break;
            case "get-str-samecmp": Reads(scenario, impl, counts, (KeySets.Strings(s.Size), null), KeySets.SameStringComparer.Instance); break;
            case "insert-long-mirrored": Fill(scenario, impl, counts, KeySets.MirroredLongs(2_048)); break;
            case "grow-int": Fill(scenario, impl, counts, KeySets.UniformInts(100_000, s.Seed)); break;
            case "grow-str": Fill(scenario, impl, counts, KeySets.Strings(100_000)); break;
            case "mixed-str":
            {
                var keys = KeySets.Strings(s.Size);
                counts.Each((t, rs) => MapFactory.With<string, object, Measurement, MixedScenario>(
                    impl, new MixedScenario(scenario, impl, t, rs, keys)));
                break;
            }

            case "churn-str":
                counts.Each((t, rs) => MapFactory.With<string, object, Measurement, ChurnScenario>(
                    impl, new ChurnScenario(scenario, impl, t, rs)));
                break;
            case "churn-long":
                counts.Each((t, rs) => MapFactory.With<long, object, Measurement, ChurnLongScenario>(
                    impl, new ChurnLongScenario(scenario, impl, t, rs)));
                break;
            case "readd-str":
            {
                var keys = KeySets.Strings(Math.Max(1, s.Size / 10));
                counts.Each((t, rs) => MapFactory.With<string, object, Measurement, ReaddScenario>(
                    impl, new ReaddScenario(scenario, impl, t, rs, keys)));
                break;
            }

            case "enum-write":
            {
                var keys = KeySets.Strings(s.Size);
                counts.Each((t, rs) => MapFactory.With<string, object, Measurement, EnumWriteScenario>(
                    impl, new EnumWriteScenario(scenario, impl, t, rs, keys)));
                break;
            }

            default:
                throw new ArgumentException($"Unknown scenario '{scenario}'. Run 'list' to see the catalogue.", nameof(scenario));
        }
    }

    internal static readonly object Value = new();

    private static void Reads<TKey>(
        string scenario, string impl, Counts counts, (TKey[] Insert, TKey[]? Lookup) keys, IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
        => MapFactory.With<TKey, object, bool, GetScenario<TKey>>(
            impl, new GetScenario<TKey>(scenario, impl, counts, keys.Insert, keys.Lookup), comparer);

    private static void Fill<TKey>(string scenario, string impl, Counts counts, TKey[] keys)
        where TKey : notnull
        => counts.Each((t, rs) => FillScenario.Run(scenario, impl, t, rs, keys));
}

/// <summary>
/// The thread counts one process measures, in order. The first count gets the full warm-up,
/// which covers JIT tiering; later counts only need their threads and caches to settle, so they
/// get <see cref="RunSettings.RewarmMs"/>. A full collection separates consecutive counts.
/// </summary>
internal sealed class Counts(int[] threadCounts, RunSettings s, Action<Measurement> emit)
{
    public void Each(Func<int, RunSettings, Measurement> run)
    {
        for (int i = 0; i < threadCounts.Length; i++)
        {
            if (i > 0)
            {
                Settle();
            }

            emit(run(threadCounts[i], i == 0 ? s : s.Rewarmed));
        }
    }

    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

internal readonly struct GetScenario<TKey>(string name, string impl, Counts counts, TKey[] keys, TKey[]? lookups = null)
    : IMapConsumer<TKey, object, bool> where TKey : notnull
{
    public bool Consume<TMap>(TMap map) where TMap : struct, IMap<TKey, object>
    {
        // Memory the filled map holds beyond the key arrays: table, nodes and value boxes.
        // A change that alters resize timing alters capacity and load factor, so it is recorded.
        long before = GC.GetTotalMemory(forceFullCollection: true);
        foreach (var k in keys)
        {
            map.TryAdd(k, Scenarios.Value);
        }

        long after = GC.GetTotalMemory(forceFullCollection: true);

        var keySet = lookups ?? keys;
        double expectedHits = (double)keys.Length / keySet.Length;
        string scenario = name, implementation = impl;

        // Lookups do not change the map, so every thread count measures the same filled table.
        counts.Each((threads, s) =>
        {
            int seed = s.Seed;
            var result = ThroughputRun.Execute(scenario, implementation, threads, s, (tid, run) =>
            {
                int[] order = KeySets.Indices(keySet.Length, seed + tid);
                int mask = order.Length - 1;
                int j = 0;
                long ops = 0, hits = 0;
                while (!run.Stopped)
                {
                    bool measuring = run.Measuring;
                    int batchHits = 0;
                    for (int b = 0; b < ThroughputRun.Batch; b++)
                    {
                        if (map.TryGet(keySet[order[j++ & mask]], out _))
                        {
                            batchHits++;
                        }
                    }

                    if (measuring)
                    {
                        ops += ThroughputRun.Batch;
                        hits += batchHits;
                    }
                }

                run.Report(tid, ops, hits);
            });

            result.Extra["table_mb"] = (after - before) / (1024.0 * 1024.0);
            result.Extra["expected_hit_ratio"] = expectedHits;
            return result;
        });

        return true;
    }
}

internal readonly struct MixedScenario(string name, string impl, int threads, RunSettings s, string[] keys)
    : IMapConsumer<string, object, Measurement>
{
    public Measurement Consume<TMap>(TMap map) where TMap : struct, IMap<string, object>
    {
        foreach (var k in keys)
        {
            map.TryAdd(k, Scenarios.Value);
        }

        var keySet = keys;
        int seed = s.Seed;
        return ThroughputRun.Execute(name, impl, threads, s, (tid, run) =>
        {
            int[] order = KeySets.Indices(keySet.Length, seed + tid);
            int mask = order.Length - 1;
            int j = 0;
            long ops = 0, writes = 0;
            while (!run.Stopped)
            {
                bool measuring = run.Measuring;
                int batchWrites = 0;
                for (int b = 0; b < ThroughputRun.Batch; b++)
                {
                    // Cadence on the running counter, not the batch index: 64 is not a multiple
                    // of 10, so b % 10 would give 7 writes per 64 operations (10.9%).
                    bool write = j % 10 == 0;
                    var key = keySet[order[j++ & mask]];
                    if (write)
                    {
                        map.Set(key, Scenarios.Value);
                        batchWrites++;
                    }
                    else
                    {
                        map.TryGet(key, out _);
                    }
                }

                if (measuring)
                {
                    ops += ThroughputRun.Batch;
                    writes += batchWrites;
                }
            }

            run.Report(tid, ops, writes);
        });
    }
}

internal readonly struct ChurnScenario(string name, string impl, int threads, RunSettings s)
    : IMapConsumer<string, object, Measurement>
{
    public Measurement Consume<TMap>(TMap map) where TMap : struct, IMap<string, object>
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        int livePerThread = Math.Max(1, s.Live / threads);

        var result = ThroughputRun.Execute(name, impl, threads, s, (tid, run) =>
        {
            var window = new Queue<string>(livePerThread + 1);
            long n = 0, ops = 0;
            while (!run.Stopped)
            {
                bool measuring = run.Measuring;
                int batchOps = 0;
                for (int b = 0; b < ThroughputRun.Batch / 2; b++)
                {
                    // Keys are never reused: models connection or session identifiers.
                    var key = string.Create(CultureInfo.InvariantCulture, $"{tid:x4}-{n++:x12}");
                    map.TryAdd(key, Scenarios.Value);
                    window.Enqueue(key);
                    batchOps++;
                    if (window.Count > livePerThread)
                    {
                        map.TryRemove(window.Dequeue());
                        batchOps++;
                    }
                }

                // Count the calls made: no removes happen while the live window is filling.
                if (measuring)
                {
                    ops += batchOps;
                }
            }

            run.Report(tid, ops);
        });

        return ChurnReport.Complete<string, TMap>(map, before, result);
    }
}

internal readonly struct ChurnLongScenario(string name, string impl, int threads, RunSettings s)
    : IMapConsumer<long, object, Measurement>
{
    public Measurement Consume<TMap>(TMap map) where TMap : struct, IMap<long, object>
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        int livePerThread = Math.Max(1, s.Live / threads);

        var result = ThroughputRun.Execute(name, impl, threads, s, (tid, run) =>
        {
            var window = new Queue<long>(livePerThread + 1);
            // Thread id in the top bits keeps keys unique across threads; n never reaches 2^40.
            long n = ((long)tid << 40) + 1, ops = 0;
            while (!run.Stopped)
            {
                bool measuring = run.Measuring;
                int batchOps = 0;
                for (int b = 0; b < ThroughputRun.Batch / 2; b++)
                {
                    long key = n++;
                    map.TryAdd(key, Scenarios.Value);
                    window.Enqueue(key);
                    batchOps++;
                    if (window.Count > livePerThread)
                    {
                        map.TryRemove(window.Dequeue());
                        batchOps++;
                    }
                }

                // Count the calls made: no removes happen while the live window is filling.
                if (measuring)
                {
                    ops += batchOps;
                }
            }

            run.Report(tid, ops);
        });

        return ChurnReport.Complete<long, TMap>(map, before, result);
    }
}

internal static class ChurnReport
{
    /// <summary>
    /// Retained heap after the run, with the live set still in the map. Both implementations
    /// hold the same live keys, so the difference is table and node overhead.
    /// </summary>
    public static Measurement Complete<TKey, TMap>(TMap map, long before, Measurement result)
        where TKey : notnull
        where TMap : struct, IMap<TKey, object>
    {
        long after = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();
        int enumerated = 0;
        foreach (var _ in map.Pairs)
        {
            enumerated++;
        }

        result.Extra["retained_mb"] = (after - before) / (1024.0 * 1024.0);
        result.Extra["post_churn_enumerate_ms"] = sw.Elapsed.TotalMilliseconds;
        result.Extra["live_count"] = enumerated;
        return result;
    }
}

internal readonly struct ReaddScenario(string name, string impl, int threads, RunSettings s, string[] keys)
    : IMapConsumer<string, object, Measurement>
{
    public Measurement Consume<TMap>(TMap map) where TMap : struct, IMap<string, object>
    {
        foreach (var k in keys)
        {
            map.TryAdd(k, Scenarios.Value);
        }

        var keySet = keys;
        int seed = s.Seed;
        long before = GC.GetTotalMemory(forceFullCollection: true);
        var result = ThroughputRun.Execute(name, impl, threads, s, (tid, run) =>
        {
            int[] order = KeySets.Indices(keySet.Length, seed + tid);
            int mask = order.Length - 1;
            int j = 0;
            long ops = 0;
            while (!run.Stopped)
            {
                bool measuring = run.Measuring;
                for (int b = 0; b < ThroughputRun.Batch / 2; b++)
                {
                    // The same key leaves and returns, as with sessions that reconnect.
                    var key = keySet[order[j++ & mask]];
                    map.TryRemove(key);
                    map.TryAdd(key, Scenarios.Value);
                }

                if (measuring)
                {
                    ops += ThroughputRun.Batch;
                }
            }

            run.Report(tid, ops);
        });

        return ChurnReport.Complete<string, TMap>(map, before, result);
    }
}

internal readonly struct EnumWriteScenario(string name, string impl, int threads, RunSettings s, string[] keys)
    : IMapConsumer<string, object, Measurement>
{
    private const int Chunk = 1024;

    public Measurement Consume<TMap>(TMap map) where TMap : struct, IMap<string, object>
    {
        foreach (var k in keys)
        {
            map.TryAdd(k, Scenarios.Value);
        }

        var keySet = keys;
        int seed = s.Seed;

        // Thread 0 enumerates; the primary metric is items enumerated per second.
        // Other threads overwrite existing keys; their rate is reported as aux_per_sec.
        return ThroughputRun.Execute(name, impl, threads, s, (tid, run) =>
        {
            if (tid == 0)
            {
                long items = 0;
                while (!run.Stopped)
                {
                    int chunk = 0;
                    foreach (var _ in map.Pairs)
                    {
                        if (++chunk < Chunk)
                        {
                            continue;
                        }

                        if (run.Stopped)
                        {
                            break;
                        }

                        if (run.Measuring)
                        {
                            items += Chunk;
                        }

                        chunk = 0;
                    }

                    if (run.Measuring)
                    {
                        items += chunk;
                    }
                }

                run.Report(tid, items);
                return;
            }

            int[] order = KeySets.Indices(keySet.Length, seed + tid);
            int mask = order.Length - 1;
            int j = 0;
            long writes = 0;
            while (!run.Stopped)
            {
                bool measuring = run.Measuring;
                for (int b = 0; b < ThroughputRun.Batch; b++)
                {
                    map.Set(keySet[order[j++ & mask]], Scenarios.Value);
                }

                if (measuring)
                {
                    writes += ThroughputRun.Batch;
                }
            }

            run.Report(tid, 0, writes);
        });
    }
}

/// <summary>
/// Each thread repeatedly fills a fresh dictionary with the same key set, which measures the
/// insert path including resizes and table copies. The memory one filled table holds is recorded
/// once before the run; a degenerate hash shows up there as well as in the rate.
/// </summary>
internal static class FillScenario
{
    public static Measurement Run<TKey>(string name, string impl, int threads, RunSettings s, TKey[] keys)
        where TKey : notnull
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        var sample = Fill(impl, keys, null).Map;
        long after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(sample);

        var result = ThroughputRun.Execute(name, impl, threads, s, (tid, run) =>
        {
            long ops = 0;
            while (!run.Stopped)
            {
                ops += Fill(impl, keys, run).Measured;
            }

            run.Report(tid, ops);
        });

        result.Extra["table_mb"] = (after - before) / (1024.0 * 1024.0);
        result.Extra["keys_per_table"] = keys.Length;
        return result;
    }

    private static (object Map, long Measured) Fill<TKey>(string impl, TKey[] keys, ThroughputRun? run)
        where TKey : notnull
    {
        if (impl == "nb")
        {
            var d = new NonBlocking.ConcurrentDictionary<TKey, object>();
            return (d, FillMap(new NbMap<TKey, object>(d), keys, run));
        }

        var b = new System.Collections.Concurrent.ConcurrentDictionary<TKey, object>();
        return (b, FillMap(new BclMap<TKey, object>(b), keys, run));
    }

    private static long FillMap<TKey, TMap>(TMap map, TKey[] keys, ThroughputRun? run)
        where TKey : notnull
        where TMap : struct, IMap<TKey, object>
    {
        long measured = 0;
        for (int i = 0; i < keys.Length; i += ThroughputRun.Batch)
        {
            // Phase is sampled per batch, as in the other scenarios, so a slow fill cannot
            // count work done after the measured window closed.
            bool measuring = run?.Measuring ?? false;
            int end = Math.Min(i + ThroughputRun.Batch, keys.Length);
            for (int j = i; j < end; j++)
            {
                map.TryAdd(keys[j], Scenarios.Value);
            }

            if (measuring)
            {
                measured += end - i;
            }

            if (run?.Stopped == true)
            {
                break;
            }
        }

        return measured;
    }
}
