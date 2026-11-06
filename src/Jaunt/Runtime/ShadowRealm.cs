using System;
using System.Collections.Generic;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

/// <summary>ShadowRealm instance: an object with a [[ShadowRealm]] slot (a separate realm on the same agent).</summary>
internal sealed class JsShadowRealm : JsObject
{
    public readonly Realm Inner;
    public JsShadowRealm(Realm realm, JsObject proto, Realm inner) : base(realm, proto) => Inner = inner;
    public override string ClassName => "ShadowRealm";
}

/// <summary>
/// Wrapped function exotic object: a callable boundary between two realms. Only primitives and callables cross it
/// (callables are wrapped again); everything else, and every abrupt completion, becomes a TypeError of the caller.
/// </summary>
internal sealed class JsWrappedFunction : JsFunctionObject
{
    public readonly JsFunctionObject Target;

    private JsWrappedFunction(Realm callerRealm, JsFunctionObject target)
        : base(callerRealm, callerRealm.FunctionShapeFor(callerRealm.FunctionPrototype), 3)
    {
        Target = target;
        Slots[0] = new JsValue(0);
        Slots[1] = new JsValue("");
    }

    public override string DebugName => "wrapped " + Target.DebugName;

    /// <summary>WrappedFunctionCreate: <paramref name="callerRealm"/> is the realm the function is created in.</summary>
    public static JsWrappedFunction Create(Realm callerRealm, JsFunctionObject target)
    {
        var f = new JsWrappedFunction(callerRealm, target);
        try { f.CopyNameAndLength(); }
        catch (JsThrowException ex) { throw ShadowRealms.BoundaryError(callerRealm, ex, "Cannot wrap the function"); }
        return f;
    }

    /// <summary>CopyNameAndLength(F, Target) with no prefix and argCount 0: observable order HasOwnProperty(length), Get(length), Get(name).</summary>
    private void CopyNameAndLength()
    {
        var realm = Realm;
        double length = 0;
        if (Target.GetOwnProperty("length", out _))
        {
            var targetLen = Target.Get("length");
            if (targetLen.IsNumber)
            {
                double d = targetLen.AsNumber();
                if (double.IsPositiveInfinity(d)) length = d;
                else if (double.IsNegativeInfinity(d)) length = 0;
                else length = Math.Max(Operations.ToIntegerOrInfinity(realm, targetLen), 0);
            }
        }
        // SetFunctionLength: { [[Writable]]: false, [[Enumerable]]: false, [[Configurable]]: true }
        Slots[0] = new JsValue(length);
        var targetName = Target.Get("name");
        Slots[1] = targetName.IsString ? targetName : new JsValue("");
    }

    public override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        var callerRealm = Realm;
        Realm targetRealm;
        try { targetRealm = Operations.GetFunctionRealm(callerRealm, Target); }
        catch (JsThrowException ex) { throw ShadowRealms.BoundaryError(callerRealm, ex, "Cannot call the wrapped function"); }
        var wrappedArgs = args.Length == 0 ? Array.Empty<JsValue>() : new JsValue[args.Length];
        for (int i = 0; i < args.Length; i++) wrappedArgs[i] = ShadowRealms.GetWrappedValue(callerRealm, targetRealm, args[i]);
        var wrappedThis = ShadowRealms.GetWrappedValue(callerRealm, targetRealm, thisValue);
        JsValue result;
        try { result = Target.Call(wrappedThis, wrappedArgs); }
        catch (JsThrowException ex) { throw ShadowRealms.BoundaryError(callerRealm, ex, "The wrapped function threw"); }
        return ShadowRealms.GetWrappedValue(callerRealm, callerRealm, result);
    }
}

internal static class ShadowRealms
{
    /// <summary>The TypeError of <paramref name="realm"/> that replaces an abrupt completion crossing a realm boundary.</summary>
    public static JsThrowException BoundaryError(Realm realm, JsThrowException inner, string what)
    {
        // never run user code here: only describe errors and primitives
        string detail = inner.Value.Ref is JsError e ? e.Name + ": " + e.MessageText : inner.Value.IsObject ? "an object" : inner.Value.ToString();
        return JsErrors.TypeError(realm, $"{what} ({detail})");
    }

    /// <summary>GetWrappedValue(realm, value); <paramref name="currentRealm"/> provides the TypeError for non-callable objects.</summary>
    public static JsValue GetWrappedValue(Realm currentRealm, Realm realm, JsValue value)
    {
        if (value.IsNotObject) return value;
        if (value.Ref is not JsFunctionObject fn)
            throw JsErrors.TypeError(currentRealm, "Only primitives and callable objects can cross a ShadowRealm boundary");
        return JsValue.FromObject(JsWrappedFunction.Create(realm, fn));
    }

