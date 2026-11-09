using System.Collections.Generic;
using System;
using System.Runtime.CompilerServices;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

/// <summary>Heap environment record for captured bindings.</summary>
internal sealed class Env
{
    public readonly JsValue[] Slots;
    public readonly Env? Parent;
    /// <summary>What only scopes that <c>with</c> or direct eval reach have: every closure over a loop or call allocates an Env, the rest stay small.</summary>
    private DynamicParts? _dynamic;
    private sealed class DynamicParts { public ScopeLayout? Layout; public JsObject? WithObject; public Dictionary<string, JsValue>? EvalVars; }

    /// <summary>Slot names, present only for scopes that direct eval / with may inspect by name.</summary>
    public ScopeLayout? Layout { get => _dynamic?.Layout; set { if (value != null || _dynamic != null) (_dynamic ??= new()).Layout = value; } }
    /// <summary>Object environment record of a <c>with</c> statement.</summary>
    public JsObject? WithObject { get => _dynamic?.WithObject; set { if (value != null || _dynamic != null) (_dynamic ??= new()).WithObject = value; } }
    /// <summary>Variables created by sloppy direct eval in this function's variable environment.</summary>
    public Dictionary<string, JsValue>? EvalVars { get => _dynamic?.EvalVars; set { if (value != null || _dynamic != null) (_dynamic ??= new()).EvalVars = value; } }

    public Env(Env? parent, int size, int lexStart)
    {
        Parent = parent;
        Slots = size == 0 ? Array.Empty<JsValue>() : new JsValue[size];
        for (int i = lexStart; i < size; i++) Slots[i] = JsValue.Empty;
    }

    private Env(Env? parent, JsValue[] slots)
    {
        Parent = parent;
        Slots = slots;
    }

    /// <summary>A loop body's closures each keep their own copy. Not <c>Array.Clone</c>: that allocates through the runtime's slow path, several times the cost of the rest of this.</summary>
    public Env CopyForIteration()
    {
        var from = Slots;
        var to = new JsValue[from.Length];
        for (int i = 0; i < to.Length; i++) JsValue.Store(ref to[i], from[i]);
        var copy = new Env(Parent, to);
        if (_dynamic != null) copy.Layout = _dynamic.Layout;
        return copy;
    }
}

[Flags]
internal enum SlotFlags : byte
{
    None = 0,
    Lexical = 1,
    Const = 2,
    /// <summary>Name binding of a named function expression: assignments are ignored (TypeError in strict code).</summary>
    FuncName = 4,
    CatchParam = 8,
}

/// <summary>Compile-time slot names of a scope (immutable, shared by all realms).</summary>
internal sealed class ScopeLayout
{
    public required string?[] Names;
    public required SlotFlags[] Flags;
    /// <summary>Function (or strict eval) scope: target of sloppy direct-eval var declarations.</summary>
    public bool IsVarScope;
    private Dictionary<string, int>? _index;

    public int IndexOf(string name)
    {
        var names = Names;
        if (names.Length <= 8)
        {
            for (int i = 0; i < names.Length; i++) if (string.Equals(names[i], name, StringComparison.Ordinal)) return i;
            return -1;
        }
        var idx = _index;
        if (idx == null)
        {
            idx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++) if (names[i] != null) idx[names[i]!] = i;
            _index = idx;
        }
        return idx.TryGetValue(name, out var j) ? j : -1;
    }
}

internal struct IcEntry
{
    public Shape? Shape;
    public JsObject? Holder;
    public Shape? NewShape;
    public object? Cell;
    public int Slot;
    public int Epoch;
    public byte Kind;
    public byte Misses;
}

