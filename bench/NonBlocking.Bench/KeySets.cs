using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace NonBlocking.Bench;

/// <summary>Deterministic key sets; the same seed yields the same keys on every machine.</summary>
internal static class KeySets
{
    // Large enough that a thread's lookups spread over a 1M-key table instead of a cache-resident
    // subset; with 1 << 16 positions each thread touched at most 6.5% of the keys.
    private const int IndexWindow = 1 << 20;

    public static string[] Strings(int count)
    {
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = "key-" + i.ToString("D12", CultureInfo.InvariantCulture);
        }

        return keys;
    }

    /// <summary>Distinct ints in [1, 2^30), so bits 30 and 31 carry no information.</summary>
    public static int[] UniformInts(int count, int seed)
    {
        var rnd = new Random(seed);
        var set = new HashSet<int>(count);
        while (set.Count < count)
        {
            set.Add(rnd.Next(1, 1 << 30));
        }

        var keys = new int[count];
        set.CopyTo(keys);
        return keys;
    }

    /// <summary>
    /// Same key count as <see cref="UniformInts"/>, built from ceil(count / 4) bases, each OR-ed with
    /// the four combinations of bits 30 and 31 (the last group is partial when count is not a
    /// multiple of four). With the default comparer the full hash is
    /// key | 0xC0000000, so each group of four shares one hash. IPv4 addresses that differ only
    /// in the top two bits of the first octet (10.x, 74.x, 138.x, 202.x) have this shape.
    /// </summary>
    public static int[] TopBitVariantInts(int count, int seed)
    {
        // Ceiling division: when count is not a multiple of four, the last group is partial,
        // so the scenario gets exactly the --size keys it was asked for.
        var bases = UniformInts((count + 3) / 4, seed);
        var keys = new int[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = bases[i / 4] | TopBits[i % 4];
        }

        return keys;
    }

    private static readonly int[] TopBits = { 0, 0x4000_0000, unchecked((int)0x8000_0000), unchecked((int)0xC000_0000) };

    /// <summary>Distinct longs in [1, 2^62), so bits 62 and 63 carry no information.</summary>
    public static long[] UniformLongs(int count, int seed)
    {
        var rnd = new Random(seed);
        var set = new HashSet<long>(count);
        while (set.Count < count)
        {
            set.Add(rnd.NextInt64(1, 1L << 62));
        }

        var keys = new long[count];
        set.CopyTo(keys);
        return keys;
    }

    /// <summary>
    /// The 64-bit counterpart of <see cref="TopBitVariantInts"/>: groups of keys that differ only
    /// in bits 62 and 63. long.GetHashCode folds the halves (lo ^ hi), which moves those bits to
    /// 30 and 31 of the fold, where the default-comparer hash tag overwrites them.
    /// </summary>
    public static long[] TopBitVariantLongs(int count, int seed)
    {
        var bases = UniformLongs((count + 3) / 4, seed);
        var keys = new long[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = bases[i / 4] | ((long)(i % 4) << 62);
        }

        return keys;
    }

    /// <summary>
    /// Keys whose high and low halves are equal (i &lt;&lt; 32 | i). long.GetHashCode folds each
    /// of them to 0, so with the default comparer every key gets the same full hash.
    /// </summary>
    public static long[] MirroredLongs(int count)
    {
        var keys = new long[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = ((long)(i + 1) << 32) | (uint)(i + 1);
        }

        return keys;
    }

    /// <summary>
    /// Splits a generated set into the keys to insert and the keys to look up. When
    /// <paramref name="misses"/> is false the lookups are the inserted keys. When it is true the
    /// generated set is twice as long: its first half is inserted and the lookups cover the whole
    /// set, so about half of them are for keys from the same distribution that were never
    /// inserted (for top-bit sets the split falls on a group boundary).
    /// </summary>
    public static (T[] Insert, T[] Lookup) WithMisses<T>(Func<int, T[]> generate, int count, bool misses)
    {
        if (!misses)
        {
            var keys = generate(count);
            return (keys, keys);
        }

        var all = generate(count * 2);
        return (all[..count], all);
    }

    /// <summary>
    /// Gives both implementations the same randomized ordinal string hashing. Passed explicitly,
    /// it keeps the BCL type from substituting its non-randomized comparer, so a string
    /// comparison measures the two tables rather than two hash functions.
    /// </summary>
    public sealed class SameStringComparer : IEqualityComparer<string>
    {
        public static readonly SameStringComparer Instance = new();

        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        public int GetHashCode(string obj) => obj.GetHashCode();
    }

    /// <summary>
    /// A per-thread window of random positions into a key array. The power-of-two length lets
    /// workers wrap with a mask instead of calling a random generator inside the measured loop.
    /// </summary>
    public static int[] Indices(int bound, int seed)
    {
        int length = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Min(IndexWindow, Math.Max(bound, 1)));
        var rnd = new Random(seed);
        var order = new int[length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = rnd.Next(bound);
        }

        return order;
    }
}
