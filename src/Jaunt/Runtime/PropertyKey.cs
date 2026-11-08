using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jaunt.Runtime;

/// <summary>A JS symbol. Identity is reference identity.</summary>
internal sealed class JsSymbol
{
    public readonly string? Description;
    /// <summary>Registry key for Symbol.for symbols; null otherwise.</summary>
    public readonly string? RegistryKey;
    /// <summary>Identity hash, assigned up front (the CLR's object hash takes a thread-static and a header update).</summary>
    public readonly int Hash;
    private static int s_nextHash;

    public JsSymbol(string? description, string? registryKey = null)
    {
        Description = description;
        RegistryKey = registryKey;
        Hash = Interlocked.Add(ref s_nextHash, unchecked((int)0x9E3779B1));
    }

    public override string ToString() => "Symbol(" + Description + ")";

    // Well-known symbols are realm-independent (shared by all realms, per spec).
    public static readonly JsSymbol Iterator = new("Symbol.iterator");
    public static readonly JsSymbol AsyncIterator = new("Symbol.asyncIterator");
    public static readonly JsSymbol Dispose = new("Symbol.dispose");
    public static readonly JsSymbol AsyncDispose = new("Symbol.asyncDispose");
    public static readonly JsSymbol HasInstance = new("Symbol.hasInstance");
    public static readonly JsSymbol ToPrimitive = new("Symbol.toPrimitive");
    public static readonly JsSymbol ToStringTag = new("Symbol.toStringTag");
    public static readonly JsSymbol Species = new("Symbol.species");
    public static readonly JsSymbol IsConcatSpreadable = new("Symbol.isConcatSpreadable");
    public static readonly JsSymbol Unscopables = new("Symbol.unscopables");
    public static readonly JsSymbol Match = new("Symbol.match");
    public static readonly JsSymbol MatchAll = new("Symbol.matchAll");
    public static readonly JsSymbol Replace = new("Symbol.replace");
    public static readonly JsSymbol Search = new("Symbol.search");
    public static readonly JsSymbol Split = new("Symbol.split");
    /// <summary>Symbol.metadata (decorators proposal): the metadata object of a decorated class.</summary>
    public static readonly JsSymbol Metadata = new("Symbol.metadata");
}