internal static class IcKind
{
    public const byte None = 0;
    public const byte OwnData = 1;
    public const byte ProtoData = 2;
    public const byte OwnGetter = 3;
    public const byte ProtoGetter = 4;
    public const byte AddTransition = 5;
    public const byte GlobalCell = 6;
    public const byte GlobalProp = 7;
    public const byte StringLength = 8;
    public const byte StringProtoData = 9;
    /// <summary>`length` of an Array exotic object (an own, non-shape property).</summary>
    public const byte ArrayLength = 10;
    /// <summary>
    /// Computed-key access on an ordinary object: Shape + key (Cell = the string key, or null with Epoch = the array
    /// index) → own data slot. Filled by the GetElem/SetElem slow paths; SetElem fills only writable properties.
    /// </summary>
    public const byte KeyedOwnData = 11;
    /// <summary>The name is absent from the receiver and its (shape-mode) prototype chain; valid while ProtoEpoch is unchanged.</summary>
    public const byte NotFound = 12;
    public const byte Megamorphic = 255;
}

/// <summary>Realm-private execution feedback (ICs, template objects) for one function template.</summary>
internal sealed class Feedback
{
    public readonly IcEntry[] Ics;
    private Feedback?[]? _children;
    public readonly FunctionTemplate Template;
    /// <summary><c>Template.Code</c> and <c>Template.Constants</c>, one load from the frame (see <see cref="Frame.Feedback"/>).</summary>
    public readonly int[] Code;
    public readonly JsValue[] Constants;
    /// <summary>For a constructor: the slot count to give the next instance, learned from the last one finished and taken by the next one (at most <see cref="Vm.MaxInstanceSlots"/>).</summary>
    public int InstanceSlots;
    /// <summary>"length" and "name" of the closures of a function with no "prototype", shared while none has redefined them (see <see cref="JsClosure"/>).</summary>
    public JsValue[]? SharedClosureSlots;

    public Feedback(FunctionTemplate t)
    {
        Template = t;
        Code = t.Code;
        Constants = t.Constants;
        Ics = t.IcCount == 0 ? Array.Empty<IcEntry>() : new IcEntry[t.IcCount];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Feedback Child(int index)
    {
        var c = _children;
        if (c != null)
        {
            var f = c[index];
            if (f != null) return f;
        }
        return CreateChild(index);
    }

    private Feedback CreateChild(int index)
    {
        _children ??= new Feedback?[Template.Functions.Length];
        return _children[index] = new Feedback(Template.Functions[index]);
    }
}

/// <summary>Callable object.</summary>
internal abstract class JsFunctionObject : JsObject
{
    protected JsFunctionObject(Realm realm, JsObject? proto) : base(realm, proto) { }
    protected JsFunctionObject(Realm realm, Shape shape, int slots) : base(realm, shape, slots) { }
    protected JsFunctionObject(Realm realm, Shape shape, JsValue[] slots) : base(realm, shape, slots) { }
    protected JsFunctionObject(Realm realm, JsObject? proto, bool exotic) : base(realm, proto, exotic) { }

    public override string ClassName => "Function";
    public virtual bool IsConstructor => false;
    public abstract string DebugName { get; }

    public abstract JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args);

    public virtual JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget)
        => throw JsErrors.TypeError(Realm, $"{DebugName} is not a constructor");

    /// <summary>Realm of the function (for GetFunctionRealm).</summary>
    public virtual Realm FunctionRealm => Realm;
}

/// <summary>Bytecode function instance (closure).</summary>
internal sealed class JsClosure : JsFunctionObject
{
    public readonly FunctionTemplate Template;
    public readonly Env? Env;
    public readonly Feedback Feedback;
    public JsValue BoundThis;
    public JsObject? HomeObject;
    public ModuleInstance? Module;
    public bool IsDerived;
    public bool IsClassConstructor;
    /// <summary>What only classes with fields, derived constructors' arrows and arrows in a constructed function have (methods and module functions are common: their parts stay fields).</summary>
    private RareParts? _rare;
    public bool HasRareParts => _rare != null;
    private sealed class RareParts { public ThisBox? ThisBox; public JsValue BoundNewTarget; public JsClosure? FieldInit; }

