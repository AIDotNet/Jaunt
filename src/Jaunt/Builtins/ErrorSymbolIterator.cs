using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal static class ErrorBuiltins
{
    public static void Setup(Realm r)
    {
        var baseProto = new JsObject(r, r.ObjectPrototype);
        var protos = new JsObject[9];
        r.SetErrorPrototypes(protos);
        protos[(int)ErrorKind.Error] = baseProto;
        var baseCtor = r.Constructor("Error", 1, static (in NativeArgs a) => Construct(a, ErrorKind.Error), baseProto);
        baseProto.DefineHidden("name", new JsValue("Error"));
        baseProto.DefineHidden("message", JsValue.EmptyString);
        r.Method(baseProto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "Error.prototype.toString called on non-object");
            var o = a.This.AsObjectUnchecked;
            var n = o.Get("name");
            var m = o.Get("message");
            string name = n.IsUndefined ? "Error" : Operations.ToString(realm, n);
            string msg = m.IsUndefined ? "" : Operations.ToString(realm, m);
            if (name.Length == 0) return new JsValue(msg);
            if (msg.Length == 0) return new JsValue(name);
            return new JsValue(name + ": " + msg);
        });
        // proposal-error-stack-accessor: Error.prototype.stack is an accessor over [[ErrorData]]
        r.Getter(baseProto, "stack", "stack", static (in NativeArgs a) =>
        {
            if (a.This.IsNotObject) throw JsErrors.TypeError(a.Realm, "Error.prototype.stack getter called on non-object");
            return a.This.Ref is JsError e ? new JsValue(e.StackString) : JsValue.Undefined;
        }, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "Error.prototype.stack setter called on non-object");
            if (a.Length == 0) throw JsErrors.TypeError(realm, "Error.prototype.stack setter requires an argument");
            if (!a[0].IsString) throw JsErrors.TypeError(realm, "Error.prototype.stack must be a string");
            // SetterThatIgnoresPrototypeProperties(this, %Error.prototype%, "stack", value)
            var o = a.This.AsObjectUnchecked;
            if (ReferenceEquals(o, realm.ErrorPrototypes[(int)ErrorKind.Error])) throw JsErrors.TypeError(realm, "Cannot set stack on Error.prototype");
            if (!o.GetOwnProperty("stack", out _)) o.CreateDataPropertyOrThrow("stack", a[0]);
            else if (!o.Set("stack", a[0], a.This)) throw JsErrors.TypeError(realm, "Cannot assign to read only property 'stack'");
            return JsValue.Undefined;
        });
        r.DefineGlobal("Error", JsValue.FromObject(baseCtor));
        Es2025Builtins.InstallError(r, baseCtor);
        r.Method(baseCtor, "captureStackTrace", 1, static (in NativeArgs a) =>
        {
            if (a[0].IsObject)
                a[0].AsObjectUnchecked.CreateDataProperty("stack", new JsValue(a.Realm.Vm.CaptureStackTrace().TrimStart('\n')));
            return JsValue.Undefined;
        });

        void Native(ErrorKind kind, NativeFn fn, int length = 1)
        {
            var p = new JsObject(r, baseProto);
            protos[(int)kind] = p;
            var c = r.Constructor(kind.ToString(), length, fn, p, baseCtor);
            p.DefineHidden("name", new JsValue(kind.ToString()));
            p.DefineHidden("message", JsValue.EmptyString);
            r.DefineGlobal(kind.ToString(), JsValue.FromObject(c));
        }
        Native(ErrorKind.TypeError, static (in NativeArgs a) => Construct(a, ErrorKind.TypeError));
        Native(ErrorKind.RangeError, static (in NativeArgs a) => Construct(a, ErrorKind.RangeError));
        Native(ErrorKind.ReferenceError, static (in NativeArgs a) => Construct(a, ErrorKind.ReferenceError));
        Native(ErrorKind.SyntaxError, static (in NativeArgs a) => Construct(a, ErrorKind.SyntaxError));
        Native(ErrorKind.EvalError, static (in NativeArgs a) => Construct(a, ErrorKind.EvalError));
        Native(ErrorKind.URIError, static (in NativeArgs a) => Construct(a, ErrorKind.URIError));
        Native(ErrorKind.AggregateError, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var e = CreateFromArgs(a, ErrorKind.AggregateError, a[1], a[2]);
            var errors = Operations.IterableToList(realm, a[0]);
            e.DefineHidden("errors", JsValue.FromObject(Operations.CreateArrayFromList(realm, errors.ToArray())));
            return JsValue.FromObject(e);
        }, 2);
        Native(ErrorKind.SuppressedError, static (in NativeArgs a) =>
        {
            var e = CreateFromArgs(a, ErrorKind.SuppressedError, a[2], JsValue.Undefined, captureStack: false);
            e.DefineHidden("error", a[0]);
            e.DefineHidden("suppressed", a[1]);
            e.CaptureStack();   // implementation-defined `stack` goes after the spec-defined properties
            return JsValue.FromObject(e);
        }, 3);
    }

    private static JsValue Construct(in NativeArgs a, ErrorKind kind) => JsValue.FromObject(CreateFromArgs(a, kind, a[0], a[1]));

    private static JsError CreateFromArgs(in NativeArgs a, ErrorKind kind, JsValue message, JsValue options, bool captureStack = true)
    {
        var realm = a.Realm;
        var nt = a.IsConstruct ? a.NewTarget.AsObjectUnchecked : a.Callee;
        var proto = Operations.GetPrototypeFromConstructor(realm, nt, realm.ErrorPrototypes[(int)kind]);
        string msg = message.IsUndefined ? "" : Operations.ToString(realm, message);
        var e = new JsError(realm, proto, kind.ToString(), msg);
        if (!message.IsUndefined) e.DefineHidden("message", new JsValue(msg));
        if (options.IsObject && options.AsObjectUnchecked.HasProperty("cause"))
            e.DefineHidden("cause", options.AsObjectUnchecked.Get("cause"));
        var nameV = proto.Get("name");
        if (nameV.IsString) e.Name = nameV.AsStringUnchecked;
        if (captureStack) e.CaptureStack();
        return e;
    }
}

