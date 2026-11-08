using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jaunt.Runtime;

/// <summary>
/// How a shape identifies the prototype of objects that use it. Shapes with a non-explicit role contain no
/// realm reference and are shared by all realms and threads (the realm resolves the role to its own intrinsic);
/// they are what keeps object literals, JSON objects, arrays and closures cheap in FreshRealm workloads.
/// </summary>
internal enum ProtoRole : byte
{
    Explicit = 0,
    RealmObjectPrototype = 1,
    RealmFunctionPrototype = 2,
    RealmArrayPrototype = 3,
}

/// <summary>
/// Hidden class describing the ordered named-property layout of ordinary objects. Explicit-prototype shapes are
/// realm-private; role shapes are process-wide, immutable once published, with lock-protected transition
/// tables and a global budget that bounds their growth.
/// </summary>
internal sealed class Shape
{
    /// <summary>Explicit prototype (null for role shapes or null-prototype objects).</summary>
    public readonly JsObject? Prototype;
    public readonly ProtoRole Role;
    public readonly int Count;
    public readonly PropertyKey[] Keys;
    public readonly PropFlags[] Flags;
    /// <summary>Unique shapes belong to dictionary-mode or exotic objects and are never cached by ICs.</summary>
    public readonly bool IsUnique;
    /// <summary>True when any key is an array index (key enumeration order needs sorting).</summary>
    public readonly bool HasIndexKeys;
    public readonly bool HasSymbolKeys;
    /// <summary>True when any property is an accessor or non-writable (affects set fast paths).</summary>
    public readonly bool HasSpecialProps;

    /// <summary>Open-addressed key table (slot + 1, 0 = empty) of a shape too large for the linear scan.</summary>
    private byte[]? _index;
    private Shape? _t1;
    private Dictionary<(PropertyKey, PropFlags), Shape>? _transitions;
    private object? _lock;
    private Shape? _sealed, _frozen;

    public const int MaxShapeProperties = 64;
    private const int LinearLimit = 8;
    /// <summary>Above this size an attribute change, which copies the flags, sends the object to dictionary mode instead.</summary>
    private const int MaxFlagChangeProperties = 32;
    internal const int MaxTransitionsPerSharedShape = 128;
    private const int MaxSharedShapes = 200_000;
    private static int s_sharedShapes;

    public static readonly Shape SharedObjectRoot = new(null, ProtoRole.RealmObjectPrototype);
    public static readonly Shape SharedFunctionRoot = new(null, ProtoRole.RealmFunctionPrototype);
    public static readonly Shape SharedArrayRoot = new(null, ProtoRole.RealmArrayPrototype);
    /// <summary>Shared layout of builtin/closure functions: length, name.</summary>
    public static readonly Shape SharedFunctionShape = SharedFunctionRoot.AddProperty("length", PropFlags.Configurable)!.AddProperty("name", PropFlags.Configurable)!;
    /// <summary>Shared layout of iterator result objects: value, done.</summary>
    public static readonly Shape SharedIterResultShape = SharedObjectRoot.AddProperty("value", PropFlags.Default)!.AddProperty("done", PropFlags.Default)!;
    /// <summary>
    /// Root of arguments object layouts, apart from <see cref="SharedObjectRoot"/> so that no plain object ever has a shape of
    /// one: a mapped arguments object keeps stale values in the slots of its mapped indices, which ICs must not read.
    /// </summary>
    private static readonly Shape SharedArgumentsRoot = new(null, ProtoRole.RealmObjectPrototype);
    private const int MaxArgumentsLayout = 24;
    private static readonly Shape?[] s_argumentsLayouts = new Shape?[(MaxArgumentsLayout + 1) * 2];
    /// <summary>Shared layout of a JSON.parse reviver's context object for a primitive value: source.</summary>
    public static readonly Shape SharedJsonContextShape = SharedObjectRoot.AddProperty("source", PropFlags.Default)!;