    public ThisBox? ThisBox { get => _rare?.ThisBox; set { if (value != null || _rare != null) (_rare ??= new()).ThisBox = value; } }
    /// <summary>new.target of an arrow's enclosing function: undefined unless that function was constructed, the only case stored.</summary>
    public JsValue BoundNewTarget
    {
        get { var r = _rare; return r != null ? r.BoundNewTarget : default; }
        set { if (value.Ref != null || _rare != null) (_rare ??= new()).BoundNewTarget = value; }
    }
    public JsClosure? FieldInit { get => _rare?.FieldInit; set { if (value != null || _rare != null) (_rare ??= new()).FieldInit = value; } }

    public JsClosure(Realm realm, FunctionTemplate t, Env? env, Feedback fb, JsObject proto)
        : base(realm, realm.FunctionShapeFor(proto), InitialSlots(t, fb))
    {
        Template = t;
        Env = env;
        Feedback = fb;
        IsClassConstructor = t.IsClassConstructor;
        IsDerived = t.IsDerived;
    }

    /// <summary>
    /// "length" and "name", the first two slots of the function shape. Arrows and methods, which have nothing else, share
    /// one array per function: these properties are not writable, so only a redefinition writes them, and that takes
    /// a copy first (<see cref="DefineOwnProperty"/>, <see cref="Vm.SetFunctionName"/>). Adding a property grows into a new array.
    /// </summary>
    private static JsValue[] InitialSlots(FunctionTemplate t, Feedback fb)
    {
        if (t.IsConstructor || t.IsGenerator)
        {
            var own = new JsValue[3];   // the third slot is for "prototype"
            own[0] = new JsValue(t.Length);
            own[1] = new JsValue(t.Name);
            return own;
        }
        return fb.SharedClosureSlots ?? NewSharedSlots(t, fb);
    }

    private static JsValue[] NewSharedSlots(FunctionTemplate t, Feedback fb) => fb.SharedClosureSlots = new[] { new JsValue(t.Length), new JsValue(t.Name) };

    /// <summary>Before anything that may write "length" or "name" whatever their attributes.</summary>
    internal void UnshareSlots()
    {
        var slots = Slots;
        if (ReferenceEquals(slots, Feedback.SharedClosureSlots)) Slots = new[] { slots[0], slots[1] };   // not Clone: every accessor of a literal comes here, for its name
    }

    /// <summary>
    /// The value of the "prototype" property when it is the third slot of the function layout and holds an object, else null
    /// (take the general path). <c>new</c> and <c>instanceof</c> read it each time, and it is the same layout almost always.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsObject? PrototypeFast()
    {
        var shape = Shape;
        if (shape.Count > 2 && ReferenceEquals(shape.Keys[2].Value, "prototype"))
        {
            ref readonly var v = ref Slots[2];
            if (v.IsObject) return v.AsObjectUnchecked;
        }
        return null;
    }

    /// <summary>The object an ordinary function's "prototype" property starts with.</summary>
    internal JsObject CreatePrototypeObject()
    {
        var p = new JsObject(Realm, Realm.ObjectPrototype);
        p.AddOwnProperty("constructor", JsValue.FromObject(this), PropFlags.Hidden);
        return p;
    }

    public override bool IsConstructor => Template.IsConstructor;
    public override string DebugName => Template.Name.Length == 0 ? "anonymous" : Template.Name;

    // ------------------------------------------------------------------ legacy "caller" / "arguments" (ECMA-262 §17.1)

    private const FunctionFlags NonLegacy = FunctionFlags.Strict | FunctionFlags.Arrow | FunctionFlags.Method | FunctionFlags.ClassConstructor
        | FunctionFlags.Generator | FunctionFlags.Async | FunctionFlags.ScriptBody | FunctionFlags.ModuleBody | FunctionFlags.FieldInitializer
        | FunctionFlags.ClassStaticInit | FunctionFlags.IsGetter | FunctionFlags.IsSetter;