    public static void Setup(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        r.ShadowRealmPrototype = proto;
        var ctor = r.Constructor("ShadowRealm", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor ShadowRealm requires 'new'");
            // like eval, ShadowRealm compiles code from strings: disabled by the Hardened profile
            if (realm.Hardened) throw JsErrors.TypeError(realm, "ShadowRealm is disabled (Hardened profile)");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.ShadowRealmPrototype!);
            // CreateRealm + SetDefaultGlobalBindings; HostInitializeShadowRealm adds nothing
            var inner = new Realm(false, realm);
            return JsValue.FromObject(new JsShadowRealm(realm, p, inner));
        }, proto);
        r.DefineGlobal("ShadowRealm", JsValue.FromObject(ctor));
        r.Method(proto, "evaluate", 1, static (in NativeArgs a) =>
        {
            var callerRealm = a.Realm;
            if (a.This.Ref is not JsShadowRealm sr) throw JsErrors.TypeError(callerRealm, "ShadowRealm.prototype.evaluate called on incompatible receiver");
            if (!a[0].IsString) throw JsErrors.TypeError(callerRealm, "ShadowRealm.prototype.evaluate: source text must be a string");
            return callerRealm.Vm.ShadowRealmEval(a[0].AsStringUnchecked, callerRealm, sr.Inner);
        });
        r.Method(proto, "importValue", 2, static (in NativeArgs a) =>
        {
            var callerRealm = a.Realm;
            if (a.This.Ref is not JsShadowRealm sr) throw JsErrors.TypeError(callerRealm, "ShadowRealm.prototype.importValue called on incompatible receiver");
            var specifier = Operations.ToString(callerRealm, a[0]);
            if (!a[1].IsString) throw JsErrors.TypeError(callerRealm, "ShadowRealm.prototype.importValue: export name must be a string");
            return JsValue.FromObject(ImportValue(specifier, a[1].AsStringUnchecked, callerRealm, sr.Inner));
        });
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("ShadowRealm"), PropFlags.Configurable);
    }

    /// <summary>ShadowRealmImportValue: loads the module in the shadow realm; the export crosses the boundary wrapped.</summary>
    private static JsPromise ImportValue(string specifier, string exportName, Realm callerRealm, Realm evalRealm)
    {
        var inner = new JsPromise(evalRealm);
        var outer = new JsPromise(callerRealm);
        var engine = callerRealm.Engine!;
        callerRealm.Vm.EnqueueJob(new CallbackJob(_ =>
        {
            try
            {
                var m = engine.LoadModuleForRealm(evalRealm, specifier);
                Modules.Evaluate(evalRealm, m).Then(new LoadContinuation(inner, m));
            }
            catch (JsThrowException ex) { inner.RejectWith(ex.Value); }
            catch (JaunModuleResolutionException mre) { inner.RejectWith(JsValue.FromObject(JsError.Create(evalRealm, ErrorKind.TypeError, mre.Message))); }
            catch (JsCompileException ce) { inner.RejectWith(JsValue.FromObject(JsError.Create(evalRealm, ErrorKind.SyntaxError, ce.Message))); }
        }));
        inner.Then(new ExportGetter(outer, exportName, callerRealm));
        return outer;
    }

    private sealed class LoadContinuation : IPromiseContinuation
    {
        private readonly JsPromise _promise;
        private readonly ModuleInstance _module;
        public LoadContinuation(JsPromise p, ModuleInstance m) { _promise = p; _module = m; }
        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            if (fulfilled) _promise.ResolveWith(JsValue.FromObject(Modules.GetNamespace(_promise.Realm, _module)));
            else _promise.RejectWith(value);
        }
    }

    /// <summary>The ExportGetter steps; rejections become a TypeError of the caller realm (%ThrowTypeError%).</summary>
    private sealed class ExportGetter : IPromiseContinuation
    {
        private readonly JsPromise _promise;
        private readonly string _exportName;
        private readonly Realm _callerRealm;
        public ExportGetter(JsPromise p, string exportName, Realm callerRealm) { _promise = p; _exportName = exportName; _callerRealm = callerRealm; }
        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            if (!fulfilled)
            {
                var detail = value.Ref is JsError e ? e.Name + ": " + e.MessageText : "import failed";
                _promise.RejectWith(JsValue.FromObject(JsError.Create(_callerRealm, ErrorKind.TypeError, $"ShadowRealm.prototype.importValue: {detail}")));
                return;
            }
            try
            {
                var exports = value.AsObjectUnchecked;
                if (!exports.GetOwnProperty(_exportName, out _))
                    throw JsErrors.TypeError(_callerRealm, $"The module does not export '{_exportName}'");
                var v = exports.Get(_exportName);
                _promise.ResolveWith(GetWrappedValue(_callerRealm, _callerRealm, v));
            }
            catch (JsThrowException ex) { _promise.RejectWith(ex.Value); }
        }
    }
}

internal sealed partial class Vm
{
    /// <summary>PerformShadowRealmEval: runs <paramref name="source"/> as indirect-eval code of <paramref name="evalRealm"/>.</summary>
    internal JsValue ShadowRealmEval(string source, Realm callerRealm, Realm evalRealm)
    {
        ScriptTemplate t;
        try { t = JsCompiler.CompileEval(source, strictCaller: false, allowNewTarget: false, allowSuper: false, Token); }
        catch (JsCompileException ce) { throw JsErrors.SyntaxError(callerRealm, ce.Description); }
        if (t.EvalPrivateNames != null)
            throw JsErrors.SyntaxError(callerRealm, "Private fields must be declared in an enclosing class");
        JsValue result;
        var saved = Realm;
        try
        {
            // the eval closure belongs to evalRealm, so its frame runs with evalRealm as the current realm
            Realm = evalRealm;
            var c = CreateClosure(t.Body, null, new Feedback(t.Body), null);
            Realm = saved;
            c.BoundThis = JsValue.FromObject(evalRealm.GlobalObject);
            c.BoundNewTarget = JsValue.Undefined;
            EnsureStack();
            var frame = PushFrame(c, default, ReadOnlySpan<JsValue>.Empty, default, FrameFlags.None);
            result = Run(frame);
        }
        catch (JsThrowException ex) { throw ShadowRealms.BoundaryError(callerRealm, ex, "ShadowRealm evaluation threw"); }
        finally { Realm = saved; }
        return ShadowRealms.GetWrappedValue(callerRealm, callerRealm, result);
    }
}