    /// <summary>Identity hash for the Vm's lookup caches (the CLR's object hash means a call and a header read per lookup).</summary>
    internal readonly int Hash = NextHash();
    private static int s_nextHash;
    // racing increments only make two shapes hash alike
    private static int NextHash() => (int)((uint)(s_nextHash += unchecked((int)0x9E3779B1)) >> 16);

    private Shape(JsObject? proto, ProtoRole role)
    {
        Prototype = proto;
        Role = role;
        Keys = System.Array.Empty<PropertyKey>();
        Flags = System.Array.Empty<PropFlags>();
    }

    private Shape(JsObject? proto, ProtoRole role, int count, PropertyKey[] keys, PropFlags[] flags, bool unique, bool hasIndex, bool hasSymbol, bool special)
    {
        Prototype = proto;
        Role = role;
        Count = count;
        Keys = keys;
        Flags = flags;
        IsUnique = unique;
        HasIndexKeys = hasIndex;
        HasSymbolKeys = hasSymbol;
        HasSpecialProps = special;
    }

    public bool IsShared => Role != ProtoRole.Explicit;

    public static Shape CreateRoot(JsObject? proto) => new(proto, ProtoRole.Explicit);
    public static Shape CreateUnique(JsObject? proto) => new(proto, ProtoRole.Explicit, 0, System.Array.Empty<PropertyKey>(), System.Array.Empty<PropFlags>(), true, false, false, true);