    /// <summary>
    /// Sloppy ordinary functions expose own, non-writable, configurable "caller" and "arguments" properties, as the
    /// web-compatible engines do. "caller" is the function that called the most recent active invocation, but never a
    /// strict function or script/eval code (those read as null); "arguments" reads as null. They are configurable so
    /// the varying value does not break the invariants of the essential internal methods; deleting or redefining one
    /// replaces it for good.
    /// </summary>
    internal bool IsLegacyKey(in PropertyKey key) =>
        (Template.Flags & NonLegacy) == 0 && key.Value is string s
        && ((s == "caller" && (_legacyRemoved & 1) == 0) || (s == "arguments" && (_legacyRemoved & 2) == 0)) && FindOwn(key, out _) < 0;

    private byte _legacyRemoved;
    private void RemoveLegacy(in PropertyKey key) => _legacyRemoved |= (byte)((string)key.Value == "caller" ? 1 : 2);

    private JsValue LegacyValue(in PropertyKey key)
    {
        if ((string)key.Value == "arguments") return JsValue.Null;
        var vm = Realm.Vm;
        for (int d = vm.Sp - 1; d >= 0; d--)
        {
            if (!ReferenceEquals(vm.FrameAt(d).Callee, this)) continue;
            var c = d > 0 ? vm.FrameAt(d - 1).Callee : null;
            if (c == null || (c.Template.Flags & (FunctionFlags.Strict | FunctionFlags.ScriptBody | FunctionFlags.ModuleBody)) != 0) return JsValue.Null;
            return JsValue.FromObject(c);
        }
        return JsValue.Null;
    }

    internal override bool CanCacheNamed(in PropertyKey key) => !IsLegacyKey(key);

    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc)
    {
        if (IsLegacyKey(key)) { desc = PropertyDescriptor.Data(LegacyValue(key), PropFlags.Configurable); return true; }
        return base.GetOwnProperty(key, out desc);
    }

    public override JsValue Get(in PropertyKey key, JsValue receiver) => IsLegacyKey(key) ? LegacyValue(key) : base.Get(key, receiver);

    public override bool HasProperty(in PropertyKey key) => IsLegacyKey(key) || base.HasProperty(key);

    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver) =>
        IsLegacyKey(key) ? false : base.Set(key, value, receiver);

    public override bool Delete(in PropertyKey key)
    {
        if (IsLegacyKey(key)) { RemoveLegacy(key); return true; }
        return base.Delete(key);
    }

    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc)
    {
        UnshareSlots();
        if (IsLegacyKey(key))
        {
            // materialize as an ordinary own property holding the current value, then apply the definition
            var current = LegacyValue(key);
            RemoveLegacy(key);
            AddOwnProperty((string)key.Value, current, PropFlags.Configurable);
        }
        return base.DefineOwnProperty(key, desc);
    }

    public override void OwnPropertyKeys(List<PropertyKey> keys)
    {
        base.OwnPropertyKeys(keys);
        if ((Template.Flags & NonLegacy) != 0) return;
        foreach (var name in s_legacyNames)
        {
            var k = new PropertyKey(name);
            if (!IsLegacyKey(k)) continue;
            // after "length"/"name", before "prototype" and later additions (string keys precede symbols)
            int at = Math.Min(2, keys.Count);
            while (at < keys.Count && keys[at].Value is string s2 && (s2 == "arguments" || s2 == "caller")) at++;
            keys.Insert(at, k);
        }
    }

    private static readonly string[] s_legacyNames = { "arguments", "caller" };

    public override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        if (IsClassConstructor) throw JsErrors.TypeError(Realm, $"Class constructor {DebugName} cannot be invoked without 'new'");
        return Realm.Vm.CallClosure(this, thisValue, args, JsValue.Undefined);
    }

    public override JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget)
    {
        if (!IsConstructor) throw JsErrors.TypeError(Realm, $"{DebugName} is not a constructor");
        return Realm.Vm.ConstructClosure(this, args, newTarget);
    }
}