internal static class SymbolBuiltins
{
    public static void Setup(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        r.SymbolPrototype = proto;
        var ctor = r.Constructor("Symbol", 0, static (in NativeArgs a) =>
        {
            if (a.IsConstruct) throw JsErrors.TypeError(a.Realm, "Symbol is not a constructor");
            string? desc = a[0].IsUndefined ? null : Operations.ToString(a.Realm, a[0]);
            return JsValue.FromSymbol(new JsSymbol(desc));
        }, proto);
        r.DefineGlobal("Symbol", JsValue.FromObject(ctor));
        foreach (var (name, sym) in new[]
        {
            ("iterator", JsSymbol.Iterator), ("asyncIterator", JsSymbol.AsyncIterator), ("hasInstance", JsSymbol.HasInstance),
            ("toPrimitive", JsSymbol.ToPrimitive), ("toStringTag", JsSymbol.ToStringTag), ("species", JsSymbol.Species),
            ("isConcatSpreadable", JsSymbol.IsConcatSpreadable), ("unscopables", JsSymbol.Unscopables), ("match", JsSymbol.Match),
            ("matchAll", JsSymbol.MatchAll), ("replace", JsSymbol.Replace), ("search", JsSymbol.Search), ("split", JsSymbol.Split),
            ("dispose", JsSymbol.Dispose), ("asyncDispose", JsSymbol.AsyncDispose),
            ("metadata", JsSymbol.Metadata),
        })
            ctor.AddOwnProperty(name, JsValue.FromSymbol(sym), PropFlags.None);
        r.Method(ctor, "for", 1, static (in NativeArgs a) =>
        {
            var key = Operations.ToString(a.Realm, a[0]);
            if (!a.Realm.SymbolRegistry.TryGetValue(key, out var s))
            {
                s = new JsSymbol(key, key);
                a.Realm.SymbolRegistry[key] = s;
            }
            return JsValue.FromSymbol(s);
        });
        r.Method(ctor, "keyFor", 1, static (in NativeArgs a) =>
        {
            if (a[0].Tag != JsTag.Symbol) throw JsErrors.TypeError(a.Realm, $"{a[0]} is not a symbol");
            var s = (JsSymbol)a[0].Ref!;
            return s.RegistryKey == null ? JsValue.Undefined : new JsValue(s.RegistryKey);
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) => new JsValue(ThisSymbol(a).ToString()));
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => JsValue.FromSymbol(ThisSymbol(a)));
        r.Getter(proto, "description", "description", static (in NativeArgs a) =>
        {
            var s = ThisSymbol(a);
            return s.Description == null ? JsValue.Undefined : new JsValue(s.Description);
        });
        r.Method(proto, JsSymbol.ToPrimitive, "[Symbol.toPrimitive]", 1, static (in NativeArgs a) => JsValue.FromSymbol(ThisSymbol(a)), PropFlags.Configurable);
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Symbol"), PropFlags.Configurable);
    }

    private static JsSymbol ThisSymbol(in NativeArgs a)
    {
        if (a.This.IsSymbol) return (JsSymbol)a.This.Ref!;
        if (a.This.Ref is JsPrimitiveWrapper w && w.Value.IsSymbol) return (JsSymbol)w.Value.Ref!;
        throw JsErrors.TypeError(a.Realm, "Symbol.prototype method called on incompatible receiver");
    }
}

