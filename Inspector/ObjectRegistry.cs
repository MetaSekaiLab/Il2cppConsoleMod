using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2cppConsoleMod.Inspector;

/// <summary>
/// Handle table that keeps inspected objects alive so the web UI (and console
/// code through <c>$n</c>) can refer to them by a small integer id.
///
/// Il2Cpp wrappers are deduplicated by native pointer + wrapper type, since every
/// property read creates a fresh managed wrapper for the same native object.
/// The table is LRU-bounded; evicted ids answer "handle expired".
/// </summary>
public static class ObjectRegistry
{
    private const int Capacity = 20000;

    private static readonly object Sync = new();
    private static readonly Dictionary<int, LinkedListNode<Entry>> ById = new();
    private static readonly Dictionary<object, int> ByKey = new(KeyComparer.Instance);
    private static readonly LinkedList<Entry> Lru = new();
    private static int _nextId;

    /// <summary>Value of the last console evaluation, exposed to scripts as <c>$_</c>.</summary>
    public static object? Last { get; set; }

    public static int Count
    {
        get
        {
            lock (Sync)
                return ById.Count;
        }
    }

    public static int Register(object value)
    {
        var key = KeyOf(value);
        lock (Sync)
        {
            if (key != null && ByKey.TryGetValue(key, out var existing) && ById.TryGetValue(existing, out var node))
            {
                Touch(node);
                return existing;
            }

            var id = ++_nextId;
            var entry = new Entry(id, value, key);
            ById[id] = Lru.AddFirst(entry);
            if (key != null)
                ByKey[key] = id;

            while (ById.Count > Capacity && Lru.Last != null)
                Remove(Lru.Last);

            return id;
        }
    }

    public static bool TryGet(int id, out object value)
    {
        lock (Sync)
        {
            if (ById.TryGetValue(id, out var node))
            {
                Touch(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    /// <summary>Used by rewritten console code (<c>$12</c>); throws a readable error for stale ids.</summary>
    public static object Get(int id)
    {
        if (TryGet(id, out var value))
            return value;
        throw new KeyNotFoundException($"handle ${id} has expired or never existed");
    }

    public static void Clear()
    {
        lock (Sync)
        {
            ById.Clear();
            ByKey.Clear();
            Lru.Clear();
            Last = null;
        }
    }

    private static void Touch(LinkedListNode<Entry> node)
    {
        if (node != Lru.First)
        {
            Lru.Remove(node);
            Lru.AddFirst(node);
        }
    }

    private static void Remove(LinkedListNode<Entry> node)
    {
        Lru.Remove(node);
        ById.Remove(node.Value.Id);
        if (node.Value.Key != null)
            ByKey.Remove(node.Value.Key);
    }

    /// <summary>Boxed value types are never deduplicated; every box is its own snapshot.</summary>
    private static object? KeyOf(object value)
    {
        if (value.GetType().IsValueType)
            return null;

        if (value is Il2CppObjectBase il2cpp)
        {
            try
            {
                return new NativeKey(il2cpp.Pointer, value.GetType());
            }
            catch
            {
                return null;
            }
        }

        return value;
    }

    private sealed record Entry(int Id, object Value, object? Key);

    private readonly record struct NativeKey(IntPtr Pointer, Type Type);

    private sealed class KeyComparer : IEqualityComparer<object>
    {
        public static readonly KeyComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is NativeKey a && y is NativeKey b)
                return a == b;
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
            => obj is NativeKey key ? key.GetHashCode() : RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>Stands for "the static side of a type" so types can be browsed like objects.</summary>
public sealed class StaticTypeRef
{
    private static readonly Dictionary<Type, StaticTypeRef> Cache = new();

    private StaticTypeRef(Type type) => Type = type;

    public Type Type { get; }

    public static StaticTypeRef For(Type type)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(type, out var value))
                Cache[type] = value = new StaticTypeRef(type);
            return value;
        }
    }

    public override string ToString() => TypeNames.Display(Type);
}
