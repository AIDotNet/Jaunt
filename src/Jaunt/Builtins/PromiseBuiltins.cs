using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal sealed partial class Realm
{
    public JsBuiltin MapSetFunction = null!;
    public JsBuiltin SetAddFunction = null!;
    private HashSet<JsObject>? _joinStack;
    public HashSet<JsObject> JoinStack => _joinStack ??= new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
}

internal static class PromiseBuiltins
{
    public static void Setup(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        r.PromisePrototype = proto;
        var ctor = r.Constructor("Promise", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Promise constructor cannot be invoked without 'new'");
            var executor = a[0];
            if (!executor.IsFunction) throw JsErrors.TypeError(realm, $"Promise resolver {executor} is not a function");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.PromisePrototype);
            var promise = new JsPromise(realm, p);
            var (res, rej) = promise.CreateResolvingFunctions();
            try
            {
                realm.Vm.Call(executor, JsValue.Undefined, new[] { JsValue.FromObject(res), JsValue.FromObject(rej) });
            }
            catch (JsThrowException ex)
            {
                rej.Call(JsValue.Undefined, new ReadOnlySpan<JsValue>(in ex.Value));
            }
            return JsValue.FromObject(promise);
        }, proto);
        r.PromiseConstructor = ctor;
        r.DefineGlobal("Promise", JsValue.FromObject(ctor));
        r.Getter(ctor, JsSymbol.Species, "[Symbol.species]", static (in NativeArgs a) => a.This);
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Promise"), PropFlags.Configurable);

        r.Method(proto, "then", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is not JsPromise p) throw JsErrors.TypeError(realm, "Promise.prototype.then called on incompatible receiver");
            var c = Operations.SpeciesConstructorDefault(realm, p, realm.PromiseConstructor);
            var cap = NewCapability(realm, JsValue.FromObject(c));
            PerformThen(p, a[0], a[1], cap);
            return JsValue.FromObject(cap.Promise);
        });
        r.Method(proto, "catch", 1, static (in NativeArgs a) =>
            Operations.Invoke(a.Realm, a.This, "then", new[] { JsValue.Undefined, a[0] }));
        r.Method(proto, "finally", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "Promise.prototype.finally called on non-object");
            var c = Operations.SpeciesConstructorDefault(realm, a.This.AsObjectUnchecked, realm.PromiseConstructor);
            var onFinally = a[0];
            if (!onFinally.IsFunction) return Operations.Invoke(realm, a.This, "then", new[] { onFinally, onFinally });
            var thenFinally = new JsBuiltin(realm, "", 1, static (in NativeArgs b) =>
            {
                var (cb, ctorObj) = ((JsValue, JsObject))b.Callee.State!;
                var result = b.Realm.Vm.Call(cb, JsValue.Undefined, ReadOnlySpan<JsValue>.Empty);
                var promise = PromiseResolveWith(b.Realm, ctorObj, result);
                var value = b[0];
                var valueThunk = new JsBuiltin(b.Realm, "", 0, static (in NativeArgs d) => (JsValue)d.Callee.State!) { State = value };
                return Operations.Invoke(b.Realm, promise, "then", new[] { JsValue.FromObject(valueThunk) });
            }) { State = (onFinally, c) };
            var catchFinally = new JsBuiltin(realm, "", 1, static (in NativeArgs b) =>
            {
                var (cb, ctorObj) = ((JsValue, JsObject))b.Callee.State!;
                var result = b.Realm.Vm.Call(cb, JsValue.Undefined, ReadOnlySpan<JsValue>.Empty);
                var promise = PromiseResolveWith(b.Realm, ctorObj, result);
                var reason = b[0];
                var thrower = new JsBuiltin(b.Realm, "", 0, static (in NativeArgs d) => throw new JsThrowException((JsValue)d.Callee.State!)) { State = reason };
                return Operations.Invoke(b.Realm, promise, "then", new[] { JsValue.FromObject(thrower) });
            }) { State = (onFinally, c) };
            return Operations.Invoke(realm, a.This, "then", new[] { JsValue.FromObject(thenFinally), JsValue.FromObject(catchFinally) });
        });

        r.Method(ctor, "resolve", 1, static (in NativeArgs a) =>
        {
            if (a.This.IsNotObject) throw JsErrors.TypeError(a.Realm, "Promise.resolve called on non-object");
            return PromiseResolveWith(a.Realm, a.This.AsObjectUnchecked, a[0]);
        });
        r.Method(ctor, "reject", 1, static (in NativeArgs a) =>
        {
            var cap = NewCapability(a.Realm, a.This);
            cap.DoReject(a.Realm, a[0]);
            return JsValue.FromObject(cap.Promise);
        });
        r.Method(ctor, "withResolvers", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var cap = NewCapability(realm, a.This);
            var o = realm.NewObject();
            o.FastAdd("promise", JsValue.FromObject(cap.Promise));
            var (res, rej) = Materialize(realm, cap);
            o.FastAdd("resolve", res);
            o.FastAdd("reject", rej);
            return JsValue.FromObject(o);
        });
        r.Method(ctor, "all", 1, static (in NativeArgs a) => Combinator(a, 0));
        r.Method(ctor, "allSettled", 1, static (in NativeArgs a) => Combinator(a, 1));
        r.Method(ctor, "any", 1, static (in NativeArgs a) => Combinator(a, 2));
        r.Method(ctor, "race", 1, static (in NativeArgs a) => Combinator(a, 3));
        r.Method(ctor, "allKeyed", 1, static (in NativeArgs a) => KeyedCombinator(a, settled: false));
        r.Method(ctor, "allSettledKeyed", 1, static (in NativeArgs a) => KeyedCombinator(a, settled: true));
        Es2025Builtins.InstallPromise(r, ctor);
    }

    private static (JsValue resolve, JsValue reject) Materialize(Realm realm, PromiseCapability cap)
    {
        if (!cap.Resolve.IsUndefined) return (cap.Resolve, cap.Reject);
        var (res, rej) = ((JsPromise)cap.Promise).CreateResolvingFunctions();
        return (JsValue.FromObject(res), JsValue.FromObject(rej));
    }

    public static JsValue PromiseResolveWith(Realm realm, JsObject c, JsValue x)
    {
        if (x.Ref is JsPromise xp)
        {
            var xc = xp.Get("constructor");
            if (xc.IsObject && ReferenceEquals(xc.Ref, c)) return x;
        }
        var cap = NewCapability(realm, JsValue.FromObject(c));
        cap.DoResolve(realm, x);
        return JsValue.FromObject(cap.Promise);
    }

    /// <summary>NewPromiseCapability(C).</summary>
    public static PromiseCapability NewCapability(Realm realm, JsValue c)
    {
        if (c.IsObject && ReferenceEquals(c.Ref, realm.PromiseConstructor))
            return new PromiseCapability(new JsPromise(realm));
        if (!Operations.IsConstructor(c)) throw JsErrors.TypeError(realm, "Promise capability constructor is not a constructor");
        var box = new JsValue[2];
        var executor = new JsBuiltin(realm, "", 2, static (in NativeArgs a) =>
        {
            var b = (JsValue[])a.Callee.State!;
            if (!b[0].IsUndefined || !b[1].IsUndefined) throw JsErrors.TypeError(a.Realm, "Promise executor has already been invoked");
            b[0] = a[0];
            b[1] = a[1];
            return JsValue.Undefined;
        }) { State = box };
        var promise = realm.Vm.Construct(c, new[] { JsValue.FromObject(executor) });
        if (!box[0].IsFunction || !box[1].IsFunction) throw JsErrors.TypeError(realm, "Promise resolve or reject function is not callable");
        return new PromiseCapability(promise.AsObjectUnchecked, box[0], box[1]);
    }

    public static void PerformThen(JsPromise p, JsValue onFulfilled, JsValue onRejected, PromiseCapability? cap)
    {
        p.Then(
            new PromiseReaction { Capability = cap, IsFulfillReaction = true, Handler = onFulfilled.IsFunction ? onFulfilled : JsValue.Undefined },
            new PromiseReaction { Capability = cap, IsFulfillReaction = false, Handler = onRejected.IsFunction ? onRejected : JsValue.Undefined });
    }

    /// <summary>0 all, 1 allSettled, 2 any, 3 race.</summary>
    private static JsValue Combinator(in NativeArgs a, int kind)
    {
        var realm = a.Realm;
        var c = a.This;
        var cap = NewCapability(realm, c);
        try
        {
            var resolveFn = c.AsObjectUnchecked.Get("resolve");
            if (!resolveFn.IsFunction) throw JsErrors.TypeError(realm, "Promise resolve is not a function");
            var it = IteratorRecord.Get(realm, a[0]);
            var values = new List<JsValue>();
            var state = new CombinatorState { Capability = cap, Values = values, Remaining = 1, Kind = kind };
            int index = 0;
            while (true)
            {
                JsValue next;
                try
                {
                    if (!it.Step(out next)) break;
                }
                catch (JsThrowException) { it.Done = true; throw; }
                try
                {
                    var nextPromise = realm.Vm.Call(resolveFn, c, new ReadOnlySpan<JsValue>(in next));
                    if (kind == 3)
                    {
                        var (res, rej) = Materialize(realm, cap);
                        Operations.Invoke(realm, nextPromise, "then", new[] { res, rej });
                        continue;
                    }
                    values.Add(JsValue.Undefined);
                    state.Remaining++;
                    int myIndex = index++;
                    JsValue onFul, onRej;
                    var ctx = new ElementContext { State = state, Index = myIndex };
                    switch (kind)
                    {
                        case 0:
                            onFul = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs b) => ((ElementContext)b.Callee.State!).Resolve(b.Realm, b[0], 0)) { State = ctx });
                            var (_, rej0) = Materialize(realm, cap);
                            onRej = rej0;
                            break;
                        case 1:
                            onFul = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs b) => ((ElementContext)b.Callee.State!).Resolve(b.Realm, b[0], 1)) { State = ctx });
                            onRej = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs b) => ((ElementContext)b.Callee.State!).Resolve(b.Realm, b[0], 2)) { State = ctx });
                            break;
                        default:
                            var (res2, _) = Materialize(realm, cap);
                            onFul = res2;
                            onRej = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs b) => ((ElementContext)b.Callee.State!).Resolve(b.Realm, b[0], 3)) { State = ctx });
                            break;
                    }
                    Operations.Invoke(realm, nextPromise, "then", new[] { onFul, onRej });
                }
                catch (JsThrowException) { it.CloseQuiet(); throw; }
                realm.Vm.Tick();
            }
            if (kind != 3 && --state.Remaining == 0) state.Finish(realm);
        }
        catch (JsThrowException ex)
        {
            cap.DoReject(realm, ex.Value);
        }
        return JsValue.FromObject(cap.Promise);
    }

    /// <summary>Promise.allKeyed / Promise.allSettledKeyed (proposal-await-dictionary).</summary>
    private static JsValue KeyedCombinator(in NativeArgs a, bool settled)
    {
        var realm = a.Realm;
        var c = a.This;
        var cap = NewCapability(realm, c);
        JsValue resolveFn;
        try
        {
            resolveFn = c.AsObjectUnchecked.Get("resolve");
            if (!resolveFn.IsFunction) throw JsErrors.TypeError(realm, "Promise resolve is not a function");
        }
        catch (JsThrowException ex)
        {
            cap.DoReject(realm, ex.Value);
            return JsValue.FromObject(cap.Promise);
        }
        if (a[0].IsNotObject)
        {
            cap.DoReject(realm, JsValue.FromObject(JsError.Create(realm, ErrorKind.TypeError, "Promise keyed combinator argument is not an object")));
            return JsValue.FromObject(cap.Promise);
        }
        try
        {
            var promises = a[0].AsObjectUnchecked;
            var allKeys = new List<PropertyKey>();
            promises.OwnPropertyKeys(allKeys);
            var values = new List<JsValue>();
            var keys = new List<PropertyKey>();
            var state = new CombinatorState { Capability = cap, Values = values, Keys = keys, Remaining = 1, Kind = settled ? 1 : 0 };
            var argsBuf = new JsValue[1];
            foreach (var key in allKeys)
            {
                if (!promises.GetOwnProperty(key, out var desc) || !desc.Enumerable) continue;
                var propertyValue = promises.Get(key, a[0]);
                keys.Add(key);
                values.Add(JsValue.Undefined);
                argsBuf[0] = propertyValue;
                var nextPromise = realm.Vm.Call(resolveFn, c, argsBuf);
                var ctx = new ElementContext { State = state, Index = keys.Count - 1 };
                JsValue onFul, onRej;
                if (!settled)
                {
                    onFul = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs b) => ((ElementContext)b.Callee.State!).Resolve(b.Realm, b[0], 0)) { State = ctx });
                    onRej = Materialize(realm, cap).reject;
                }
                else
                {
                    onFul = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs b) => ((ElementContext)b.Callee.State!).Resolve(b.Realm, b[0], 1)) { State = ctx });
                    onRej = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs b) => ((ElementContext)b.Callee.State!).Resolve(b.Realm, b[0], 2)) { State = ctx });
                }
                state.Remaining++;
                Operations.Invoke(realm, nextPromise, "then", new[] { onFul, onRej });
                realm.Vm.Tick();
            }
            if (--state.Remaining == 0) state.Finish(realm);
        }
        catch (JsThrowException ex)
        {
            cap.DoReject(realm, ex.Value);
        }
        return JsValue.FromObject(cap.Promise);
    }

    private sealed class CombinatorState
    {
        public required PromiseCapability Capability;
        public required List<JsValue> Values;
        /// <summary>Keyed combinators: property keys parallel to <see cref="Values"/> (result is a null-prototype object).</summary>
        public List<PropertyKey>? Keys;
        public int Remaining;
        public int Kind;

        public void Finish(Realm realm)
        {
            if (Keys != null)
            {
                var obj = new JsObject(realm, (JsObject?)null);
                for (int i = 0; i < Keys.Count; i++) obj.CreateDataProperty(Keys[i], Values[i]);
                Capability.DoResolve(realm, JsValue.FromObject(obj));
                return;
            }
            var arr = Operations.CreateArrayFromList(realm, Values.ToArray());
            if (Kind == 2)
            {
                var err = JsError.Create(realm, ErrorKind.AggregateError, "All promises were rejected");
                err.DefineHidden("errors", JsValue.FromObject(arr));
                Capability.DoReject(realm, JsValue.FromObject(err));
            }
            else Capability.DoResolve(realm, JsValue.FromObject(arr));
        }
    }

    private sealed class ElementContext
    {
        public required CombinatorState State;
        public int Index;
        private bool _called;

        public JsValue Resolve(Realm realm, JsValue v, int mode)
        {
            if (_called) return JsValue.Undefined;
            _called = true;
            JsValue stored = v;
            if (mode == 1 || mode == 2)
            {
                var o = realm.NewObject();
                o.FastAdd("status", new JsValue(mode == 1 ? "fulfilled" : "rejected"));
                o.FastAdd(mode == 1 ? "value" : "reason", v);
                stored = JsValue.FromObject(o);
            }
            State.Values[Index] = stored;
            if (--State.Remaining == 0) State.Finish(realm);
            return JsValue.Undefined;
        }
    }
}