    /// <summary>Creates a cacheable shape directly from (possibly shared, immutable) key/flag arrays.</summary>
    public static Shape CreateFromArrays(JsObject? proto, PropertyKey[] keys, PropFlags[] flags)
    {
        bool hasIndex = false, hasSymbol = false, special = false;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].IsSymbol) hasSymbol = true;
            else if (keys[i].TryGetArrayIndex(out _)) hasIndex = true;
            if ((flags[i] & (PropFlags.Accessor | PropFlags.Writable)) != PropFlags.Writable) special = true;
        }
        return new Shape(proto, ProtoRole.Explicit, keys.Length, keys, flags, false, hasIndex, hasSymbol, special);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IndexOf(in PropertyKey key)
    {
        int count = Count;
        if (count <= LinearLimit)
        {
            var keys = Keys;
            object kv = key.Value;
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(keys[i].Value, kv)) return i;
            }
            if (kv is string ks)
            {
                for (int i = 0; i < count; i++)
                {
                    if (keys[i].Value is string s && s.Length == ks.Length && string.Equals(s, ks)) return i;
                }
            }
            return -1;
        }
        return IndexOfSlow(key);
    }

    private int IndexOfSlow(in PropertyKey key)
    {
        var index = Volatile.Read(ref _index) ?? BuildIndex();
        var keys = Keys;
        object kv = key.Value;
        int mask = index.Length - 1;
        for (int h = PropertyKey.CheapHash(kv) & mask; ; h = (h + 1) & mask)
        {
            int e = index[h];
            if (e == 0) return -1;
            object c = keys[e - 1].Value;
            if (ReferenceEquals(c, kv)) return e - 1;
            if (kv is string ks && c is string s && s.Length == ks.Length && string.Equals(s, ks)) return e - 1;
        }
    }

    /// <summary>
    /// Built on first use and immutable afterwards. A shape holds a small bounded number of keys (at most
    /// <see cref="MaxShapeProperties"/>, or the size limit of <see cref="JsObject.CompactToShape"/>), so a sampled
    /// string hash can't degrade far, and a miss, the usual outcome on a prototype, costs one or two probes.
    /// </summary>
    private byte[] BuildIndex()
    {
        int size = 16;
        while (size < Count * 2) size *= 2;
        var index = new byte[size];
        int mask = size - 1;
        for (int i = 0; i < Count; i++)
        {
            int h = PropertyKey.CheapHash(Keys[i].Value) & mask;
            while (index[h] != 0) h = (h + 1) & mask;
            index[h] = (byte)(i + 1);
        }
        Volatile.Write(ref _index, index);
        return index;
    }

    /// <summary>
    /// The layout of a fresh arguments object with <paramref name="argc"/> arguments (indices, length, @@iterator, callee),
    /// or null when that many arguments are built property by property.
    /// </summary>
    public static Shape? ArgumentsLayout(int argc, bool mapped)
    {
        if (argc > MaxArgumentsLayout) return null;
        int i = argc * 2 + (mapped ? 1 : 0);
        var shape = Volatile.Read(ref s_argumentsLayouts[i]);
        if (shape != null) return shape;
        shape = SharedArgumentsRoot;
        for (int n = 0; n < argc; n++) shape = shape.AddProperty(PropertyKey.FromIndex((uint)n), PropFlags.Default)!;
        shape = shape.AddProperty("length", PropFlags.Hidden)!.AddProperty(JsSymbol.Iterator, PropFlags.Hidden)!;
        shape = shape.AddProperty("callee", mapped ? PropFlags.Hidden : PropFlags.Accessor)!;
        Volatile.Write(ref s_argumentsLayouts[i], shape);
        return shape;
    }

    /// <summary>Returns the shape obtained by appending a property, or null when the object should go to dictionary mode.</summary>
    public Shape? AddProperty(in PropertyKey key, PropFlags flags)
    {
        if (IsUnique || Count >= MaxShapeProperties) return null;
        var t1 = Volatile.Read(ref _t1);
        if (t1 != null && t1.Flags[Count] == flags && t1.Keys[Count].Equals(key)) return t1;
        if (!IsShared)
        {
            if (_transitions != null && _transitions.TryGetValue((key, flags), out var existing)) return existing;
            var shape = Derive(key, flags);
            if (t1 == null) _t1 = shape;
            else (_transitions ??= new Dictionary<(PropertyKey, PropFlags), Shape>())[(key, flags)] = shape;
            return shape;
        }
        // shared shape: lock-free reads of an immutable (copy-on-write) transition table; writers publish under a lock
        var table = Volatile.Read(ref _transitions);
        if (table != null && table.TryGetValue((key, flags), out var found)) return found;
        bool cacheable = key.Value is string || IsWellKnownSymbol(key.Value);
        if (!cacheable || Volatile.Read(ref s_sharedShapes) >= MaxSharedShapes) return Derive(key, flags);
        // allocated once even when two threads race here (a plain `??=` could hand them different lock objects)
        lock (_lock ?? Interlocked.CompareExchange(ref _lock, new object(), null) ?? _lock)
        {
            t1 = _t1;
            if (t1 != null && t1.Flags[Count] == flags && t1.Keys[Count].Equals(key)) return t1;
            table = _transitions;
            if (table != null && table.TryGetValue((key, flags), out var existing)) return existing;
            var shape = Derive(key, flags);
            if (t1 == null)
            {
                Interlocked.Increment(ref s_sharedShapes);
                Volatile.Write(ref _t1, shape);
                return shape;
            }
            if (table != null && table.Count >= MaxTransitionsPerSharedShape) return shape;
            var copy = table == null
                ? new Dictionary<(PropertyKey, PropFlags), Shape>(TransitionComparer.Instance)
                : new Dictionary<(PropertyKey, PropFlags), Shape>(table, TransitionComparer.Instance);
            copy[(key, flags)] = shape;
            Interlocked.Increment(ref s_sharedShapes);
            Volatile.Write(ref _transitions, copy);
            return shape;
        }
    }

    /// <summary>
    /// Returns the shape with the attributes of the property in slot <paramref name="idx"/> changed (slots keep their place),
    /// or null when the object should go to dictionary mode. Cached in the transition table, where it can't be mistaken for
    /// an appended property: nothing appends a key the shape already has.
    /// </summary>
    public Shape? WithFlags(int idx, PropFlags flags)
    {
        if (IsUnique || Count > MaxFlagChangeProperties) return null;
        var key = Keys[idx];
        if (!IsShared)
        {
            if (_transitions != null && _transitions.TryGetValue((key, flags), out var existing)) return existing;
            var shape = ChangeFlags(idx, flags);
            (_transitions ??= new Dictionary<(PropertyKey, PropFlags), Shape>())[(key, flags)] = shape;
            return shape;
        }
        var table = Volatile.Read(ref _transitions);
        if (table != null && table.TryGetValue((key, flags), out var found)) return found;
        bool cacheable = key.Value is string || IsWellKnownSymbol(key.Value);
        if (!cacheable || Volatile.Read(ref s_sharedShapes) >= MaxSharedShapes) return ChangeFlags(idx, flags);
        lock (_lock ?? Interlocked.CompareExchange(ref _lock, new object(), null) ?? _lock)
        {
            table = _transitions;
            if (table != null && table.TryGetValue((key, flags), out var existing)) return existing;
            var shape = ChangeFlags(idx, flags);
            if (table != null && table.Count >= MaxTransitionsPerSharedShape) return shape;
            var copy = table == null
                ? new Dictionary<(PropertyKey, PropFlags), Shape>(TransitionComparer.Instance)
                : new Dictionary<(PropertyKey, PropFlags), Shape>(table, TransitionComparer.Instance);
            copy[(key, flags)] = shape;
            Interlocked.Increment(ref s_sharedShapes);
            Volatile.Write(ref _transitions, copy);
            return shape;
        }
    }

    private Shape ChangeFlags(int idx, PropFlags flags)
    {
        var fl = (PropFlags[])Flags.Clone();
        fl[idx] = flags;
        return WithAllFlags(fl);
    }

    private Shape WithAllFlags(PropFlags[] flags)
    {
        bool special = false;
        foreach (var f in flags) if ((f & (PropFlags.Accessor | PropFlags.Writable)) != PropFlags.Writable) special = true;
        return new Shape(Prototype, Role, Count, Keys, flags, false, HasIndexKeys, HasSymbolKeys, special) { _index = Volatile.Read(ref _index) };
    }

    /// <summary>The shape with every property non-configurable, and when <paramref name="frozen"/> every data property read-only as well; null for a unique shape.</summary>
    public Shape? WithIntegrity(bool frozen)
    {
        if (IsUnique) return null;
        ref Shape? cache = ref frozen ? ref _frozen : ref _sealed;
        var shape = Volatile.Read(ref cache);
        if (shape != null) return shape;
        var fl = new PropFlags[Count];
        bool changed = false;
        for (int i = 0; i < fl.Length; i++)
        {
            var f = Flags[i] & ~PropFlags.Configurable;
            if (frozen && (f & PropFlags.Accessor) == 0) f &= ~PropFlags.Writable;
            fl[i] = f;
            if (f != Flags[i]) changed = true;
        }
        if (!changed) shape = this;
        else
        {
            shape = WithAllFlags(fl);
            if (IsShared)
            {
                if (Volatile.Read(ref s_sharedShapes) >= MaxSharedShapes) return shape;
                Interlocked.Increment(ref s_sharedShapes);
            }
        }
        return Interlocked.CompareExchange(ref cache, shape, null) ?? shape;
    }

    /// <summary>Whether every property is non-configurable, and when <paramref name="frozen"/> every data property read-only as well.</summary>
    public bool HasIntegrity(bool frozen)
    {
        foreach (var f in Flags)
        {
            if ((f & PropFlags.Configurable) != 0) return false;
            if (frozen && (f & (PropFlags.Accessor | PropFlags.Writable)) == PropFlags.Writable) return false;
        }
        return true;
    }

    private static bool IsWellKnownSymbol(object k) =>
        ReferenceEquals(k, JsSymbol.Iterator) || ReferenceEquals(k, JsSymbol.ToStringTag) || ReferenceEquals(k, JsSymbol.ToPrimitive)
        || ReferenceEquals(k, JsSymbol.HasInstance) || ReferenceEquals(k, JsSymbol.AsyncIterator);

    private Shape Derive(in PropertyKey key, PropFlags flags)
    {
        var keys = new PropertyKey[Count + 1];
        var fl = new PropFlags[Count + 1];
        System.Array.Copy(Keys, keys, Count);
        System.Array.Copy(Flags, fl, Count);
        keys[Count] = key;
        fl[Count] = flags;
        bool isIndex = key.TryGetArrayIndex(out _);
        bool special = HasSpecialProps || (flags & (PropFlags.Accessor | PropFlags.Writable)) != PropFlags.Writable;
        return new Shape(Prototype, Role, Count + 1, keys, fl, false, HasIndexKeys || isIndex, HasSymbolKeys || key.IsSymbol, special);
    }
}