/// <summary>Property key: either a string or a <see cref="JsSymbol"/>.</summary>
internal readonly struct PropertyKey : IEquatable<PropertyKey>
{
    public readonly object Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PropertyKey(string s) => Value = s;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PropertyKey(JsSymbol s) => Value = s;

    public bool IsSymbol => Value is JsSymbol;
    public bool IsString => Value is string;
    public string AsString => (string)Value;
    public JsSymbol AsSymbol => (JsSymbol)Value;

    public static implicit operator PropertyKey(string s) => new(s);
    public static implicit operator PropertyKey(JsSymbol s) => new(s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(PropertyKey other)
    {
        if (ReferenceEquals(Value, other.Value)) return true;
        return Value is string a && other.Value is string b && a.Length == b.Length && string.Equals(a, b);
    }

    public override bool Equals(object? obj) => obj is PropertyKey k && Equals(k);
    public override int GetHashCode() => Value is string s ? s.GetHashCode() : ((JsSymbol)Value).Hash;

    /// <summary>
    /// A hash for tables that hold a small bounded number of keys. It samples a string, so scripts can make keys collide;
    /// a table open to any number of script-chosen keys needs <see cref="GetHashCode"/> (seeded) instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CheapHash(object key)
    {
        if (key is string s)
        {
            int n = s.Length;
            if (n == 0) return 0;
            int h = (s[0] * 31 + s[n - 1]) * 31 + s[n >> 1] + n * 7;
            return h ^ (h >> 7);
        }
        return ((JsSymbol)key).Hash >> 16;
    }
    public override string ToString() => Value is string s ? s : Value.ToString()!;

    public JsValue ToJsValue() => Value is string s ? new JsValue(s) : JsValue.FromSymbol((JsSymbol)Value);

    /// <summary>True when this key is a canonical array index (0 .. 2^32-2).</summary>
    public bool TryGetArrayIndex(out uint index)
    {
        if (Value is string s) return TryParseArrayIndex(s, out index);
        index = 0;
        return false;
    }

    public static bool TryParseArrayIndex(string s, out uint index)
    {
        index = 0;
        int len = s.Length;
        if (len == 0 || len > 10) return false;
        char c0 = s[0];
        if (c0 < '0' || c0 > '9') return false;
        if (c0 == '0') { return len == 1; }
        ulong acc = 0;
        for (int i = 0; i < len; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9') return false;
            acc = acc * 10 + (uint)(c - '0');
        }
        if (acc >= uint.MaxValue) return false;
        index = (uint)acc;
        return true;
    }

    private static readonly string[] SmallIndexStrings = BuildSmall();
    private static string[] BuildSmall()
    {
        var arr = new string[1024];
        for (int i = 0; i < arr.Length; i++) arr[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return arr;
    }

    public static string IndexToString(uint i) =>
        i < SmallIndexStrings.Length ? SmallIndexStrings[i] : i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static PropertyKey FromIndex(uint i) => new(IndexToString(i));
}

internal sealed class PropertyKeyComparer : IEqualityComparer<PropertyKey>
{
    public static readonly PropertyKeyComparer Instance = new();
    public bool Equals(PropertyKey x, PropertyKey y) => x.Equals(y);
    public int GetHashCode(PropertyKey obj) => obj.GetHashCode();
}

[Flags]
internal enum PropFlags : byte
{
    None = 0,
    Writable = 1,
    Enumerable = 2,
    Configurable = 4,
    Accessor = 8,
    /// <summary>writable | enumerable | configurable — ordinary assignment-created data property.</summary>
    Default = Writable | Enumerable | Configurable,
    /// <summary>writable | configurable — builtin methods.</summary>
    Hidden = Writable | Configurable,
}

/// <summary>Getter/setter pair stored in a property slot.</summary>
internal sealed class Accessor
{
    public JsObject? Getter;
    public JsObject? Setter;
    public Accessor(JsObject? getter, JsObject? setter) { Getter = getter; Setter = setter; }
}

/// <summary>Full or partial property descriptor (ECMA-262 6.2.6).</summary>
internal struct PropertyDescriptor
{
    public JsValue Value;
    public JsObject? Getter;
    public JsObject? Setter;
    public bool HasValue, HasGet, HasSet, HasWritable, HasEnumerable, HasConfigurable;
    public bool Writable, Enumerable, Configurable;

    public bool IsAccessor => HasGet || HasSet;
    public bool IsData => HasValue || HasWritable;
    public bool IsGeneric => !IsAccessor && !IsData;

    public static PropertyDescriptor Data(JsValue value, PropFlags flags) => new()
    {
        Value = value, HasValue = true,
        HasWritable = true, Writable = (flags & PropFlags.Writable) != 0,
        HasEnumerable = true, Enumerable = (flags & PropFlags.Enumerable) != 0,
        HasConfigurable = true, Configurable = (flags & PropFlags.Configurable) != 0,
    };

    public static PropertyDescriptor AccessorDesc(JsObject? getter, JsObject? setter, PropFlags flags) => new()
    {
        Getter = getter, Setter = setter, HasGet = true, HasSet = true,
        HasEnumerable = true, Enumerable = (flags & PropFlags.Enumerable) != 0,
        HasConfigurable = true, Configurable = (flags & PropFlags.Configurable) != 0,
    };

    /// <summary>IsCompatiblePropertyDescriptor (10.1.6.2): ValidateAndApplyPropertyDescriptor without an object.</summary>
    public static bool IsCompatible(bool extensible, in PropertyDescriptor desc, bool hasCurrent, in PropertyDescriptor current)
    {
        if (!hasCurrent) return extensible;
        if (!desc.HasValue && !desc.HasGet && !desc.HasSet && !desc.HasWritable && !desc.HasEnumerable && !desc.HasConfigurable) return true;
        if (current.Configurable) return true;
        if (desc.HasConfigurable && desc.Configurable) return false;
        if (desc.HasEnumerable && desc.Enumerable != current.Enumerable) return false;
        if (!desc.IsGeneric && desc.IsAccessor != current.IsAccessor) return false;
        if (current.IsAccessor)
        {
            if (desc.HasGet && !ReferenceEquals(desc.Getter, current.Getter)) return false;
            if (desc.HasSet && !ReferenceEquals(desc.Setter, current.Setter)) return false;
        }
        else if (!current.Writable)
        {
            if (desc.HasWritable && desc.Writable) return false;
            if (desc.HasValue && !Operations.SameValue(desc.Value, current.Value)) return false;
        }
        return true;
    }

    public PropFlags ToFlags()
    {
        var f = PropFlags.None;
        if (Enumerable) f |= PropFlags.Enumerable;
        if (Configurable) f |= PropFlags.Configurable;
        if (IsAccessor) f |= PropFlags.Accessor;
        else if (Writable) f |= PropFlags.Writable;
        return f;
    }
}
