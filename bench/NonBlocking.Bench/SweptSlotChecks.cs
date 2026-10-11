using System;
using System.Collections.Generic;
using System.Reflection;

namespace NonBlocking.Bench;

/// <summary>
/// Deterministic checks for a slot the background sweeper is retiring: a removed key whose value
/// is TOMBPRIME while its key and hash are still set and no resize is in progress. A thread that
/// reaches the slot in that window must neither crash nor bring the removed key back. The window
/// is too narrow to hit reliably under load, so the state is built directly through reflection on
/// the library's internals; a renamed internal fails the check loudly rather than skipping it.
/// </summary>
internal static class SweptSlotChecks
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Key = "k";

    private static readonly (string Name, Func<ConcurrentDictionary<string, object>, string?> Check)[] Checks =
    {
        // A copier that lost the race to another thread, after the key was removed and swept.
        ("table copy", d =>
        {
            var (table, hash) = Sweep(d);
            var copy = FindMethod(table.GetType(), "PutSlotCopy");
            bool copied = (bool)copy.Invoke(table, new object[] { Key, "stale", hash })!;
            return copied || d.ContainsKey(Key) ? "brought the removed key back with a stale value" : null;
        }),
        ("GetOrAdd(key, factory)", d => { Sweep(d); return Equals(d.GetOrAdd(Key, _ => "new"), "new") && Equals(d[Key], "new") ? null : "did not add the key"; }),
        ("GetOrAdd(key, value)", d => { Sweep(d); return Equals(d.GetOrAdd(Key, "new"), "new") ? null : "did not add the key"; }),
        ("TryAdd", d => { Sweep(d); return d.TryAdd(Key, "new") && Equals(d[Key], "new") ? null : "did not add the key"; }),
        ("indexer set", d => { Sweep(d); d[Key] = "new"; return Equals(d[Key], "new") ? null : "did not set the key"; }),
        ("AddOrUpdate", d => { Sweep(d); return Equals(d.AddOrUpdate(Key, "new", (_, _) => "updated"), "new") ? null : "updated a removed key"; }),
        ("TryUpdate", d => { Sweep(d); return d.TryUpdate(Key, "new", "old") ? "updated a removed key" : null; }),
        ("TryGetValue", d => { Sweep(d); return d.TryGetValue(Key, out _) ? "found a removed key" : null; }),
        ("TryRemove", d => { Sweep(d); return d.TryRemove(Key, out _) ? "removed a key twice" : null; }),
        ("TryRemove(pair)", d => { Sweep(d); return d.TryRemove(new KeyValuePair<string, object>(Key, "old")) ? "removed a key twice" : null; }),
        ("enumerate", d =>
        {
            Sweep(d);
            foreach (var _ in d)
            {
                return "enumerated a removed key";
            }

            return d.Count == 0 ? null : $"Count is {d.Count}";
        }),
    };

    /// <summary>Returns the number of failed checks, after printing one line per check.</summary>
    public static int Run()
    {
        int failed = 0;
        foreach (var (name, check) in Checks)
        {
            string? problem;
            try
            {
                problem = check(new ConcurrentDictionary<string, object>());
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
                problem = $"threw {inner.GetType().Name}: {inner.Message}";
            }

            Console.WriteLine($"swept-slot check, {name}: {problem ?? "ok"}");
            if (problem is not null)
            {
                failed++;
            }
        }

        return failed;
    }

    /// <summary>Adds and removes the key, then leaves its slot as the sweeper does mid-way.</summary>
    private static (object Table, int Hash) Sweep(ConcurrentDictionary<string, object> d)
    {
        // A removal arms the real sweeper, which runs after the next garbage collection and would
        // clear the slot under the check. A pending request keeps SweepCheck from arming it.
        FindField(d.GetType(), "_sweepRequests").SetValue(d, 1);

        d[Key] = "old";
        d.TryRemove(Key, out _);
        object table = FindField(d.GetType(), "_table").GetValue(d)!;
        var entries = (Array)FindField(table.GetType(), "_entries").GetValue(table)!;
        object tombprime = FindField(table.GetType(), "TOMBPRIME").GetValue(null)!;
        for (int i = 0; i < entries.Length; i++)
        {
            // Entry is a struct: change a boxed copy and store it back.
            object entry = entries.GetValue(i)!;
            var type = entry.GetType();
            if (Equals(type.GetField("key", Any)!.GetValue(entry), Key))
            {
                type.GetField("value", Any)!.SetValue(entry, tombprime);
                entries.SetValue(entry, i);
                return (table, (int)type.GetField("hash", Any)!.GetValue(entry)!);
            }
        }

        throw new InvalidOperationException("the removed key's slot was not found");
    }

    private static FieldInfo FindField(Type? type, string name)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.GetField(name, Any | BindingFlags.DeclaredOnly) is { } field)
            {
                return field;
            }
        }

        throw new MissingFieldException(name);
    }

    private static MethodInfo FindMethod(Type? type, string name)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.GetMethod(name, Any | BindingFlags.DeclaredOnly) is { } method)
            {
                return method;
            }
        }

        throw new MissingMethodException(name);
    }
}
