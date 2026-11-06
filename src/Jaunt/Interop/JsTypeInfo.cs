using System;
using System.Collections.Generic;
using System.Text;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>
/// Conversion context for DTO mapping. Enforces the data-only contract (own data properties only; accessors,
/// proxies, functions and promises are rejected), node/depth/output budgets and member paths for errors.
/// </summary>
public sealed class JsConversionContext
{
    internal readonly Realm Realm;
    private readonly long _maxNodes;
    private long _nodes;
    private readonly List<string> _path = new();
    private int _depth;
    private const int MaxDepth = 256;

    internal JsConversionContext(Realm realm, long maxNodes)
    {
        Realm = realm;
        _maxNodes = maxNodes;
    }

    internal string Path
    {
        get
        {
            var sb = new StringBuilder("$");
            foreach (var p in _path) sb.Append(p);
            return sb.ToString();
        }
    }

    /// <summary>Fails the conversion at the current member path.</summary>
    public JsConversionException Fail(string message) => new(Path, message);

    private void Count()
    {
        if (++_nodes > _maxNodes) throw new JsTerminationException(TerminationKind.OutputBytes, "Conversion exceeded its node budget.", _nodes, _maxNodes);
        if ((_nodes & 255) == 0) Realm.Vm.Tick();
    }

    internal void Push(string segment)
    {
        if (++_depth > MaxDepth) throw Fail("Maximum conversion depth exceeded");
        _path.Add(segment);
    }

    internal void Pop()
    {
        _depth--;
        _path.RemoveAt(_path.Count - 1);
    }

    // ---- writing (CLR → JS)

    /// <summary>Creates a plain object.</summary>
    public JsValue CreateObject() { Count(); return JsValue.FromObject(Realm.NewObject()); }
    /// <summary>Creates an array.</summary>
    public JsValue CreateArray(int capacity = 0) { Count(); return JsValue.FromObject(new JsArray(Realm, capacity)); }
    /// <summary>Defines a data property.</summary>
    public void SetProperty(JsValue obj, string name, JsValue value)
    {
        if (obj.Ref is not JsObject o || !ReferenceEquals(o.Realm.Vm, Realm.Vm)) throw new ArgumentException("Not an object of this engine.", nameof(obj));
        JsEngine.CheckOwned(Realm, value, nameof(value));
        o.CreateDataPropertyOrThrow(name, value);
    }
    /// <summary>Appends to an array created by <see cref="CreateArray"/>.</summary>
    public void Append(JsValue array, JsValue value)
    {
        if (array.Ref is not JsArray a || !ReferenceEquals(a.Realm.Vm, Realm.Vm)) throw new ArgumentException("Not an array of this engine.", nameof(array));
        JsEngine.CheckOwned(Realm, value, nameof(value));
        a.Push(value);
    }
    /// <summary>Creates a string value.</summary>
    public JsValue String(string? s) { Count(); return JsValue.From(s); }

    // ---- reading (JS → CLR), data-only

    internal JsObject RequireDataObject(JsValue v, bool allowArray)
    {
        Count();
        if (v.IsNotObject) throw Fail($"Expected an object but found {v.TypeName}");
        var o = (JsObject)v.Ref!;
        switch (o)
        {
            case JsFunctionObject: throw Fail("Functions cannot be converted to data");
            case JsPromise: throw Fail("Promises cannot be converted to data; await the value first");
            case JsProxy: throw Fail("Proxies are not supported by data-only conversion");
            case JsArray when !allowArray: throw Fail("Expected an object but found an array");
        }
        return o;
    }

    /// <summary>Reads an own data property. Returns false when absent; throws for accessors.</summary>
    public bool TryGetProperty(JsValue obj, string name, out JsValue value)
    {
        var o = RequireDataObject(obj, allowArray: false);
        if (o.IsExotic)
        {
            if (!o.GetOwnProperty(name, out var d)) { value = default; return false; }
            if (d.IsAccessor) throw Fail($"Property '{name}' is an accessor; data-only conversion does not run getters");
            value = d.Value;
            return true;
        }
        int i = o.FindOwn(name, out var f);
        if (i < 0) { value = default; return false; }
        if ((f & PropFlags.Accessor) != 0) throw Fail($"Property '{name}' is an accessor; data-only conversion does not run getters");
        value = o.ReadSlot(i);
        return true;
    }

