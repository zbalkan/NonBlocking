using System.Collections.Generic;
using Bcl = System.Collections.Concurrent;
using Nb = NonBlocking;

namespace NonBlocking.Bench;

/// <summary>
/// The operations the scenarios exercise. Implemented by structs and consumed through a
/// generic constraint, so the JIT specialises each scenario per implementation and the
/// adapter adds no interface dispatch to the measured loop.
/// </summary>
internal interface IMap<TKey, TValue> where TKey : notnull
{
    bool TryGet(TKey key, out TValue value);
    bool TryAdd(TKey key, TValue value);
    bool TryRemove(TKey key);
    void Set(TKey key, TValue value);
    int Count { get; }
    IEnumerable<KeyValuePair<TKey, TValue>> Pairs { get; }
}

internal readonly struct NbMap<TKey, TValue> : IMap<TKey, TValue> where TKey : notnull
{
    private readonly Nb.ConcurrentDictionary<TKey, TValue> _d;

    public NbMap(Nb.ConcurrentDictionary<TKey, TValue> d) => _d = d;

    public bool TryGet(TKey key, out TValue value) => _d.TryGetValue(key, out value!);
    public bool TryAdd(TKey key, TValue value) => _d.TryAdd(key, value);
    public bool TryRemove(TKey key) => _d.TryRemove(key, out _);
    public void Set(TKey key, TValue value) => _d[key] = value;
    public int Count => _d.Count;
    public IEnumerable<KeyValuePair<TKey, TValue>> Pairs => _d;
}

internal readonly struct BclMap<TKey, TValue> : IMap<TKey, TValue> where TKey : notnull
{
    private readonly Bcl.ConcurrentDictionary<TKey, TValue> _d;

    public BclMap(Bcl.ConcurrentDictionary<TKey, TValue> d) => _d = d;

    public bool TryGet(TKey key, out TValue value) => _d.TryGetValue(key, out value!);
    public bool TryAdd(TKey key, TValue value) => _d.TryAdd(key, value);
    public bool TryRemove(TKey key) => _d.TryRemove(key, out _);
    public void Set(TKey key, TValue value) => _d[key] = value;
    public int Count => _d.Count;
    public IEnumerable<KeyValuePair<TKey, TValue>> Pairs => _d;
}

/// <summary>Selects the implementation by name and hands a typed adapter to the scenario.</summary>
internal interface IMapConsumer<TKey, TValue, TResult> where TKey : notnull
{
    TResult Consume<TMap>(TMap map) where TMap : struct, IMap<TKey, TValue>;
}

internal static class MapFactory
{
    /// <param name="comparer">
    /// Null uses each implementation's own default. They differ for strings: the BCL type switches
    /// to a non-randomized ordinal comparer, NonBlocking keeps the randomized default.
    /// </param>
    public static TResult With<TKey, TValue, TResult, TConsumer>(
        string impl, TConsumer consumer, IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
        where TConsumer : IMapConsumer<TKey, TValue, TResult>
        => impl switch
        {
            "nb" => consumer.Consume(new NbMap<TKey, TValue>(comparer is null
                ? new Nb.ConcurrentDictionary<TKey, TValue>()
                : new Nb.ConcurrentDictionary<TKey, TValue>(comparer))),
            "bcl" => consumer.Consume(new BclMap<TKey, TValue>(comparer is null
                ? new Bcl.ConcurrentDictionary<TKey, TValue>()
                : new Bcl.ConcurrentDictionary<TKey, TValue>(comparer))),
            _ => throw new System.ArgumentException($"Unknown implementation '{impl}'. Use 'nb' or 'bcl'.", nameof(impl)),
        };
}
