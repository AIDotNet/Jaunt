using System;
using System.Collections.Generic;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

internal static class ObjectBuiltins
{
    private static JsValue DefineLegacyAccessor(in NativeArgs a, bool getter)
    {
        var realm = a.Realm;
        var o = Operations.ToObject(realm, a.This);
        if (!a[1].IsFunction) throw JsErrors.TypeError(realm, $"Object.prototype.__define{(getter ? "Getter" : "Setter")}__: Expecting function");
        var fn = a[1].AsObjectUnchecked;
        var desc = getter
            ? PropertyDescriptor.AccessorDesc(fn, null, PropFlags.Enumerable | PropFlags.Configurable)
            : PropertyDescriptor.AccessorDesc(null, fn, PropFlags.Enumerable | PropFlags.Configurable);
        if (getter) desc.HasSet = false; else desc.HasGet = false;
        var key = Operations.ToPropertyKey(realm, a[0]);
        if (!o.DefineOwnProperty(key, desc)) throw JsErrors.TypeError(realm, $"Cannot redefine property: {key}");
        return JsValue.Undefined;
    }

    private static JsValue LookupLegacyAccessor(in NativeArgs a, bool getter)
    {
        var realm = a.Realm;
        JsObject? o = Operations.ToObject(realm, a.This);
        var key = Operations.ToPropertyKey(realm, a[0]);
        while (o != null)
        {
            if (o.GetOwnProperty(key, out var d))
            {
                if (!d.IsAccessor) return JsValue.Undefined;
                var f = getter ? d.Getter : d.Setter;
                return f == null ? JsValue.Undefined : JsValue.FromObject(f);
            }
            o = o.GetPrototypeOf();
        }
        return JsValue.Undefined;
    }

