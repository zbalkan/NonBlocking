using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace NonBlocking.Bench;

/// <summary>
/// Coordinates one timed run: all workers start together, run unmeasured through the warm-up
/// (JIT tiering, cache warm-up), then count operations only while <see cref="Measuring"/> is set.
/// Workers poll the phase once per batch, so the boundary error is at most one batch per thread.
/// </summary>
internal sealed class ThroughputRun
{
    public const int Batch = 64;

    private const int Warmup = 0;
    private const int Measure = 1;
    private const int Stop = 2;

    private volatile int _phase = Warmup;
    private long _warmupStart;
    private long _measureStart;
    private readonly long[] _counts;
    private readonly long[] _aux;

    private ThroughputRun(int threads)
    {
        _counts = new long[threads];
        _aux = new long[threads];
    }

    public bool Stopped => _phase == Stop;

    public bool Measuring => _phase == Measure;

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> at the start of the current phase: the warm-up while
    /// warming up, the measured window once <see cref="Measuring"/> is set. Workloads that change
    /// over time key off this, so every implementation runs the same schedule in the window.
    /// </summary>
    public long PhaseStart => Measuring ? Volatile.Read(ref _measureStart) : Volatile.Read(ref _warmupStart);

    /// <summary>Workers call this once, after leaving their loop, with their own totals.</summary>
    public void Report(int threadId, long measuredOps, long auxiliary = 0)
    {
        _counts[threadId] = measuredOps;
        _aux[threadId] = auxiliary;
    }

    public static Measurement Execute(
        string scenario, string impl, int threads, RunSettings settings, Action<int, ThroughputRun> worker)
    {
        var run = new ThroughputRun(threads);
        using var ready = new CountdownEvent(threads);
        using var go = new ManualResetEventSlim(false);

        var pool = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            pool[t] = new Thread(() =>
            {
                ready.Signal();
                go.Wait();
                worker(id, run);
            })
            { IsBackground = true, Name = $"bench-{id}" };
            pool[t].Start();
        }

        ready.Wait();
        run._warmupStart = Stopwatch.GetTimestamp();
        go.Set();
        Thread.Sleep(settings.WarmupMs);

        var sw = Stopwatch.StartNew();
        // Written before the phase flips; the volatile write of _phase publishes it.
        run._measureStart = Stopwatch.GetTimestamp();
        run._phase = Measure;
        Thread.Sleep(settings.DurationMs);
        run._phase = Stop;
        double seconds = sw.Elapsed.TotalSeconds;

        foreach (var thread in pool)
        {
            thread.Join();
        }

        long ops = 0;
        long aux = 0;
        for (int t = 0; t < threads; t++)
        {
            ops += run._counts[t];
            aux += run._aux[t];
        }

        return new Measurement(scenario, impl, threads, ops, seconds, ops / seconds)
        {
            Extra = new Dictionary<string, double> { ["aux_per_sec"] = aux / seconds },
        };
    }
}

internal sealed record RunSettings(int WarmupMs, int RewarmMs, int DurationMs, int Size, int Live, int Seed)
{
    /// <summary>Settings for every thread count after the first in a process: --rewarm-ms as given.</summary>
    public RunSettings Rewarmed => this with { WarmupMs = RewarmMs };
}

internal sealed record Measurement(
    string Scenario, string Impl, int Threads, long Ops, double Seconds, double OpsPerSec)
{
    public Dictionary<string, double> Extra { get; init; } = new();
}