/// <summary>Keys the transition table of a shared shape, whose size is bounded by <see cref="Shape.MaxTransitionsPerSharedShape"/>.</summary>
internal sealed class TransitionComparer : IEqualityComparer<(PropertyKey Key, PropFlags Flags)>
{
    public static readonly TransitionComparer Instance = new();
    public bool Equals((PropertyKey Key, PropFlags Flags) x, (PropertyKey Key, PropFlags Flags) y) => x.Flags == y.Flags && x.Key.Equals(y.Key);
    public int GetHashCode((PropertyKey Key, PropFlags Flags) t) => PropertyKey.CheapHash(t.Key.Value) + (int)t.Flags;
}

/// <summary>Ordered property table used by dictionary-mode objects.</summary>
internal sealed class PropertyMap
{
    public PropertyKey[] Keys;
    public JsValue[] Values;
    public PropFlags[] Flags;
    public bool[] Live;
    public int Count; // includes tombstones
    public int LiveCount;
    private readonly Dictionary<PropertyKey, int> _index;
    public bool HasIndexKeys;
    public bool HasSymbolKeys;

    public PropertyMap(int capacity)
    {
        if (capacity < 4) capacity = 4;
        Keys = new PropertyKey[capacity];
        Values = new JsValue[capacity];
        Flags = new PropFlags[capacity];
        Live = new bool[capacity];
        _index = new Dictionary<PropertyKey, int>(capacity, PropertyKeyComparer.Instance);
    }

