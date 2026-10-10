// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Xunit;

namespace NonBlockingTests
{
    // The sweeper runs after a garbage collection that follows a removal, and retires the removed
    // keys so the table stops referencing them.
    public class SweepTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // A key removed, swept and added back while an enumeration is part-way through the table
        // must not be yielded a second time. Each round enumerates up to the key, removes it, waits
        // for the sweeper to retire it, adds it back and finishes the enumeration.
        [Fact]
        public static void KeyReaddedAfterSweepIsNotEnumeratedTwice()
        {
            const int Keys = 16;
            int swept = 0;
            var duplicates = new List<string>();

            for (int round = 0; round < 2 * Keys; round++)
            {
                var d = new NonBlocking.ConcurrentDictionary<string, string>();
                for (int i = 0; i < Keys; i++)
                {
                    d["k" + i] = "v";
                }

                string key = "k" + (round % Keys);
                int yielded = 0;
                using var e = d.GetEnumerator();
                while (e.MoveNext())
                {
                    if (e.Current.Key == key)
                    {
                        yielded++;
                        break;
                    }
                }

                // The table is quiescent, so the first pass must reach the key.
                Assert.Equal(1, yielded);
                Assert.True(d.TryRemove(key, out _));
                if (!WaitForSweep(d, key))
                {
                    continue;
                }

                swept++;
                d[key] = "again";
                while (e.MoveNext())
                {
                    if (e.Current.Key == key)
                    {
                        yielded++;
                    }
                }

                if (yielded > 1)
                {
                    duplicates.Add(key);
                }
            }

            // Without a sweep the test checks nothing, so require that sweeps happened.
            Assert.True(swept > 0, "the sweeper never ran");
            Assert.True(duplicates.Count == 0,
                $"{duplicates.Count} of {swept} swept keys were yielded twice: {string.Join(", ", duplicates)}");
        }

        // The sweeper is armed by the removal and runs from a finalizer, so collect until the
        // current table no longer references the key, or give up.
        private static bool WaitForSweep(object dictionary, string key)
        {
            for (int i = 0; i < 100; i++)
            {
                if (!CurrentTableHolds(dictionary, key))
                {
                    return true;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(10);
            }

            return false;
        }

        private static bool CurrentTableHolds(object dictionary, string key)
        {
            object table = FindField(dictionary.GetType(), "_table").GetValue(dictionary);
            var entries = (Array)FindField(table.GetType(), "_entries").GetValue(table);
            for (int i = 0; i < entries.Length; i++)
            {
                object entry = entries.GetValue(i);
                if (Equals(entry.GetType().GetField("key", Any).GetValue(entry), key))
                {
                    return true;
                }
            }

            return false;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }
            }

            throw new MissingFieldException(name);
        }
    }
}