    /// <summary>Reads array elements (dense or sparse; holes read as undefined).</summary>
    public IReadOnlyList<JsValue> GetArrayItems(JsValue v)
    {
        var o = RequireDataObject(v, allowArray: true);
        if (o is not JsArray a) throw Fail($"Expected an array but found {o.ClassName}");
        var list = new JsValue[a.Length];
        for (uint i = 0; i < a.Length; i++)
        {
            if (a.TryGetDense(i, out var x)) list[i] = x;
            else if (a.GetOwnProperty(PropertyKey.FromIndex(i), out var d))
            {
                if (d.IsAccessor) throw Fail($"Element {i} is an accessor");
                list[i] = d.Value;
            }
        }
        return list;
    }

    /// <summary>Own enumerable string-keyed data properties in order.</summary>
    public IEnumerable<KeyValuePair<string, JsValue>> GetEntries(JsValue v)
    {
        var o = RequireDataObject(v, allowArray: false);
        var keys = new List<PropertyKey>();
        o.OwnPropertyKeys(keys);
        var result = new List<KeyValuePair<string, JsValue>>();
        foreach (var k in keys)
        {
            if (k.Value is not string s) continue;
            if (!o.GetOwnProperty(k, out var d) || !d.Enumerable) continue;
            if (d.IsAccessor) throw Fail($"Property '{s}' is an accessor; data-only conversion does not run getters");
            result.Add(new(s, d.Value));
        }
        return result;
    }
}

/// <summary>
/// Explicit, AOT-safe mapping between a CLR type and JS data. Instances are produced by
/// <see cref="JsTypeInfo"/> factory members, the <c>Jaunt.Generators</c> source generator, or hand-written
/// subclasses. No reflection or runtime code generation is ever used as a fallback.
/// </summary>
public abstract class JsTypeInfo<T>
{
    /// <summary>Converts a CLR value to JS.</summary>
    public abstract JsValue Write(JsConversionContext context, T value);
    /// <summary>Converts JS data to a new CLR value.</summary>
    public abstract T Read(JsConversionContext context, JsValue value);
}