    public static void Setup(Realm r)
    {
        var proto = r.ObjectPrototype;
        var ctor = r.Constructor("Object", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.IsConstruct && !ReferenceEquals(a.NewTarget.Ref, a.Callee))
                return JsValue.FromObject(new JsObject(realm, Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.ObjectPrototype)));
            var v = a[0];
            if (v.IsNullOrUndefined) return JsValue.FromObject(realm.NewObject());
            return JsValue.FromObject(Operations.ToObject(realm, v));
        }, proto);
        r.ObjectConstructor = ctor;
        r.DefineGlobal("Object", JsValue.FromObject(ctor));

        r.Method(ctor, "keys", 1, static (in NativeArgs a) => EnumerableOwn(a.Realm, a[0], 0));
        r.Method(ctor, "values", 1, static (in NativeArgs a) => EnumerableOwn(a.Realm, a[0], 1));
        r.Method(ctor, "entries", 1, static (in NativeArgs a) => EnumerableOwn(a.Realm, a[0], 2));
        r.Method(ctor, "assign", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var to = Operations.ToObject(realm, a[0]);
            var keys = new List<PropertyKey>();
            for (int i = 1; i < a.Length; i++)
            {
                var src = a[i];
                if (src.IsNullOrUndefined) continue;
                var from = Operations.ToObject(realm, src);
                keys.Clear();
                from.OwnPropertyKeys(keys);
                foreach (var k in keys)
                {
                    if (from.GetOwnProperty(k, out var d) && d.Enumerable)
                        to.SetOrThrow(k, from.Get(k, JsValue.FromObject(from)));
                }
            }
            return JsValue.FromObject(to);
        });
        r.Method(ctor, "freeze", 1, static (in NativeArgs a) =>
        {
            if (a[0].IsNotObject) return a[0];
            if (!SetIntegrityLevel(a.Realm, a[0].AsObjectUnchecked, true)) throw JsErrors.TypeError(a.Realm, "Cannot freeze");
            return a[0];
        });
        r.Method(ctor, "seal", 1, static (in NativeArgs a) =>
        {
            if (a[0].IsNotObject) return a[0];
            if (!SetIntegrityLevel(a.Realm, a[0].AsObjectUnchecked, false)) throw JsErrors.TypeError(a.Realm, "Cannot seal");
            return a[0];
        });
        r.Method(ctor, "preventExtensions", 1, static (in NativeArgs a) =>
        {
            if (a[0].IsNotObject) return a[0];
            if (!a[0].AsObjectUnchecked.PreventExtensions()) throw JsErrors.TypeError(a.Realm, "Cannot prevent extensions");
            return a[0];
        });
        r.Method(ctor, "isFrozen", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].IsNotObject || TestIntegrityLevel(a[0].AsObjectUnchecked, true)));
        r.Method(ctor, "isSealed", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].IsNotObject || TestIntegrityLevel(a[0].AsObjectUnchecked, false)));
        r.Method(ctor, "isExtensible", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].IsObject && a[0].AsObjectUnchecked.IsExtensibleCheck()));
        r.Method(ctor, "getPrototypeOf", 1, static (in NativeArgs a) =>
        {
            var o = Operations.ToObject(a.Realm, a[0]);
            var p = o.GetPrototypeOf();
            return p == null ? JsValue.Null : JsValue.FromObject(p);
        });
        r.Method(ctor, "setPrototypeOf", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = a[0];
            if (o.IsNullOrUndefined) throw JsErrors.TypeError(realm, "Object.setPrototypeOf called on null or undefined");
            var p = a[1];
            if (p.IsNotObject && !p.IsNull) throw JsErrors.TypeError(realm, "Object prototype may only be an Object or null");
            if (o.IsNotObject) return o;
            if (!o.AsObjectUnchecked.SetPrototypeOf(p.IsNull ? null : p.AsObjectUnchecked))
                throw JsErrors.TypeError(realm, "Cannot set prototype (object is not extensible or a cycle would be created)");
            return o;
        });
        r.Method(ctor, "create", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var p = a[0];
            if (p.IsNotObject && !p.IsNull) throw JsErrors.TypeError(realm, "Object prototype may only be an Object or null");
            var o = new JsObject(realm, p.IsNull ? null : p.AsObjectUnchecked);
            if (!a[1].IsUndefined) DefineProperties(realm, o, a[1]);
            return JsValue.FromObject(o);
        });
        r.Method(ctor, "defineProperty", 3, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a[0].IsNotObject) throw JsErrors.TypeError(realm, "Object.defineProperty called on non-object");
            var key = Operations.ToPropertyKey(realm, a[1]);
            var desc = Operations.ToPropertyDescriptor(realm, a[2]);
            a[0].AsObjectUnchecked.DefinePropertyOrThrow(key, desc);
            return a[0];
        });
        r.Method(ctor, "defineProperties", 2, static (in NativeArgs a) =>
        {
            if (a[0].IsNotObject) throw JsErrors.TypeError(a.Realm, "Object.defineProperties called on non-object");
            DefineProperties(a.Realm, a[0].AsObjectUnchecked, a[1]);
            return a[0];
        });
        r.Method(ctor, "getOwnPropertyDescriptor", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a[0]);
            var key = Operations.ToPropertyKey(realm, a[1]);
            return o.GetOwnProperty(key, out var d) ? Operations.FromPropertyDescriptor(realm, d) : JsValue.Undefined;
        });
        r.Method(ctor, "getOwnPropertyDescriptors", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a[0]);
            var keys = new List<PropertyKey>();
            o.OwnPropertyKeys(keys);
            var result = realm.NewObject();
            foreach (var k in keys)
                if (o.GetOwnProperty(k, out var d)) result.CreateDataProperty(k, Operations.FromPropertyDescriptor(realm, d));
            return JsValue.FromObject(result);
        });
        r.Method(ctor, "getOwnPropertyNames", 1, static (in NativeArgs a) => OwnKeysArray(a.Realm, a[0], strings: true));
        r.Method(ctor, "getOwnPropertySymbols", 1, static (in NativeArgs a) => OwnKeysArray(a.Realm, a[0], strings: false));
        r.Method(ctor, "is", 2, static (in NativeArgs a) => JsValue.FromBool(Operations.SameValue(a[0], a[1])));
        r.Method(ctor, "hasOwn", 2, static (in NativeArgs a) =>
        {
            var o = Operations.ToObject(a.Realm, a[0]);
            return JsValue.FromBool(o.HasOwnProperty(Operations.ToPropertyKey(a.Realm, a[1])));
        });
        r.Method(ctor, "fromEntries", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a[0].IsNullOrUndefined) throw JsErrors.TypeError(realm, "Object.fromEntries requires an iterable");
            var o = realm.NewObject();
            var it = IteratorRecord.Get(realm, a[0]);
            while (it.Step(out var entry))
            {
                try
                {
                    if (entry.IsNotObject) throw JsErrors.TypeError(realm, "Iterator value is not an entry object");
                    var eo = entry.AsObjectUnchecked;
                    var k = eo.GetIndex(0, entry);
                    var v = eo.GetIndex(1, entry);
                    o.CreateDataPropertyOrThrow(Operations.ToPropertyKey(realm, k), v);
                }
                catch (JsThrowException) { it.CloseQuiet(); throw; }
                realm.Vm.Tick();
            }
            return JsValue.FromObject(o);
        });
        r.Method(ctor, "groupBy", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var cb = a[1];
            if (!cb.IsFunction) throw JsErrors.TypeError(realm, "callback is not a function");
            var result = new JsObject(realm, (JsObject?)null);
            var it = IteratorRecord.Get(realm, a[0]);
            double k = 0;
            Span<JsValue> args = new JsValue[2];
            while (it.Step(out var v))
            {
                args[0] = v; args[1] = new JsValue(k++);
                var key = Operations.ToPropertyKey(realm, realm.Vm.Call(cb, JsValue.Undefined, args));
                var existing = result.Get(key);
                JsArray arr;
                if (existing.IsObject) arr = (JsArray)existing.Ref!;
                else { arr = realm.NewArray(); result.CreateDataProperty(key, JsValue.FromObject(arr)); }
                arr.Push(v);
            }
            return JsValue.FromObject(result);
        });

        // prototype
        r.Method(proto, "hasOwnProperty", 1, static (in NativeArgs a) =>
        {
            var key = Operations.ToPropertyKey(a.Realm, a[0]);
            return JsValue.FromBool(Operations.ToObject(a.Realm, a.This).HasOwnProperty(key));
        });
        r.Method(proto, "isPrototypeOf", 1, static (in NativeArgs a) =>
        {
            if (a[0].IsNotObject) return JsValue.False;
            var o = Operations.ToObject(a.Realm, a.This);
            for (var p = a[0].AsObjectUnchecked.GetPrototypeOf(); p != null; p = p.GetPrototypeOf())
                if (ReferenceEquals(p, o)) return JsValue.True;
            return JsValue.False;
        });
        r.Method(proto, "propertyIsEnumerable", 1, static (in NativeArgs a) =>
        {
            var key = Operations.ToPropertyKey(a.Realm, a[0]);
            var o = Operations.ToObject(a.Realm, a.This);
            return JsValue.FromBool(o.GetOwnProperty(key, out var d) && d.Enumerable);
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) => new JsValue(ObjectToString(a.Realm, a.This)));
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) => Operations.Invoke(a.Realm, a.This, "toString", ReadOnlySpan<JsValue>.Empty));
        // Annex B.2.2: legacy accessor helpers
        r.Method(proto, "__defineGetter__", 2, static (in NativeArgs a) => DefineLegacyAccessor(a, getter: true));
        r.Method(proto, "__defineSetter__", 2, static (in NativeArgs a) => DefineLegacyAccessor(a, getter: false));
        r.Method(proto, "__lookupGetter__", 1, static (in NativeArgs a) => LookupLegacyAccessor(a, getter: true));
        r.Method(proto, "__lookupSetter__", 1, static (in NativeArgs a) => LookupLegacyAccessor(a, getter: false));
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => JsValue.FromObject(Operations.ToObject(a.Realm, a.This)));
        r.Getter(proto, "__proto__", "__proto__", static (in NativeArgs a) =>
        {
            var o = Operations.ToObject(a.Realm, a.This);
            var p = o.GetPrototypeOf();
            return p == null ? JsValue.Null : JsValue.FromObject(p);
        }, static (in NativeArgs a) =>
        {
            if (a.This.IsNullOrUndefined) throw JsErrors.TypeError(a.Realm, "Object.prototype.__proto__ called on null or undefined");
            var p = a[0];
            if (p.IsNotObject && !p.IsNull) return JsValue.Undefined;
            if (a.This.IsNotObject) return JsValue.Undefined;
            if (!a.This.AsObjectUnchecked.SetPrototypeOf(p.IsNull ? null : p.AsObjectUnchecked))
                throw JsErrors.TypeError(a.Realm, "Object.prototype.__proto__ setter failed");
            return JsValue.Undefined;
        });
    }

    public static string ObjectToString(Realm realm, JsValue v)
    {
        if (v.IsUndefined) return "[object Undefined]";
        if (v.IsNull) return "[object Null]";
        var o = Operations.ToObject(realm, v);
        string builtinTag = o switch
        {
            JsArray => "Array",
            JsArguments or JsMappedArguments => "Arguments",
            JsFunctionObject => "Function",
            JsError => "Error",
            JsPrimitiveWrapper w => w.Value.IsBoolean ? "Boolean" : w.Value.IsNumber ? "Number" : "Object",
            JsStringObject => "String",
            JsDate => "Date",
            JsRegExp => "RegExp",
            JsProxy p when p.IsArrayTarget() => "Array",
            _ => "Object",
        };
        var tag = o.Get(JsSymbol.ToStringTag, v);
        if (tag.IsString) builtinTag = tag.AsStringUnchecked;
        return "[object " + builtinTag + "]";
    }

    private static JsValue EnumerableOwn(Realm realm, JsValue v, int kind)
    {
        var o = Operations.ToObject(realm, v);
        var result = realm.NewArray();
        if (o is JsArray arr && arr.Dense && kind == 0 && arr.Shape.Count == 0)
        {
            for (uint i = 0; i < arr.Length && i < (uint)arr.Items.Length; i++)
                if (arr.Items[i].IsNotEmpty) result.Push(new JsValue(PropertyKey.IndexToString(i)));
            return JsValue.FromObject(result);
        }
        if (!o.IsExotic && o.Dict == null && !o.Shape.HasIndexKeys && !o.Shape.HasSpecialProps)
        {
            // fast path: ordinary shape-mode object with only plain data properties
            var shape = o.Shape;
            for (int i = 0; i < shape.Count; i++)
            {
                var key = shape.Keys[i];
                if (key.Value is not string s) continue;
                if ((shape.Flags[i] & PropFlags.Enumerable) == 0) continue;
                switch (kind)
                {
                    case 0: result.Push(new JsValue(s)); break;
                    case 1: result.Push(o.Slots[i]); break;
                    default:
                        var pair = realm.NewArray(2);
                        pair.Push(new JsValue(s));
                        pair.Push(o.Slots[i]);
                        result.Push(JsValue.FromObject(pair));
                        break;
                }
            }
            return JsValue.FromObject(result);
        }
        var keys = new List<PropertyKey>();
        o.OwnPropertyKeys(keys);
        var self = JsValue.FromObject(o);
        foreach (var k in keys)
        {
            if (k.Value is not string s) continue;
            if (!o.GetOwnProperty(k, out var d) || !d.Enumerable) continue;
            switch (kind)
            {
                case 0: result.Push(new JsValue(s)); break;
                case 1: result.Push(o.Get(k, self)); break;
                default:
                    var pair = realm.NewArray(2);
                    pair.Push(new JsValue(s));
                    pair.Push(o.Get(k, self));
                    result.Push(JsValue.FromObject(pair));
                    break;
            }
            realm.Vm.Tick();
        }
        return JsValue.FromObject(result);
    }

    private static JsValue OwnKeysArray(Realm realm, JsValue v, bool strings)
    {
        var o = Operations.ToObject(realm, v);
        var keys = new List<PropertyKey>();
        o.OwnPropertyKeys(keys);
        var result = realm.NewArray(keys.Count);
        foreach (var k in keys) if (k.IsString == strings) result.Push(k.ToJsValue());
        return JsValue.FromObject(result);
    }

    private static void DefineProperties(Realm realm, JsObject o, JsValue props)
    {
        var src = Operations.ToObject(realm, props);
        var keys = new List<PropertyKey>();
        src.OwnPropertyKeys(keys);
        var descs = new List<(PropertyKey, PropertyDescriptor)>();
        foreach (var k in keys)
        {
            if (src.GetOwnProperty(k, out var pd) && pd.Enumerable)
                descs.Add((k, Operations.ToPropertyDescriptor(realm, src.Get(k))));
        }
        foreach (var (k, d) in descs) o.DefinePropertyOrThrow(k, d);
    }

    public static bool SetIntegrityLevel(Realm realm, JsObject o, bool frozen)
    {
        if (!o.PreventExtensions()) return false;
        var keys = new List<PropertyKey>();
        o.OwnPropertyKeys(keys);
        if (o is JsArray arr && arr.Dense) arr.ConvertToSparse();
        foreach (var k in keys)
        {
            PropertyDescriptor d;
            if (frozen)
            {
                if (!o.GetOwnProperty(k, out var cur)) continue;
                d = cur.IsAccessor
                    ? new PropertyDescriptor { HasConfigurable = true, Configurable = false }
                    : new PropertyDescriptor { HasConfigurable = true, Configurable = false, HasWritable = true, Writable = false };
            }
            else d = new PropertyDescriptor { HasConfigurable = true, Configurable = false };
            o.DefinePropertyOrThrow(k, d);
        }
        return true;
    }

    public static bool TestIntegrityLevel(JsObject o, bool frozen)
    {
        if (o.IsExtensibleCheck()) return false;
        var keys = new List<PropertyKey>();
        o.OwnPropertyKeys(keys);
        foreach (var k in keys)
        {
            if (!o.GetOwnProperty(k, out var d)) continue;
            if (d.Configurable) return false;
            if (frozen && d.IsData && d.Writable) return false;
        }
        return true;
    }
}

