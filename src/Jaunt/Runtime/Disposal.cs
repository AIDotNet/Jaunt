using System;
using System.Collections.Generic;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

/// <summary>DisposeCapability (27.3.1): resources in registration order; disposal runs in reverse.</summary>
internal sealed class DisposeCapability
{
    public readonly List<(JsValue Value, JsValue Method)> Resources = new();
}

/// <summary>Explicit resource management runtime (using / await using, DisposableStack, AsyncDisposableStack).</summary>
internal static class Disposal
{
    /// <summary>AddDisposableResource with CreateDisposableResource / GetDisposeMethod (hint: 0 sync, 1 async).</summary>
    public static void Add(Realm realm, DisposeCapability cap, in JsValue v, int hint)
    {
        if (v.IsNullOrUndefined)
        {
            // sync: nothing to dispose; async: still awaited once at disposal (needsAwait)
            if (hint == 1) cap.Resources.Add((JsValue.Undefined, JsValue.Undefined));
            return;
        }
        if (v.IsNotObject) throw JsErrors.TypeError(realm, "using declarations require an object with a dispose method");
        var method = GetDisposeMethod(realm, v, hint);
        if (method.IsUndefined) throw JsErrors.TypeError(realm, hint == 1 ? "Object does not have a [Symbol.asyncDispose] or [Symbol.dispose] method" : "Object does not have a [Symbol.dispose] method");
        cap.Resources.Add((v, method));
    }

    public static JsValue GetDisposeMethod(Realm realm, in JsValue v, int hint)
    {
        if (hint == 1)
        {
            var am = Operations.GetV(realm, v, JsSymbol.AsyncDispose);
            if (!am.IsNullOrUndefined)
            {
                if (!am.IsFunction) throw JsErrors.TypeError(realm, "[Symbol.asyncDispose] is not a function");
                return am;
            }
            var sm = Operations.GetV(realm, v, JsSymbol.Dispose);
            if (sm.IsNullOrUndefined) return JsValue.Undefined;
            if (!sm.IsFunction) throw JsErrors.TypeError(realm, "[Symbol.dispose] is not a function");
            // wrap: call the sync method, then return a promise (rejected when it throws)
            var wrapper = new JsBuiltin(realm, "", 0, static (in NativeArgs a) =>
            {
                var r = a.Realm;
                var p = new JsPromise(r);
                try
                {
                    r.Vm.Call((JsValue)a.Callee.State!, a.This, ReadOnlySpan<JsValue>.Empty);
                    p.ResolveWith(JsValue.Undefined);
                }
                catch (JsThrowException ex) { p.RejectWith(ex.Value); }
                return JsValue.FromObject(p);
            });
            wrapper.State = sm;
            return JsValue.FromObject(wrapper);
        }
        var m = Operations.GetV(realm, v, JsSymbol.Dispose);
        if (m.IsNullOrUndefined) return JsValue.Undefined;
        if (!m.IsFunction) throw JsErrors.TypeError(realm, "[Symbol.dispose] is not a function");
        return m;
    }

    public static JsValue Suppressed(Realm realm, in JsValue error, in JsValue suppressed)
    {
        var e = JsError.Create(realm, ErrorKind.SuppressedError, "An error was suppressed during disposal.");
        e.DefineHidden("error", error);
        e.DefineHidden("suppressed", suppressed);
        return JsValue.FromObject(e);
    }

    /// <summary>Synchronous DisposeResources. Returns the new completion: (isThrow, value).</summary>
    public static (bool Throw, JsValue Value) DisposeSync(Realm realm, DisposeCapability cap, bool isThrow, JsValue value)
    {
        var list = cap.Resources;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var (v, method) = list[i];
            if (method.IsUndefined) continue;
            try { realm.Vm.Call(method, v, ReadOnlySpan<JsValue>.Empty); }
            catch (JsThrowException ex)
            {
                value = isThrow ? Suppressed(realm, ex.Value, value) : ex.Value;
                isThrow = true;
            }
        }
        list.Clear();
        return (isThrow, value);
    }
}

