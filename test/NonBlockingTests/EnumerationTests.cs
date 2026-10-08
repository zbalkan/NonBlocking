// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace NonBlockingTests
{
    public class EnumerationTests
    {
        // Enumeration reads slots directly and resolves through a lookup only the slots that are
        // being copied or swept. A key whose slot was already copied to a newer table must still
        // be found there, so writers keep the table resizing while the enumerator runs: every
        // stable key must appear exactly once in every enumeration, with its own value.
        [Fact]
        public static void EnumerationDuringResizeYieldsEveryStableKeyOnce()
        {
            const int StableKeys = 1_000;
            var d = new NonBlocking.ConcurrentDictionary<string, string>();
            for (int i = 0; i < StableKeys; i++)
            {
                d["stable-" + i] = "v-" + i;
            }

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var writers = new Thread[Math.Max(2, Environment.ProcessorCount)];
            for (int t = 0; t < writers.Length; t++)
            {
                int id = t;
                writers[t] = new Thread(() =>
                {
                    // Unique keys added and removed again keep the table growing and copying.
                    var window = new Queue<string>();
                    for (long n = 0; !stop.IsCancellationRequested; n++)
                    {
                        var key = "churn-" + id + "-" + n;
                        d.TryAdd(key, key);
                        window.Enqueue(key);
                        if (window.Count > 256)
                        {
                            d.TryRemove(window.Dequeue(), out _);
                        }
                    }
                });
                writers[t].Start();
            }

            int enumerations = 0;
            string problem = null;
            var seen = new HashSet<string>();
            while (!stop.IsCancellationRequested && problem == null)
            {
                seen.Clear();
                foreach (var kv in d)
                {
                    if (!kv.Key.StartsWith("stable-", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!seen.Add(kv.Key))
                    {
                        problem = $"{kv.Key} yielded twice in one enumeration";
                    }
                    else if (kv.Value != "v-" + kv.Key.Substring("stable-".Length))
                    {
                        problem = $"{kv.Key} enumerated with value {kv.Value}";
                    }
                }

                if (problem == null && seen.Count != StableKeys)
                {
                    problem = $"enumeration {enumerations} yielded {seen.Count} of {StableKeys} stable keys";
                }

                enumerations++;
            }

            stop.Cancel();
            foreach (var w in writers)
            {
                w.Join();
            }

            Assert.True(problem == null, problem);
            Assert.True(enumerations > 0, "no enumeration completed");
        }
    }
}