    public int IndexOf(in PropertyKey key) => _index.TryGetValue(key, out int i) ? i : -1;

    public int Add(in PropertyKey key, JsValue value, PropFlags flags)
    {
        if (Count == Keys.Length)
        {
            if (LiveCount < Count / 2) Compact();
            if (Count == Keys.Length)
            {
                int n = Keys.Length * 2;
                System.Array.Resize(ref Keys, n);
                System.Array.Resize(ref Values, n);
                System.Array.Resize(ref Flags, n);
                System.Array.Resize(ref Live, n);
            }
        }
        int idx = Count++;
        Keys[idx] = key;
        Values[idx] = value;
        Flags[idx] = flags;
        Live[idx] = true;
        LiveCount++;
        _index[key] = idx;
        if (key.IsSymbol) HasSymbolKeys = true;
        else if (key.TryGetArrayIndex(out _)) HasIndexKeys = true;
        return idx;
    }

    public void Remove(int idx)
    {
        _index.Remove(Keys[idx]);
        Live[idx] = false;
        Keys[idx] = default;
        Values[idx] = default;
        LiveCount--;
    }

    private void Compact()
    {
        int w = 0;
        for (int r = 0; r < Count; r++)
        {
            if (!Live[r]) continue;
            if (w != r)
            {
                Keys[w] = Keys[r];
                Values[w] = Values[r];
                Flags[w] = Flags[r];
                Live[w] = true;
            }
            w++;
        }
        for (int i = w; i < Count; i++) { Keys[i] = default; Values[i] = default; Live[i] = false; }
        Count = w;
        _index.Clear();
        for (int i = 0; i < Count; i++) _index[Keys[i]] = i;
    }
}