internal static class FunctionBuiltins
{
    public static void Setup(Realm r)
    {
        var proto = r.FunctionPrototype;
        var ctor = r.Constructor("Function", 1, static (in NativeArgs a) => CreateDynamicFunction(a, "function"), proto);
        r.FunctionConstructor = ctor;
        r.DefineGlobal("Function", JsValue.FromObject(ctor));

        r.Method(proto, "call", 1, static (in NativeArgs a) =>
        {
            if (!a.This.IsFunction) throw JsErrors.TypeError(a.Realm, "Function.prototype.call called on non-function");
            var rest = a.Args.Length > 1 ? a.Args.Slice(1) : ReadOnlySpan<JsValue>.Empty;
            return ((JsFunctionObject)a.This.Ref!).Call(a[0], rest);
        });
        r.Method(proto, "apply", 2, static (in NativeArgs a) =>
        {
            if (!a.This.IsFunction) throw JsErrors.TypeError(a.Realm, "Function.prototype.apply was called on non-function");
            var fn = (JsFunctionObject)a.This.Ref!;
            if (a[1].IsNullOrUndefined) return fn.Call(a[0], ReadOnlySpan<JsValue>.Empty);
            if (a[1].Ref is JsArray arr && arr.Dense && arr.Length <= arr.Items.Length && Array.IndexOf(arr.Items, JsValue.Empty, 0, (int)arr.Length) < 0)
                return fn.Call(a[0], new ReadOnlySpan<JsValue>(arr.Items, 0, (int)arr.Length).ToArray());
            var list = Operations.CreateListFromArrayLike(a.Realm, a[1]);
            return fn.Call(a[0], list.ToArray());
        });
        r.Method(proto, "bind", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.This.IsFunction) throw JsErrors.TypeError(realm, "Bind must be called on a function");
            var target = (JsFunctionObject)a.This.Ref!;
            var boundArgs = a.Args.Length > 1 ? a.Args.Slice(1).ToArray() : Array.Empty<JsValue>();
            var bf = new JsBoundFunction(realm, target, a[0], boundArgs);
            double len = 0;
            if (target.HasOwnProperty("length"))
            {
                var tl = target.Get("length");
                if (tl.IsNumber)
                {
                    if (double.IsPositiveInfinity(tl.Num)) len = double.PositiveInfinity;
                    else if (!double.IsNegativeInfinity(tl.Num) && !double.IsNaN(tl.Num)) len = Math.Max(0, Math.Truncate(tl.Num) - boundArgs.Length);
                }
            }
            bf.AddOwnProperty("length", new JsValue(len), PropFlags.Configurable);
            var tn = target.Get("name");
            bf.AddOwnProperty("name", new JsValue("bound " + (tn.IsString ? tn.AsStringUnchecked : "")), PropFlags.Configurable);
            return JsValue.FromObject(bf);
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is JsClosure c)
            {
                var src = c.Template.Script.Source;
                if (src != null && c.Template.SourceEnd <= src.Length && c.Template.SourceEnd > c.Template.SourceStart
                    && (c.Template.Flags & (FunctionFlags.ScriptBody | FunctionFlags.ModuleBody | FunctionFlags.FieldInitializer)) == 0)
                    return new JsValue(src.Substring(c.Template.SourceStart, c.Template.SourceEnd - c.Template.SourceStart));
                return new JsValue("function " + c.Template.Name + "() { [native code] }");
            }
            if (a.This.Ref is JsFunctionObject fo) return new JsValue("function " + (fo is JsBuiltin nf ? NativeFunctionName(nf.Name) : "") + "() { [native code] }");
            throw JsErrors.TypeError(realm, "Function.prototype.toString requires that 'this' be a Function");
        });
        var hasInstance = r.Method(proto, JsSymbol.HasInstance, "[Symbol.hasInstance]", 1,
            static (in NativeArgs a) => JsValue.FromBool(Operations.OrdinaryHasInstance(a.Realm, a.This.IsObject ? a.This.AsObjectUnchecked : null!, a[0])),
            PropFlags.None);
        r.FunctionPrototypeHasInstance = hasInstance;

        r.ThrowTypeErrorFunction = new JsBuiltin(r, "", 0, static (in NativeArgs a) =>
            throw JsErrors.TypeError(a.Realm, "'caller', 'callee', and 'arguments' properties may not be accessed on strict mode functions or the arguments objects for calls to them"));
        // %ThrowTypeError% is frozen: its length and name are non-configurable (10.2.4.1)
        r.ThrowTypeErrorFunction.DefineOwnProperty("length", PropertyDescriptor.Data(new JsValue(0), PropFlags.None));
        r.ThrowTypeErrorFunction.DefineOwnProperty("name", PropertyDescriptor.Data(new JsValue(""), PropFlags.None));
        r.ThrowTypeErrorFunction.PreventExtensions();
        proto.DefineAccessor("caller", r.ThrowTypeErrorFunction, r.ThrowTypeErrorFunction, PropFlags.Configurable);
        proto.DefineAccessor("arguments", r.ThrowTypeErrorFunction, r.ThrowTypeErrorFunction, PropFlags.Configurable);

    }

    /// <summary>%GeneratorFunction%, %AsyncFunction%, %AsyncGeneratorFunction% (lazy group: rarely needed up front).</summary>
    public static void SetupFunctionKinds(Realm r)
    {
        var proto = r.FunctionPrototype;
        var ctor = r.FunctionConstructor;
        var genFnProto = new JsObject(r, proto);
        r.GeneratorFunctionPrototype = genFnProto;
        var genFnCtor = r.Constructor("GeneratorFunction", 1, static (in NativeArgs a) => CreateDynamicFunction(a, "function*"), genFnProto, ctor, readOnlyConstructorLink: true);
        genFnProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("GeneratorFunction"), PropFlags.Configurable);
        _ = genFnCtor;
        var asyncFnProto = new JsObject(r, proto);
        r.AsyncFunctionPrototype = asyncFnProto;
        r.Constructor("AsyncFunction", 1, static (in NativeArgs a) => CreateDynamicFunction(a, "async function"), asyncFnProto, ctor, readOnlyConstructorLink: true);
        asyncFnProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("AsyncFunction"), PropFlags.Configurable);
        var asyncGenFnProto = new JsObject(r, proto);
        r.AsyncGeneratorFunctionPrototype = asyncGenFnProto;
        r.Constructor("AsyncGeneratorFunction", 1, static (in NativeArgs a) => CreateDynamicFunction(a, "async function*"), asyncGenFnProto, ctor, readOnlyConstructorLink: true);
        asyncGenFnProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("AsyncGeneratorFunction"), PropFlags.Configurable);
    }

    /// <summary>CreateDynamicFunction: compiles new Jaunt bytecode at runtime (works under NativeAOT; disabled in Hardened).</summary>
    private static JsValue CreateDynamicFunction(in NativeArgs a, string kind)
    {
        var realm = a.Realm;
        if (realm.Hardened) throw JsErrors.EvalError(realm, "Code generation from strings is disabled (Hardened profile)");
        var sb = new System.Text.StringBuilder();
        sb.Append('(').Append(kind).Append(" anonymous(");
        for (int i = 0; i < a.Length - 1; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Operations.ToString(realm, a[i]));
        }
        sb.Append("\n) {\n");
        if (a.Length > 0) sb.Append(Operations.ToString(realm, a[a.Length - 1]));
        sb.Append("\n})");
        ScriptTemplate t;
        try
        {
            t = JsCompiler.Compile(sb.ToString(), new JsCompileOptions { SourceName = "anonymous" }, isModule: false, realm.Vm.Token);
        }
        catch (JsCompileException ce)
        {
            throw JsErrors.SyntaxError(realm, ce.Description);
        }
        var fn = realm.Engine!.RunScriptTemplate(t, oneShot: true, realm);
        // subclass construction (class X extends Function): proto from new.target, after parsing
        if (a.IsConstruct && a.NewTarget.IsObject && !ReferenceEquals(a.NewTarget.Ref, a.Callee) && fn.IsObject)
        {
            var f = fn.AsObjectUnchecked;
            var proto = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, f.GetPrototypeOf()!);
            f.SetPrototypeOf(proto);
        }
        return fn;
    }

    /// <summary>
    /// NativeFunction syntax for a builtin's name: names that are not an IdentifierName or computed key (e.g. the legacy
    /// accessor <c>get $&amp;</c>) are written as a computed PropertyName (<c>["$&amp;"]</c>) so the result stays parseable.
    /// </summary>
    private static string NativeFunctionName(string name)
    {
        int sp = name.StartsWith("get ", StringComparison.Ordinal) || name.StartsWith("set ", StringComparison.Ordinal) ? 4 : 0;
        var key = name.AsSpan(sp);
        if (key.Length == 0 || key[0] == '[') return name;
        bool ident = !char.IsAsciiDigit(key[0]);
        foreach (char ch in key)
            if (!(char.IsLetterOrDigit(ch) || ch is '$' or '_')) { ident = false; break; }
        if (ident) return name;
        return name.Substring(0, sp) + "[\"" + key.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]";
    }
}