internal sealed partial class Vm
{
    private int DisposeOp(Frame f, int pc)
    {
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        var realm = Realm;
        switch ((Op)code[pc])
        {
            case Op.NewDisposeStack:
                regs[bp + code[pc + 1]] = new JsValue(JsTag.Empty, 0, new DisposeCapability());
                return pc + 2;
            case Op.AddDisposable:
                Disposal.Add(realm, (DisposeCapability)regs[bp + code[pc + 1]].Ref!, regs[bp + code[pc + 2]], code[pc + 3]);
                return pc + 4;
            case Op.DisposeSync:
            {
                var cap = (DisposeCapability)regs[bp + code[pc + 1]].Ref!;
                ref var type = ref regs[bp + code[pc + 2]];
                ref var value = ref regs[bp + code[pc + 3]];
                bool wasThrow = type.IsNumber && type.Num == 1;
                var (isThrow, v) = Disposal.DisposeSync(realm, cap, wasThrow, value);
                if (isThrow)
                {
                    type = new JsValue(1);
                    value = v;
                }
                return pc + 4;
            }
            case Op.DisposeNext:
            {
                var cap = (DisposeCapability)regs[bp + code[pc + 3]].Ref!;
                var list = cap.Resources;
                if (list.Count == 0) return code[pc + 4];
                var (v, method) = list[^1];
                list.RemoveAt(list.Count - 1);
                regs[bp + code[pc + 1]] = method;
                regs[bp + code[pc + 2]] = v;
                return pc + 5;
            }
            case Op.DisposeMerge:
            {
                ref var type = ref regs[bp + code[pc + 1]];
                ref var value = ref regs[bp + code[pc + 2]];
                var error = regs[bp + code[pc + 3]];
                bool wasThrow = type.IsNumber && type.Num == 1;
                value = wasThrow ? Disposal.Suppressed(realm, error, value) : error;
                type = new JsValue(1);
                return pc + 4;
            }
            default:
                throw new InvalidOperationException("Jaunt internal error: not a dispose opcode.");
        }
    }
}

internal sealed class JsDisposableStack : JsObject
{
    public DisposeCapability Capability = new();
    public bool Disposed;
    public readonly bool Async;
    public JsDisposableStack(Realm realm, JsObject proto, bool async) : base(realm, proto) => Async = async;
    public override string ClassName => Async ? "AsyncDisposableStack" : "DisposableStack";
}