internal readonly ref struct NativeArgs
{
    public readonly Realm Realm;
    public readonly JsValue This;
    public readonly ReadOnlySpan<JsValue> Args;
    /// <summary>undefined for [[Call]], the new.target object for [[Construct]].</summary>
    public readonly JsValue NewTarget;
    public readonly JsBuiltin Callee;

    public NativeArgs(Realm realm, JsValue thisValue, ReadOnlySpan<JsValue> args, JsValue newTarget, JsBuiltin callee)
    {
        Realm = realm;
        This = thisValue;
        Args = args;
        NewTarget = newTarget;
        Callee = callee;
    }

    public JsValue this[int i]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (uint)i < (uint)Args.Length ? Args[i] : default;
    }

    public int Length => Args.Length;
    public bool IsConstruct => !NewTarget.IsUndefined;
}

internal delegate JsValue NativeFn(in NativeArgs args);

/// <summary>Builtin or host function implemented in C#.</summary>
internal sealed class JsBuiltin : JsFunctionObject
{
    public readonly NativeFn Fn;
    public readonly bool Constructor;
    public readonly string Name;
    public object? State;
    /// <summary>Set for host functions that start asynchronous host work (rejected in synchronous executions).</summary>
    public bool IsAsyncHost;

    public JsBuiltin(Realm realm, string name, int length, NativeFn fn, bool constructor = false, JsObject? proto = null)
        : base(realm, realm.FunctionShapeFor(proto ?? realm.FunctionPrototype), 3)
    {
        Fn = fn;
        Constructor = constructor;
        Name = name;
        Slots[0] = new JsValue(length);
        Slots[1] = new JsValue(name);
    }

    public override bool IsConstructor => Constructor;
    public override string DebugName => Name;

    // A builtin that raised an error through Vm.Raise returns JsValue.Empty; the interpreter dispatches that to a JS
    // handler directly, every other caller gets the CLR exception it expects.
    public override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        var r = Fn(new NativeArgs(Realm, thisValue, args, JsValue.Undefined, this));
        if (r.IsEmpty) ThrowPending();
        return r;
    }

    public override JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget)
    {
        if (!Constructor) throw JsErrors.TypeError(Realm, $"{Name} is not a constructor");
        var r = Fn(new NativeArgs(Realm, JsValue.Undefined, args, JsValue.FromObject(newTarget), this));
        if (r.IsEmpty) ThrowPending();
        return r;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowPending() => throw new JsThrowException(Realm.Vm.TakePending());
}

internal sealed class JsBoundFunction : JsFunctionObject
{
    public readonly JsFunctionObject Target;
    public readonly JsValue BoundThis;
    public readonly JsValue[] BoundArgs;

    public JsBoundFunction(Realm realm, JsFunctionObject target, JsValue boundThis, JsValue[] boundArgs)
        : base(realm, target.GetPrototypeOf())
    {
        Target = target;
        BoundThis = boundThis;
        BoundArgs = boundArgs;
    }

    public override bool IsConstructor => Target.IsConstructor;
    public override string DebugName => "bound " + Target.DebugName;

    private JsValue[] Merge(ReadOnlySpan<JsValue> args)
    {
        var all = new JsValue[BoundArgs.Length + args.Length];
        BoundArgs.CopyTo(all, 0);
        args.CopyTo(all.AsSpan(BoundArgs.Length));
        return all;
    }

    public override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        Realm.Vm.EnsureNativeStack();
        return BoundArgs.Length == 0 ? Target.Call(BoundThis, args) : Target.Call(BoundThis, Merge(args));
    }

    public override JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget)
    {
        Realm.Vm.EnsureNativeStack();
        if (ReferenceEquals(newTarget, this)) newTarget = Target;
        return BoundArgs.Length == 0 ? Target.Construct(args, newTarget) : Target.Construct(Merge(args), newTarget);
    }
}

/// <summary>Private name identity (#x) created per class evaluation.</summary>
internal sealed class JsPrivateName
{
    public readonly string Description;
    public readonly int Kind; // 0 field, 1 method, 2 accessor
    public JsObject? Method;
    public JsObject? Getter;
    public JsObject? Setter;
    public JsPrivateName(string description, int kind) { Description = description; Kind = kind; }
}