/// <summary>Built-in type infos and builders.</summary>
public static class JsTypeInfo
{
    /// <summary>JS number ↔ double.</summary>
    public static JsTypeInfo<double> Double { get; } = new Prim<double>(static (c, v) => v.IsNumber ? v.Num : throw c.Fail($"Expected a number but found {v.TypeName}"), static (c, v) => new JsValue(v));
    /// <summary>JS integer number ↔ int (no truncation).</summary>
    public static JsTypeInfo<int> Int32 { get; } = new Prim<int>(static (c, v) => v.IsNumber && v.Num == Math.Floor(v.Num) && v.Num >= int.MinValue && v.Num <= int.MaxValue ? (int)v.Num : throw c.Fail($"Expected an Int32 integer but found {v}"), static (c, v) => new JsValue(v));
    /// <summary>JS safe integer ↔ long.</summary>
    public static JsTypeInfo<long> Int64 { get; } = new Prim<long>(static (c, v) => v.IsNumber && v.Num == Math.Floor(v.Num) && Math.Abs(v.Num) <= JsValue.MaxSafeInteger ? (long)v.Num : throw c.Fail($"Expected a safe integer but found {v}"),
        static (c, v) => v > JsValue.MaxSafeInteger || v < -JsValue.MaxSafeInteger ? throw c.Fail($"Value {v} exceeds the JavaScript safe integer range") : new JsValue(v));
    /// <summary>JS boolean ↔ bool.</summary>
    public static JsTypeInfo<bool> Boolean { get; } = new Prim<bool>(static (c, v) => v.IsBoolean ? v.IsTrue : throw c.Fail($"Expected a boolean but found {v.TypeName}"), static (c, v) => JsValue.FromBool(v));
    /// <summary>JS string ↔ string (null ↔ null; undefined reads as null).</summary>
    public static JsTypeInfo<string?> String { get; } = new Prim<string?>(static (c, v) => v.IsString ? v.AsStringUnchecked : v.IsNullOrUndefined ? null : throw c.Fail($"Expected a string but found {v.TypeName}"), static (c, v) => JsValue.From(v));
    /// <summary>ISO-8601 string ↔ DateTimeOffset (explicit; no culture guessing).</summary>
    public static JsTypeInfo<DateTimeOffset> IsoDateTime { get; } = new Prim<DateTimeOffset>(
        static (c, v) => v.IsString && DateTimeOffset.TryParse(v.AsStringUnchecked, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : throw c.Fail("Expected an ISO-8601 date string"),
        static (c, v) => new JsValue(v.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Nullable value type (null/undefined ↔ null).</summary>
    public static JsTypeInfo<T?> Nullable<T>(JsTypeInfo<T> inner) where T : struct => new NullableInfo<T>(inner);

    /// <summary>JS array ↔ List&lt;T&gt;.</summary>
    public static JsTypeInfo<List<T>> List<T>(JsTypeInfo<T> item) => new ListInfo<T>(item);

    /// <summary>JS array ↔ T[].</summary>
    public static JsTypeInfo<T[]> Array<T>(JsTypeInfo<T> item) => new ArrayInfo<T>(item);

    /// <summary>JS plain object ↔ Dictionary&lt;string, T&gt; (own enumerable data properties).</summary>
    public static JsTypeInfo<Dictionary<string, T>> Dictionary<T>(JsTypeInfo<T> value) => new DictInfo<T>(value);

    /// <summary>Defers creation of a descriptor (supports recursive DTO graphs in generated code).</summary>
    public static JsTypeInfo<T> Lazy<T>(Func<JsTypeInfo<T>> factory) => new LazyInfo<T>(factory);

    private sealed class LazyInfo<T> : JsTypeInfo<T>
    {
        private readonly Func<JsTypeInfo<T>> _factory;
        private JsTypeInfo<T>? _inner;
        public LazyInfo(Func<JsTypeInfo<T>> factory) => _factory = factory;
        private JsTypeInfo<T> Inner => _inner ??= _factory();
        public override T Read(JsConversionContext context, JsValue value) => Inner.Read(context, value);
        public override JsValue Write(JsConversionContext context, T value) => Inner.Write(context, value);
    }

    /// <summary>Starts an object descriptor for <typeparamref name="T"/>.</summary>
    public static JsObjectTypeInfoBuilder<T> Object<T>() => new();

    private sealed class Prim<T> : JsTypeInfo<T>
    {
        private readonly Func<JsConversionContext, JsValue, T> _read;
        private readonly Func<JsConversionContext, T, JsValue> _write;
        public Prim(Func<JsConversionContext, JsValue, T> read, Func<JsConversionContext, T, JsValue> write) { _read = read; _write = write; }
        public override T Read(JsConversionContext context, JsValue value) => _read(context, value);
        public override JsValue Write(JsConversionContext context, T value) => _write(context, value);
    }

    private sealed class NullableInfo<T> : JsTypeInfo<T?> where T : struct
    {
        private readonly JsTypeInfo<T> _inner;
        public NullableInfo(JsTypeInfo<T> inner) => _inner = inner;
        public override T? Read(JsConversionContext context, JsValue value) => value.IsNullOrUndefined ? null : _inner.Read(context, value);
        public override JsValue Write(JsConversionContext context, T? value) => value.HasValue ? _inner.Write(context, value.Value) : JsValue.Null;
    }

    private sealed class ListInfo<T> : JsTypeInfo<List<T>>
    {
        private readonly JsTypeInfo<T> _item;
        public ListInfo(JsTypeInfo<T> item) => _item = item;
        public override List<T> Read(JsConversionContext context, JsValue value)
        {
            var items = context.GetArrayItems(value);
            var list = new List<T>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                context.Push("[" + i + "]");
                list.Add(_item.Read(context, items[i]));
                context.Pop();
            }
            return list;
        }
        public override JsValue Write(JsConversionContext context, List<T> value)
        {
            if (value == null) return JsValue.Null;
            var arr = context.CreateArray(value.Count);
            for (int i = 0; i < value.Count; i++)
            {
                context.Push("[" + i + "]");
                context.Append(arr, _item.Write(context, value[i]));
                context.Pop();
            }
            return arr;
        }
    }

    private sealed class ArrayInfo<T> : JsTypeInfo<T[]>
    {
        private readonly ListInfo<T> _list;
        public ArrayInfo(JsTypeInfo<T> item) => _list = new ListInfo<T>(item);
        public override T[] Read(JsConversionContext context, JsValue value) => _list.Read(context, value).ToArray();
        public override JsValue Write(JsConversionContext context, T[] value) => value == null ? JsValue.Null : _list.Write(context, new List<T>(value));
    }

    private sealed class DictInfo<T> : JsTypeInfo<Dictionary<string, T>>
    {
        private readonly JsTypeInfo<T> _value;
        public DictInfo(JsTypeInfo<T> value) => _value = value;
        public override Dictionary<string, T> Read(JsConversionContext context, JsValue value)
        {
            var d = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var kv in context.GetEntries(value))
            {
                context.Push("." + kv.Key);
                d[kv.Key] = _value.Read(context, kv.Value);
                context.Pop();
            }
            return d;
        }
        public override JsValue Write(JsConversionContext context, Dictionary<string, T> value)
        {
            if (value == null) return JsValue.Null;
            var o = context.CreateObject();
            foreach (var kv in value)
            {
                context.Push("." + kv.Key);
                context.SetProperty(o, kv.Key, _value.Write(context, kv.Value));
                context.Pop();
            }
            return o;
        }
    }
}

/// <summary>Reads members of a JS data object during DTO construction.</summary>
public readonly struct JsObjectReader
{
    private readonly JsConversionContext _context;
    private readonly JsValue _object;

    internal JsObjectReader(JsConversionContext context, JsValue obj)
    {
        _context = context;
        _object = obj;
    }

    /// <summary>Reads a required member; missing members are conversion errors.</summary>
    public TProp Required<TProp>(string name, JsTypeInfo<TProp> info)
    {
        if (!_context.TryGetProperty(_object, name, out var v)) throw _context.Fail($"Required member '{name}' is missing");
        _context.Push("." + name);
        var r = info.Read(_context, v);
        _context.Pop();
        return r;
    }

    /// <summary>Reads an optional member (missing or undefined → <paramref name="defaultValue"/>).</summary>
    public TProp Optional<TProp>(string name, JsTypeInfo<TProp> info, TProp defaultValue = default!)
    {
        if (!_context.TryGetProperty(_object, name, out var v) || v.IsUndefined) return defaultValue;
        _context.Push("." + name);
        var r = info.Read(_context, v);
        _context.Pop();
        return r;
    }
}

/// <summary>Fluent builder for object descriptors (used by hand-written and generated bindings).</summary>
public sealed class JsObjectTypeInfoBuilder<T>
{
    private readonly List<Func<JsConversionContext, T, (string, JsValue)>> _writers = new();
    private Func<JsObjectReader, T>? _factory;

