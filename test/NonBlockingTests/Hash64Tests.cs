// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace NonBlockingTests
{
    // 64-bit keys with the default comparer. A hash of lo ^ hi gives every key with equal halves
    // the same hash and packed pairs only a few, which made the table grow far past the key count.
    public class Hash64Tests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Fact]
        public static void KeysWithEqualHalvesKeepTheTableSmall()
        {
            var d = new NonBlocking.ConcurrentDictionary<long, int>();
            for (int i = 1; i <= 2048; i++)
            {
                Assert.True(d.TryAdd(((long)i << 32) | (uint)i, i));
            }

            AssertAllPresent(d, i => ((long)i << 32) | (uint)i, 2048);
            AssertTableAtMost(d, 16_384);
        }

        [Fact]
        public static void PackedPairsKeepTheTableSmall()
        {
            var d = new NonBlocking.ConcurrentDictionary<ulong, int>();
            for (int x = 0; x < 64; x++)
            {
                for (int y = 0; y < 64; y++)
                {
                    Assert.True(d.TryAdd(((ulong)x << 32) | (uint)y, x * 64 + y));
                }
            }

            for (int x = 0; x < 64; x++)
            {
                for (int y = 0; y < 64; y++)
                {
                    Assert.True(d.TryGetValue(((ulong)x << 32) | (uint)y, out int v));
                    Assert.Equal(x * 64 + y, v);
                }
            }

            AssertTableAtMost(d, 16_384);
        }

        [Fact]
        public static void NativeIntKeysWithEqualHalvesKeepTheTableSmall()
        {
            if (!Environment.Is64BitProcess)
            {
                return;
            }

            var d = new NonBlocking.ConcurrentDictionary<nint, int>();
            for (int i = 1; i <= 2048; i++)
            {
                Assert.True(d.TryAdd((nint)(((long)i << 32) | (uint)i), i));
            }

            for (int i = 1; i <= 2048; i++)
            {
                Assert.True(d.TryGetValue((nint)(((long)i << 32) | (uint)i), out int v));
                Assert.Equal(i, v);
            }

            AssertTableAtMost(d, 16_384);
        }

        [Fact]
        public static void ExplicitDefaultComparerKeepsTheTableSmall()
        {
            var d = new NonBlocking.ConcurrentDictionary<long, int>(EqualityComparer<long>.Default);
            var u = new NonBlocking.ConcurrentDictionary<ulong, int>(EqualityComparer<ulong>.Default);
            for (int i = 1; i <= 2048; i++)
            {
                Assert.True(d.TryAdd(((long)i << 32) | (uint)i, i));
                Assert.True(u.TryAdd(((ulong)i << 32) | (uint)i, i));
            }

            AssertAllPresent(d, i => ((long)i << 32) | (uint)i, 2048);
            Assert.Equal(2048, u.Count);
            AssertTableAtMost(d, 16_384);
            AssertTableAtMost(u, 16_384);
        }

        [Fact]
        public static void NativeIntExplicitDefaultComparerKeepsTheTableSmall()
        {
            if (!Environment.Is64BitProcess)
            {
                return;
            }

            var d = new NonBlocking.ConcurrentDictionary<nint, int>(EqualityComparer<nint>.Default);
            var u = new NonBlocking.ConcurrentDictionary<nuint, int>(EqualityComparer<nuint>.Default);
            for (int i = 1; i <= 2048; i++)
            {
                Assert.True(d.TryAdd((nint)(((long)i << 32) | (uint)i), i));
                Assert.True(u.TryAdd((nuint)(((ulong)i << 32) | (uint)i), i));
            }

            for (int i = 1; i <= 2048; i++)
            {
                Assert.True(d.TryGetValue((nint)(((long)i << 32) | (uint)i), out int v));
                Assert.Equal(i, v);
                Assert.True(u.TryGetValue((nuint)(((ulong)i << 32) | (uint)i), out v));
                Assert.Equal(i, v);
            }

            AssertTableAtMost(d, 16_384);
            AssertTableAtMost(u, 16_384);
        }

        // Keys that differ only in bits 62 and 63 differed only in the bits the tag overwrites, so
        // every such group shared one hash. Too few keys collide to force a resize, so compare the
        // hashes the table computes.
        [Fact]
        public static void KeysDifferingOnlyInTheTopBitsGetDistinctHashes()
        {
            var d = new NonBlocking.ConcurrentDictionary<long, int>();
            for (long b = 1; b <= 1024; b++)
            {
                var hashes = new HashSet<int>();
                for (long t = 0; t < 4; t++)
                {
                    long key = (b * 0x10001) | (t << 62);
                    Assert.True(d.TryAdd(key, (int)b));
                    hashes.Add(TableHash(d, key));
                }

                Assert.True(hashes.Count == 4, $"the four variants of {b} got only {hashes.Count} distinct hash(es)");
            }

            Assert.Equal(4096, d.Count);
        }

        // A comparer other than the default keeps its own equality and hash; the routing change only
        // concerns the default comparer.
        [Fact]
        public static void CustomComparerIsRespected()
        {
            var comparer = new ModuloComparer(1000);
            var d = new NonBlocking.ConcurrentDictionary<long, int>(comparer);
            var u = new NonBlocking.ConcurrentDictionary<ulong, int>(new UnsignedModuloComparer(1000));
            Assert.True(d.TryAdd(5, 1));
            Assert.False(d.TryAdd(1005, 2));
            Assert.True(d.TryGetValue(2005, out int v));
            Assert.Equal(1, v);
            Assert.True(u.TryAdd(5, 1));
            Assert.False(u.TryAdd(1005, 2));
            Assert.True(u.TryGetValue(2005, out v));
            Assert.Equal(1, v);
            Assert.Equal(1, d.Count);
            Assert.Equal(1, u.Count);
            Assert.True(comparer.HashCalls > 0);
        }

        private sealed class ModuloComparer : IEqualityComparer<long>
        {
            private readonly long _m;
            public int HashCalls;

            public ModuloComparer(long m) => _m = m;

            public bool Equals(long x, long y) => x % _m == y % _m;

            public int GetHashCode(long x)
            {
                HashCalls++;
                return (int)(x % _m);
            }
        }

        private sealed class UnsignedModuloComparer : IEqualityComparer<ulong>
        {
            private readonly ulong _m;

            public UnsignedModuloComparer(ulong m) => _m = m;

            public bool Equals(ulong x, ulong y) => x % _m == y % _m;

            public int GetHashCode(ulong x) => (int)(x % _m);
        }

        [Fact]
        public static void EdgeKeysRoundTrip()
        {
            long[] keys = { 0, 1, -1, 2, -2, 1L << 30, 1L << 31, 1L << 32, -(1L << 32), 1L << 62, long.MinValue, long.MaxValue };
            var d = new NonBlocking.ConcurrentDictionary<long, long>();
            var u = new NonBlocking.ConcurrentDictionary<ulong, long>();
            foreach (long k in keys)
            {
                Assert.True(d.TryAdd(k, k));
                Assert.True(u.TryAdd(unchecked((ulong)k), k));
            }

            foreach (long k in keys)
            {
                Assert.True(d.TryGetValue(k, out long v));
                Assert.Equal(k, v);
                Assert.True(u.TryGetValue(unchecked((ulong)k), out v));
                Assert.Equal(k, v);
                Assert.True(d.TryRemove(k, out _));
                Assert.False(d.ContainsKey(k));
                Assert.True(d.TryAdd(k, ~k));
                Assert.True(d.TryGetValue(k, out v));
                Assert.Equal(~k, v);
            }

            Assert.Equal(keys.Length, d.Count);
            Assert.Equal(keys.Length, u.Count);
        }

        // The hash the dictionary's current table computes for a key.
        private static int TableHash(object dictionary, long key)
        {
            object table = FindField(dictionary.GetType(), "_table").GetValue(dictionary);
            for (Type t = table.GetType(); t != null; t = t.BaseType)
            {
                MethodInfo hash = t.GetMethod("hash", Any | BindingFlags.DeclaredOnly, null, new[] { typeof(long) }, null);
                if (hash != null)
                {
                    return (int)hash.Invoke(table, new object[] { key });
                }
            }

            throw new MissingMethodException("hash");
        }

        private static void AssertAllPresent(NonBlocking.ConcurrentDictionary<long, int> d, Func<int, long> key, int count)
        {
            for (int i = 1; i <= count; i++)
            {
                Assert.True(d.TryGetValue(key(i), out int v));
                Assert.Equal(i, v);
            }

            Assert.Equal(count, d.Count);
        }

        private static void AssertTableAtMost(object dictionary, int slots)
        {
            // A resize publishes the new table only once its copy is done, so follow it to the newest.
            object table = FindField(dictionary.GetType(), "_table").GetValue(dictionary);
            for (object next; (next = FindField(table.GetType(), "_newTable").GetValue(table)) != null;)
            {
                table = next;
            }

            int length = ((Array)FindField(table.GetType(), "_entries").GetValue(table)).Length;
            Assert.True(length <= slots, $"the table has {length} slots, expected at most {slots}");
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