/// <summary>Boolean/Number/Symbol wrapper objects.</summary>
internal sealed class JsPrimitiveWrapper : JsObject
{
    public readonly JsValue Value;
    public JsPrimitiveWrapper(Realm realm, JsObject proto, JsValue value) : base(realm, proto) => Value = value;
    public override string ClassName => Value.Tag switch { JsTag.Boolean => "Boolean", JsTag.Number => "Number", JsTag.BigInt => "BigInt", _ => "Symbol" };
}

/// <summary>String exotic object.</summary>
internal sealed class JsStringObject : JsObject
{
    public readonly string Value;

    public JsStringObject(Realm realm, string value, JsObject? proto = null) : base(realm, proto ?? realm.StringPrototype, exotic: true, uniqueShape: false)
    {
        Value = value;
    }

    public override string ClassName => "String";

    internal override bool CanCacheNamed(in PropertyKey key) => key.Value is string s && s != "length" && !PropertyKey.TryParseArrayIndex(s, out _);

    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc)
    {
        if (key.Value is string s)
        {
            if (s == "length") { desc = PropertyDescriptor.Data(new JsValue(Value.Length), PropFlags.None); return true; }
            if (PropertyKey.TryParseArrayIndex(s, out uint i) && i < (uint)Value.Length)
            {
                desc = PropertyDescriptor.Data(new JsValue(Realm.CharString(Value[(int)i])), PropFlags.Enumerable);
                return true;
            }
        }
        return base.GetOwnProperty(key, out desc);
    }

    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc)
    {
        if (key.Value is string s && (s == "length" || (PropertyKey.TryParseArrayIndex(s, out uint i) && i < (uint)Value.Length)))
        {
            GetOwnProperty(key, out var cur);
            // only compatible no-op redefinitions are allowed
            if (desc.HasConfigurable && desc.Configurable) return false;
            if (desc.HasWritable && desc.Writable) return false;
            if (desc.HasValue && !Operations.SameValue(desc.Value, cur.Value)) return false;
            if (desc.IsAccessor) return false;
            if (desc.HasEnumerable && desc.Enumerable != cur.Enumerable) return false;
            return true;
        }
        return OrdinaryDefineOwnProperty(key, desc);
    }

    public override JsValue Get(in PropertyKey key, JsValue receiver)
    {
        if (key.Value is string s)
        {
            if (s == "length") return new JsValue(Value.Length);
            if (PropertyKey.TryParseArrayIndex(s, out uint i) && i < (uint)Value.Length) return new JsValue(Realm.CharString(Value[(int)i]));
        }
        return OrdinaryGet(key, receiver);
    }

    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver)
    {
        if (key.Value is string s && (s == "length" || (PropertyKey.TryParseArrayIndex(s, out uint i) && i < (uint)Value.Length))) return false;
        return OrdinarySet(key, value, receiver);
    }

    public override bool HasProperty(in PropertyKey key)
    {
        if (GetOwnProperty(key, out _)) return true;
        return Proto?.HasProperty(key) ?? false;
    }

    public override bool Delete(in PropertyKey key)
    {
        if (key.Value is string s && (s == "length" || (PropertyKey.TryParseArrayIndex(s, out uint i) && i < (uint)Value.Length))) return false;
        return base.Delete(key);
    }

    public override void OwnPropertyKeys(System.Collections.Generic.List<PropertyKey> keys)
    {
        for (int i = 0; i < Value.Length; i++) keys.Add(PropertyKey.FromIndex((uint)i));
        var rest = new System.Collections.Generic.List<PropertyKey>();
        OrdinaryOwnKeys(rest);
        // integer keys of the ordinary part must come after string indices but in ascending order
        foreach (var k in rest) if (k.TryGetArrayIndex(out _)) keys.Add(k);
        keys.Add("length");
        foreach (var k in rest) if (!k.TryGetArrayIndex(out _)) keys.Add(k);
    }
}

/// <summary>this-binding of a derived constructor shared with the arrow functions it creates.</summary>
internal sealed class ThisBox
{
    public JsValue Value;
    public required JsClosure Ctor;
    public JsValue NewTarget;
}