    /// <summary>Adds a member written to JS.</summary>
    public JsObjectTypeInfoBuilder<T> Property<TProp>(string jsName, Func<T, TProp> getter, JsTypeInfo<TProp> info)
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(info);
        _writers.Add((ctx, obj) =>
        {
            ctx.Push("." + jsName);
            var v = info.Write(ctx, getter(obj));
            ctx.Pop();
            return (jsName, v);
        });
        return this;
    }

    /// <summary>Sets how a new <typeparamref name="T"/> is created from JS data.</summary>
    public JsObjectTypeInfoBuilder<T> Factory(Func<JsObjectReader, T> factory)
    {
        _factory = factory;
        return this;
    }

    /// <summary>Builds the immutable descriptor.</summary>
    public JsTypeInfo<T> Build() => new Built(_writers.ToArray(), _factory);

    private sealed class Built : JsTypeInfo<T>
    {
        private readonly Func<JsConversionContext, T, (string, JsValue)>[] _writers;
        private readonly Func<JsObjectReader, T>? _factory;

        public Built(Func<JsConversionContext, T, (string, JsValue)>[] writers, Func<JsObjectReader, T>? factory)
        {
            _writers = writers;
            _factory = factory;
        }

        public override T Read(JsConversionContext context, JsValue value)
        {
            if (_factory == null) throw context.Fail($"Type '{typeof(T).Name}' has no factory; it can only be written to JS");
            context.RequireDataObject(value, allowArray: false);
            return _factory(new JsObjectReader(context, value));
        }

        public override JsValue Write(JsConversionContext context, T value)
        {
            if (value is null) return JsValue.Null;
            var o = context.CreateObject();
            foreach (var w in _writers)
            {
                var (name, v) = w(context, value);
                context.SetProperty(o, name, v);
            }
            return o;
        }
    }
}

/// <summary>Base class for source-generated binding contexts (<c>[JsSerializable(typeof(T))]</c>).</summary>
public abstract class JsBindingContext
{
}

/// <summary>Requests source generation of a <see cref="JsTypeInfo{T}"/> for a type on a partial <see cref="JsBindingContext"/>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class JsSerializableAttribute : Attribute
{
    /// <summary>The DTO type.</summary>
    public Type Type { get; }
    /// <summary>Creates the attribute.</summary>
    public JsSerializableAttribute(Type type) => Type = type;
}

/// <summary>Overrides the JS member name used by generated bindings.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class JsPropertyNameAttribute : Attribute
{
    /// <summary>JS name.</summary>
    public string Name { get; }
    /// <summary>Creates the attribute.</summary>
    public JsPropertyNameAttribute(string name) => Name = name;
}

/// <summary>Excludes a member from generated bindings.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class JsIgnoreAttribute : Attribute
{
}
