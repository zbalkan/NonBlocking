using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NonBlocking.Bench;

/// <summary>
/// Multi-threaded throughput harness for NonBlocking.ConcurrentDictionary versus the BCL type.
/// It is a plain console program rather than BenchmarkDotNet because the quantity of interest
/// is aggregate throughput of N concurrent threads, which BenchmarkDotNet does not model; the
/// statistics (repeated trials, interleaving, confidence intervals) live in run_bench.py.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // A misspelt option must fail rather than silently fall back to a default and
    // produce normal-looking results for a run that is not the one requested.
    private static readonly Dictionary<string, HashSet<string>> AllowedOptions = new(StringComparer.Ordinal)
    {
        ["list"] = new(StringComparer.Ordinal),
        ["env"] = new(StringComparer.Ordinal),
        ["run"] = new(StringComparer.Ordinal)
        {
            "scenario", "impl", "threads", "warmup-ms", "rewarm-ms", "duration-ms", "size", "live", "seed",
        },
        ["stress"] = new(StringComparer.Ordinal) { "seconds", "threads" },
    };

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            string command = args[0];
            if (!AllowedOptions.TryGetValue(command, out var allowed))
            {
                return Usage();
            }

            var opts = ParseOptions(args.Skip(1), allowed);
            return command switch
            {
                "list" => List(),
                "env" => Env(),
                "run" => RunScenario(opts),
                _ => RunStress(opts),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int RunStress(Dictionary<string, string> opts)
    {
        int seconds = Int(opts, "seconds", 60);
        int threads = Int(opts, "threads", Environment.ProcessorCount);

        // The deterministic checks run first; the timed stress runs regardless, so one
        // failure does not hide another.
        int failedChecks = SweptSlotChecks.Run();
        int stress = Stress.Run(seconds, threads);
        return failedChecks > 0 ? 1 : stress;
    }

    private static int RunScenario(Dictionary<string, string> opts)
    {
        string scenario = Required(opts, "scenario");
        string impl = Required(opts, "impl");
        var settings = new RunSettings(
            WarmupMs: Int(opts, "warmup-ms", 500),
            RewarmMs: Int(opts, "rewarm-ms", 200),
            DurationMs: Int(opts, "duration-ms", 1000),
            Size: Int(opts, "size", 1_000_000),
            Live: Int(opts, "live", 1_000),
            Seed: AnyInt(opts, "seed", 42));

        var threadCounts = opts.TryGetValue("threads", out var t)
            ? t.Split(',').Select(ParseThreadCount).ToArray()
            : new[] { Environment.ProcessorCount };

        // One JSON line per thread count, written as each completes, so a process that fails
        // part-way still leaves the counts it finished.
        Scenarios.Run(scenario, impl, threadCounts, settings, m =>
        {
            Console.WriteLine(JsonSerializer.Serialize(m, Json));
            Console.Out.Flush();
        });

        return 0;
    }

    private static int List()
    {
        foreach (var (name, description) in Scenarios.Descriptions)
        {
            Console.WriteLine($"{name,-18} {description}");
        }

        return 0;
    }

    private static int Env()
    {
        var env = new Dictionary<string, object>
        {
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processor_count"] = Environment.ProcessorCount,
            ["server_gc"] = GCSettings.IsServerGC,
            ["nonblocking_assembly"] = typeof(NonBlocking.ConcurrentDictionary<int, int>).Assembly.Location,
        };
        Console.WriteLine(JsonSerializer.Serialize(env, Json));
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            usage:
              NonBlocking.Bench list
              NonBlocking.Bench env
              NonBlocking.Bench run --scenario NAME --impl nb|bcl [--threads 1,2,4]
                                    [--warmup-ms 500] [--rewarm-ms 200] [--duration-ms 1000]
                                    [--size 1000000] [--live 1000] [--seed 42]
              NonBlocking.Bench stress [--seconds 60] [--threads N]
            'run' prints one JSON line per thread count on stdout. The first count warms
            up for --warmup-ms; later counts, with the code already compiled, for --rewarm-ms.
            'stress' runs deterministic swept-slot checks, then the timed stress; it exits 1
            if any check fails.
            """);
        return 2;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args, HashSet<string> allowed)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pending = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                pending = a[2..];
                if (!allowed.Contains(pending))
                {
                    throw new ArgumentException($"Unknown option '--{pending}'.");
                }

                opts[pending] = "";
            }
            else if (pending is not null)
            {
                opts[pending] = a;
                pending = null;
            }
            else
            {
                throw new ArgumentException($"Unexpected argument '{a}'.");
            }
        }

        return opts;
    }

    private static string Required(Dictionary<string, string> opts, string name)
        => opts.TryGetValue(name, out var v) && v.Length > 0
            ? v
            : throw new ArgumentException($"Missing --{name}.");

    private static int ParseThreadCount(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : throw new ArgumentException($"--threads expects comma-separated positive integers, got '{value}'.");

    private static int AnyInt(Dictionary<string, string> opts, string name, int fallback)
        => opts.TryGetValue(name, out var v)
            ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? n
                : throw new ArgumentException($"--{name} must be an integer, got '{v}'.")
            : fallback;

    private static int Int(Dictionary<string, string> opts, string name, int fallback)
        => opts.TryGetValue(name, out var v)
            ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
                ? n
                : throw new ArgumentException($"--{name} must be a positive integer, got '{v}'.")
            : fallback;
}