/// <summary>%ArrayIteratorPrototype% instances.</summary>
internal sealed class JsArrayIterator : JsObject
{
    public JsObject? Target;
    public uint Index;
    public byte Kind; // 0 keys, 1 values, 2 entries
    public JsArrayIterator(Realm realm, JsObject target, byte kind) : base(realm, realm.ArrayIteratorPrototype)
    {
        Target = target;
        Kind = kind;
    }
}

internal sealed class JsStringIterator : JsObject
{
    public string? Value;
    public int Index;
    public JsStringIterator(Realm realm, string s) : base(realm, realm.StringIteratorPrototype) => Value = s;
}

internal static class IteratorBuiltins
{
    public static void Setup(Realm r)
    {
        var iterProto = new JsObject(r, r.ObjectPrototype);
        r.IteratorPrototype = iterProto;
        r.Method(iterProto, JsSymbol.Iterator, "[Symbol.iterator]", 0, static (in NativeArgs a) => a.This);

        var arrayIter = new JsObject(r, iterProto);
        r.ArrayIteratorPrototype = arrayIter;
        r.ArrayIteratorNext = r.MethodEager(arrayIter, "next", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is not JsArrayIterator it) throw JsErrors.TypeError(realm, "next method called on incompatible receiver");
            var target = it.Target;
            if (target == null) return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
            if (target is JsTypedArray tao && tao.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
            long len = target is JsTypedArray ta ? ta.Length : Operations.LengthOfArrayLike(realm, target);
            if (it.Index >= len)
            {
                it.Target = null;
                return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
            }
            uint i = it.Index++;
            switch (it.Kind)
            {
                case 0: return Coroutines.CreateIterResult(realm, new JsValue(i), false);
                case 1: return Coroutines.CreateIterResult(realm, target.GetIndex(i, JsValue.FromObject(target)), false);
                default:
                    var pair = realm.NewArray(2);
                    pair.Push(new JsValue(i));
                    pair.Push(target.GetIndex(i, JsValue.FromObject(target)));
                    return Coroutines.CreateIterResult(realm, JsValue.FromObject(pair), false);
            }
        });
        arrayIter.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Array Iterator"), PropFlags.Configurable);

        var stringIter = new JsObject(r, iterProto);
        r.StringIteratorPrototype = stringIter;
        r.StringIteratorNext = r.MethodEager(stringIter, "next", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is not JsStringIterator it) throw JsErrors.TypeError(realm, "next method called on incompatible receiver");
            var s = it.Value;
            if (s == null || it.Index >= s.Length)
            {
                it.Value = null;
                return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
            }
            int i = it.Index;
            int len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            it.Index += len;
            return Coroutines.CreateIterResult(realm, new JsValue(len == 1 ? realm.CharString(s[i]) : s.Substring(i, 2)), false);
        });
        stringIter.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("String Iterator"), PropFlags.Configurable);

        // generators
        var genProto = new JsObject(r, iterProto);
        r.GeneratorPrototype = genProto;
        r.GeneratorFunctionPrototype.AddOwnProperty("prototype", JsValue.FromObject(genProto), PropFlags.Configurable);
        genProto.AddOwnProperty("constructor", JsValue.FromObject(r.GeneratorFunctionPrototype), PropFlags.Configurable);
        r.Method(genProto, "next", 1, static (in NativeArgs a) => Coroutines.Resume(a.Realm, a.This, Coroutines.ModeNext, a[0], "Generator.prototype.next"));
        r.Method(genProto, "return", 1, static (in NativeArgs a) => Coroutines.Resume(a.Realm, a.This, Coroutines.ModeReturn, a[0], "Generator.prototype.return"));
        r.Method(genProto, "throw", 1, static (in NativeArgs a) => Coroutines.Resume(a.Realm, a.This, Coroutines.ModeThrow, a[0], "Generator.prototype.throw"));
        genProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Generator"), PropFlags.Configurable);
        AsyncGenerators.Setup(r);
        IteratorHelpers.Setup(r, iterProto);
        IteratorProposals.Setup(r, iterProto);
        DisposableStackBuiltins.Setup(r);
    }
}