/// <summary>DisposableStack / AsyncDisposableStack constructors and Iterator dispose methods.</summary>
internal static class DisposableStackBuiltins
{
    public static void Setup(Realm r)
    {
        SetupStack(r, async: false);
        SetupStack(r, async: true);
        r.Method(r.IteratorPrototype, JsSymbol.Dispose, "[Symbol.dispose]", 0, static (in NativeArgs a) =>
        {
            var ret = Operations.GetV(a.Realm, a.This, "return");
            if (!ret.IsNullOrUndefined)
            {
                if (!ret.IsFunction) throw JsErrors.TypeError(a.Realm, "iterator.return is not a function");
                a.Realm.Vm.Call(ret, a.This, ReadOnlySpan<JsValue>.Empty);
            }
            return JsValue.Undefined;
        });
        r.Method(r.AsyncIteratorPrototype, JsSymbol.AsyncDispose, "[Symbol.asyncDispose]", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var p = new JsPromise(realm);
            try
            {
                var ret = Operations.GetV(realm, a.This, "return");
                if (ret.IsNullOrUndefined) { p.ResolveWith(JsValue.Undefined); return JsValue.FromObject(p); }
                if (!ret.IsFunction) throw JsErrors.TypeError(realm, "iterator.return is not a function");
                var result = realm.Vm.Call(ret, a.This, ReadOnlySpan<JsValue>.Empty);   // called with no arguments
                JsPromise.Resolve(realm, result).Then(new ResolveUndefined(p));
            }
            catch (JsThrowException ex) { p.RejectWith(ex.Value); }
            return JsValue.FromObject(p);
        });
    }

    private sealed class ResolveUndefined : IPromiseContinuation
    {
        private readonly JsPromise _p;
        public ResolveUndefined(JsPromise p) => _p = p;
        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            if (fulfilled) _p.ResolveWith(JsValue.Undefined); else _p.RejectWith(value);
        }
    }

    private static JsDisposableStack This(in NativeArgs a, bool async, string method)
    {
        if (a.This.Ref is JsDisposableStack s && s.Async == async) return s;
        throw JsErrors.TypeError(a.Realm, $"{(async ? "AsyncDisposableStack" : "DisposableStack")}.prototype.{method} called on incompatible receiver");
    }

    private static void SetupStack(Realm r, bool async)
    {
        string name = async ? "AsyncDisposableStack" : "DisposableStack";
        var proto = new JsObject(r, r.ObjectPrototype);
        if (async) r.AsyncDisposableStackPrototype = proto; else r.DisposableStackPrototype = proto;
        var ctor = r.Constructor(name, 0, async
            ? static (in NativeArgs a) =>
            {
                if (!a.IsConstruct) throw JsErrors.TypeError(a.Realm, "Constructor AsyncDisposableStack requires 'new'");
                var p = Operations.GetPrototypeFromConstructor(a.Realm, a.NewTarget.AsObjectUnchecked, a.Realm.AsyncDisposableStackPrototype!);
                return JsValue.FromObject(new JsDisposableStack(a.Realm, p, true));
            }
            : static (in NativeArgs a) =>
            {
                if (!a.IsConstruct) throw JsErrors.TypeError(a.Realm, "Constructor DisposableStack requires 'new'");
                var p = Operations.GetPrototypeFromConstructor(a.Realm, a.NewTarget.AsObjectUnchecked, a.Realm.DisposableStackPrototype!);
                return JsValue.FromObject(new JsDisposableStack(a.Realm, p, false));
            }, proto);
        r.DefineGlobal(name, JsValue.FromObject(ctor));
        var useFn = async
            ? r.MethodEager(proto, "use", 1, static (in NativeArgs a) => Use(a, true))
            : r.MethodEager(proto, "use", 1, static (in NativeArgs a) => Use(a, false));
        _ = useFn;
        if (async)
        {
            r.Method(proto, "adopt", 2, static (in NativeArgs a) => Adopt(a, true));
            r.Method(proto, "defer", 1, static (in NativeArgs a) => Defer(a, true));
            r.Method(proto, "move", 0, static (in NativeArgs a) => Move(a, true));
            r.Getter(proto, "disposed", "disposed", static (in NativeArgs a) => JsValue.FromBool(This(a, true, "disposed").Disposed));
            var dispose = r.MethodEager(proto, "disposeAsync", 0, static (in NativeArgs a) => DisposeAsync(a));
            proto.AddOwnProperty(JsSymbol.AsyncDispose, JsValue.FromObject(dispose), PropFlags.Hidden);
        }
        else
        {
            r.Method(proto, "adopt", 2, static (in NativeArgs a) => Adopt(a, false));
            r.Method(proto, "defer", 1, static (in NativeArgs a) => Defer(a, false));
            r.Method(proto, "move", 0, static (in NativeArgs a) => Move(a, false));
            r.Getter(proto, "disposed", "disposed", static (in NativeArgs a) => JsValue.FromBool(This(a, false, "disposed").Disposed));
            var dispose = r.MethodEager(proto, "dispose", 0, static (in NativeArgs a) =>
            {
                var realm = a.Realm;
                var s = This(a, false, "dispose");
                if (s.Disposed) return JsValue.Undefined;
                s.Disposed = true;
                var (isThrow, v) = Disposal.DisposeSync(realm, s.Capability, false, JsValue.Undefined);
                if (isThrow) throw new JsThrowException(v);
                return JsValue.Undefined;
            });
            proto.AddOwnProperty(JsSymbol.Dispose, JsValue.FromObject(dispose), PropFlags.Hidden);
        }
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue(name), PropFlags.Configurable);
    }

    private static JsValue Use(in NativeArgs a, bool async)
    {
        var realm = a.Realm;
        var s = This(a, async, "use");
        if (s.Disposed) throw JsErrors.ReferenceError(realm, "Cannot use a disposed stack");
        Disposal.Add(realm, s.Capability, a[0], async ? 1 : 0);
        return a[0];
    }

    private static JsValue Adopt(in NativeArgs a, bool async)
    {
        var realm = a.Realm;
        var s = This(a, async, "adopt");
        if (s.Disposed) throw JsErrors.ReferenceError(realm, "Cannot adopt into a disposed stack");
        if (!a[1].IsFunction) throw JsErrors.TypeError(realm, "onDispose must be a function");
        var closure = new JsBuiltin(realm, "", 0, static (in NativeArgs b) =>
        {
            var (value, onDispose) = ((JsValue, JsValue))b.Callee.State!;
            return b.Realm.Vm.Call(onDispose, JsValue.Undefined, new ReadOnlySpan<JsValue>(in value));
        });
        closure.State = (a[0], a[1]);
        s.Capability.Resources.Add((JsValue.Undefined, JsValue.FromObject(closure)));
        return a[0];
    }

    private static JsValue Defer(in NativeArgs a, bool async)
    {
        var realm = a.Realm;
        var s = This(a, async, "defer");
        if (s.Disposed) throw JsErrors.ReferenceError(realm, "Cannot defer on a disposed stack");
        if (!a[0].IsFunction) throw JsErrors.TypeError(realm, "onDispose must be a function");
        s.Capability.Resources.Add((JsValue.Undefined, a[0]));
        return JsValue.Undefined;
    }

    private static JsValue Move(in NativeArgs a, bool async)
    {
        var realm = a.Realm;
        var s = This(a, async, "move");
        if (s.Disposed) throw JsErrors.ReferenceError(realm, "Cannot move a disposed stack");
        var n = new JsDisposableStack(realm, async ? realm.AsyncDisposableStackPrototype! : realm.DisposableStackPrototype!, async)
        {
            Capability = s.Capability,
        };
        s.Capability = new DisposeCapability();
        s.Disposed = true;
        return JsValue.FromObject(n);
    }

    private static JsValue DisposeAsync(in NativeArgs a)
    {
        var realm = a.Realm;
        var p = new JsPromise(realm);
        if (a.This.Ref is not JsDisposableStack { Async: true } s)
        {
            p.RejectWith(JsErrors.TypeError(realm, "AsyncDisposableStack.prototype.disposeAsync called on incompatible receiver").Value);
            return JsValue.FromObject(p);
        }
        if (s.Disposed) { p.ResolveWith(JsValue.Undefined); return JsValue.FromObject(p); }
        s.Disposed = true;
        new AsyncDisposer(realm, s.Capability, p).Next();
        return JsValue.FromObject(p);
    }

    /// <summary>Async DisposeResources as a promise continuation chain.</summary>
    private sealed class AsyncDisposer : IPromiseContinuation
    {
        private readonly Realm _realm;
        private readonly DisposeCapability _cap;
        private readonly JsPromise _result;
        private bool _isThrow;
        private JsValue _error;

        public AsyncDisposer(Realm realm, DisposeCapability cap, JsPromise result)
        {
            _realm = realm;
            _cap = cap;
            _result = result;
        }

        public void Next()
        {
            var list = _cap.Resources;
            if (list.Count == 0)
            {
                if (_isThrow) _result.RejectWith(_error); else _result.ResolveWith(JsValue.Undefined);
                return;
            }
            var (v, method) = list[^1];
            list.RemoveAt(list.Count - 1);
            JsValue r = JsValue.Undefined;
            if (!method.IsUndefined)
            {
                try { r = _realm.Vm.Call(method, v, ReadOnlySpan<JsValue>.Empty); }
                catch (JsThrowException ex) { Record(ex.Value); Next(); return; }
            }
            JsPromise.Resolve(_realm, r).Then(this);
        }

        private void Record(JsValue error)
        {
            _error = _isThrow ? Disposal.Suppressed(_realm, error, _error) : error;
            _isThrow = true;
        }

        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            if (!fulfilled) Record(value);
            Next();
        }
    }
}
