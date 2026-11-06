using System;
using System.Collections.Generic;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

/// <summary>Name-based environment access for direct eval, eval code and <c>with</c> (conservative path).</summary>
internal sealed partial class Vm
{
    private enum NameKind { Slot, EvalVar, With, Global }

    /// <summary>ResolveBinding over the environment chain (9.1.2.1 GetIdentifierReference).</summary>
    private NameKind ResolveName(Env? e, string name, out Env? holder, out int slot)
    {
        for (; e != null; e = e.Parent)
        {
            if (e.WithObject is { } wo)
            {
                var key = new PropertyKey(name);
                if (wo.HasProperty(key))
                {
                    var uns = wo.Get(JsSymbol.Unscopables);
                    if (!(uns.IsObject && Operations.ToBoolean(uns.AsObjectUnchecked.Get(key, uns))))
                    {
                        holder = e;
                        slot = -1;
                        return NameKind.With;
                    }
                }
                continue;
            }
            var l = e.Layout;
            if (l != null)
            {
                int i = l.IndexOf(name);
                if (i >= 0)
                {
                    holder = e;
                    slot = i;
                    return NameKind.Slot;
                }
            }
            if (e.EvalVars != null && e.EvalVars.ContainsKey(name))
            {
                holder = e;
                slot = -1;
                return NameKind.EvalVar;
            }
        }
        holder = null;
        slot = -1;
        return NameKind.Global;
    }

    private JsValue LoadName(Env? env, string name, bool typeofMode, out JsValue thisValue, bool strict = false)
    {
        thisValue = JsValue.Undefined;
        switch (ResolveName(env, name, out var holder, out int slot))
        {
            case NameKind.Slot:
            {
                var v = holder!.Slots[slot];
                if (v.IsEmpty && v.Ref == null) throw JsErrors.ReferenceError(Realm, $"Cannot access '{name}' before initialization");
                return v;
            }
            case NameKind.EvalVar:
                return holder!.EvalVars![name];
            case NameKind.With:
            {
                var wo = holder!.WithObject!;
                thisValue = JsValue.FromObject(wo);
                // Object Environment GetBindingValue: the binding may have been removed since it was resolved
                var key = new PropertyKey(name);
                if (!wo.HasProperty(key))
                {
                    if (strict) throw JsErrors.ReferenceError(Realm, $"{name} is not defined");
                    return JsValue.Undefined;
                }
                return wo.Get(key, thisValue);
            }
            default:
            {
                IcEntry ic = default;
                return GetGlobalSlow(name, ref ic, typeofMode);
            }
        }
    }

    private void StoreName(Env? env, string name, JsValue value, bool strict)
    {
        switch (ResolveName(env, name, out var holder, out int slot))
        {
            case NameKind.Slot:
            {
                var flags = holder!.Layout!.Flags[slot];
                if ((flags & SlotFlags.Lexical) != 0 && holder.Slots[slot].IsEmpty)
                    throw JsErrors.ReferenceError(Realm, $"Cannot access '{name}' before initialization");
                if ((flags & SlotFlags.FuncName) != 0)
                {
                    if (strict) throw JsErrors.TypeError(Realm, "Assignment to constant variable.");
                    return;
                }
                if ((flags & SlotFlags.Const) != 0) throw JsErrors.TypeError(Realm, "Assignment to constant variable.");
                holder.Slots[slot] = value;
                return;
            }
            case NameKind.EvalVar:
                holder!.EvalVars![name] = value;
                return;
            case NameKind.With:
            {
                var wo = holder!.WithObject!;
                var key = new PropertyKey(name);
                if (!wo.HasProperty(key) && strict) throw JsErrors.ReferenceError(Realm, $"{name} is not defined");
                if (!wo.Set(key, value, JsValue.FromObject(wo)) && strict)
                    throw JsErrors.TypeError(Realm, $"Cannot assign to read only property '{name}' of object");
                return;
            }
            default:
            {
                IcEntry ic = default;
                SetGlobalSlow(name, value, ref ic, strict);
                return;
            }
        }
    }

    private bool DeleteName(Env? env, string name)
    {
        switch (ResolveName(env, name, out var holder, out _))
        {
            case NameKind.Slot: return false;
            case NameKind.EvalVar: holder!.EvalVars!.Remove(name); return true;
            case NameKind.With: return holder!.WithObject!.Delete(new PropertyKey(name));
            default:
            {
                var realm = Realm;
                realm.EnsureGlobal(name);
                if (realm.GlobalLex.ContainsKey(name)) return false;
                bool ok = realm.GlobalObject.Delete(new PropertyKey(name));
                if (ok) realm.GlobalVersion++;
                return ok;
            }
        }
    }

    /// <summary>Finds the variable environment for sloppy eval var declarations (null = global).</summary>
    private Env? EvalVarEnv(Env? e, EvalDeclInfo? info)
    {
        for (; e != null; e = e.Parent)
        {
            if (e.WithObject != null) continue;
            var l = e.Layout;
            if (l == null) continue;
            if (l.IsVarScope) return e;
            if (info != null)
            {
                // lexical declarations between the eval and its variable environment conflict with its vars
                foreach (var n in info.VarNames) CheckEvalConflict(l, n);
                foreach (var n in info.FunctionNames) CheckEvalConflict(l, n);
            }
        }
        return null;
    }

    private void CheckEvalConflict(ScopeLayout l, string name)
    {
        int i = l.IndexOf(name);
        if (i >= 0 && (l.Flags[i] & SlotFlags.CatchParam) == 0)
            throw JsErrors.SyntaxError(Realm, $"Identifier '{name}' has already been declared");
    }

    /// <summary>EvalDeclarationInstantiation for sloppy eval code (19.2.1.3), var / function names only.</summary>
    private void EvalDeclare(Env? env, EvalDeclInfo info)
    {
        var realm = Realm;
        var varEnv = EvalVarEnv(env, info);
        if (varEnv == null)
        {
            var g = realm.GlobalObject;
            foreach (var n in info.FunctionNames) realm.EnsureGlobal(n);
            foreach (var n in info.VarNames) realm.EnsureGlobal(n);
            foreach (var n in info.VarNames)
                if (realm.GlobalLex.ContainsKey(n)) throw JsErrors.SyntaxError(realm, $"Identifier '{n}' has already been declared");
            foreach (var n in info.FunctionNames)
            {
                if (realm.GlobalLex.ContainsKey(n)) throw JsErrors.SyntaxError(realm, $"Identifier '{n}' has already been declared");
                // CanDeclareGlobalFunction
                if (g.GetOwnProperty(n, out var d))
                {
                    if (!d.Configurable && (d.IsAccessor || !d.Writable || !d.Enumerable))
                        throw JsErrors.TypeError(realm, $"Cannot redefine global function '{n}'");
                }
                else if (!g.IsExtensibleCheck()) throw JsErrors.TypeError(realm, $"Cannot define global function '{n}'");
            }
            foreach (var n in info.VarNames)
                if (!g.HasOwnProperty(n) && !g.IsExtensibleCheck()) throw JsErrors.TypeError(realm, $"Cannot define global variable '{n}'");
            foreach (var n in info.FunctionNames)
            {
                if (!g.GetOwnProperty(n, out var d) || d.Configurable)
                    g.DefinePropertyOrThrow(n, PropertyDescriptor.Data(JsValue.Undefined, PropFlags.Writable | PropFlags.Enumerable | PropFlags.Configurable));
            }
            foreach (var n in info.VarNames)
            {
                if (!g.HasOwnProperty(n))
                    g.DefinePropertyOrThrow(n, PropertyDescriptor.Data(JsValue.Undefined, PropFlags.Writable | PropFlags.Enumerable | PropFlags.Configurable));
            }
            realm.GlobalVersion++;
            return;
        }
        var l = varEnv.Layout!;
        foreach (var n in info.VarNames) CheckEvalLexical(l, n);
        foreach (var n in info.FunctionNames) CheckEvalLexical(l, n);
        foreach (var n in info.FunctionNames) DeclareEvalVar(varEnv, n);
        foreach (var n in info.VarNames) DeclareEvalVar(varEnv, n);
    }

    private void CheckEvalLexical(ScopeLayout l, string name)
    {
        int i = l.IndexOf(name);
        if (i >= 0 && (l.Flags[i] & (SlotFlags.Lexical | SlotFlags.FuncName)) == SlotFlags.Lexical)
            throw JsErrors.SyntaxError(Realm, $"Identifier '{name}' has already been declared");
    }

    private static void DeclareEvalVar(Env varEnv, string name)
    {
        int i = varEnv.Layout!.IndexOf(name);
        if (i >= 0 && (varEnv.Layout.Flags[i] & SlotFlags.FuncName) == 0) return; // existing var / parameter
        var bag = varEnv.EvalVars ??= new Dictionary<string, JsValue>(StringComparer.Ordinal);
        if (!bag.ContainsKey(name)) bag[name] = JsValue.Undefined;
    }

    /// <summary>Initializes a function declared by sloppy eval code in the variable environment.</summary>
    private void EvalInitVar(Env? env, string name, JsValue value, bool setOnly)
    {
        var varEnv = EvalVarEnv(env, null);
        if (varEnv == null)
        {
            var g = Realm.GlobalObject;
            if (setOnly)
            {
                // Annex B block function evaluated: SetMutableBinding on the global var binding
                g.Set(new PropertyKey(name), value, JsValue.FromObject(g));
                Realm.GlobalVersion++;
                return;
            }
            if (!g.GetOwnProperty(name, out var d) || d.Configurable)
                g.DefinePropertyOrThrow(name, PropertyDescriptor.Data(value, PropFlags.Writable | PropFlags.Enumerable | PropFlags.Configurable));
            else g.Set(new PropertyKey(name), value, JsValue.FromObject(g));
            Realm.GlobalVersion++;
            return;
        }
        int i = varEnv.Layout!.IndexOf(name);
        if (i >= 0 && (varEnv.Layout.Flags[i] & SlotFlags.FuncName) == 0) varEnv.Slots[i] = value;
        else (varEnv.EvalVars ??= new Dictionary<string, JsValue>(StringComparer.Ordinal))[name] = value;
    }

    // ------------------------------------------------------------------ references (resolved once, used twice)

    internal sealed class NameReference
    {
        public readonly string Name;
        public readonly int Kind; // 0 slot, 1 eval var, 2 with, 3 global
        public readonly Env? Holder;
        public readonly int Slot;
        public readonly bool Unresolvable;
        public NameReference(string name, int kind, Env? holder, int slot, bool unresolvable)
        {
            Name = name; Kind = kind; Holder = holder; Slot = slot; Unresolvable = unresolvable;
        }
    }

    private JsValue GetReference(NameReference r, bool strict)
    {
        switch (r.Kind)
        {
            case 0:
            {
                var v = r.Holder!.Slots[r.Slot];
                if (v.IsEmpty && v.Ref == null) throw JsErrors.ReferenceError(Realm, $"Cannot access '{r.Name}' before initialization");
                return v;
            }
            case 1:
                return r.Holder!.EvalVars!.TryGetValue(r.Name, out var ev) ? ev : throw JsErrors.ReferenceError(Realm, $"{r.Name} is not defined");
            case 2:
            {
                var wo = r.Holder!.WithObject!;
                var key = new PropertyKey(r.Name);
                if (wo.HasProperty(key)) return wo.Get(key, JsValue.FromObject(wo));
                if (strict) throw JsErrors.ReferenceError(Realm, $"{r.Name} is not defined");
                return JsValue.Undefined;
            }
            default:
            {
                if (r.Unresolvable) throw JsErrors.ReferenceError(Realm, $"{r.Name} is not defined");
                IcEntry ic = default;
                return GetGlobalSlow(r.Name, ref ic, false);
            }
        }
    }

    private void PutReference(NameReference r, JsValue value, bool strict)
    {
        switch (r.Kind)
        {
            case 0:
            {
                var holder = r.Holder!;
                var flags = holder.Layout!.Flags[r.Slot];
                if ((flags & SlotFlags.Lexical) != 0 && holder.Slots[r.Slot].IsEmpty)
                    throw JsErrors.ReferenceError(Realm, $"Cannot access '{r.Name}' before initialization");
                if ((flags & SlotFlags.FuncName) != 0)
                {
                    if (strict) throw JsErrors.TypeError(Realm, "Assignment to constant variable.");
                    return;
                }
                if ((flags & SlotFlags.Const) != 0) throw JsErrors.TypeError(Realm, "Assignment to constant variable.");
                holder.Slots[r.Slot] = value;
                return;
            }
            case 1:
                r.Holder!.EvalVars![r.Name] = value;
                return;
            case 2:
            {
                var wo = r.Holder!.WithObject!;
                var key = new PropertyKey(r.Name);
                if (!wo.HasProperty(key) && strict) throw JsErrors.ReferenceError(Realm, $"{r.Name} is not defined");
                if (!wo.Set(key, value, JsValue.FromObject(wo)) && strict)
                    throw JsErrors.TypeError(Realm, $"Cannot assign to read only property '{r.Name}' of object");
                return;
            }
            default:
            {
                if (r.Unresolvable)
                {
                    if (strict) throw JsErrors.ReferenceError(Realm, $"{r.Name} is not defined");
                    var g = Realm.GlobalObject;
                    g.Set(new PropertyKey(r.Name), value, JsValue.FromObject(g));
                    return;
                }
                IcEntry ic = default;
                SetGlobalSlow(r.Name, value, ref ic, strict);
                return;
            }
        }
    }

    // ------------------------------------------------------------------ eval

    /// <summary>PerformEval (19.2.1.1). <paramref name="caller"/> is null for indirect eval.</summary>
    internal JsValue PerformEval(JsValue x, Frame? caller, bool strictCaller, bool allowNewTarget, int flags = 0)
    {
        var realm = Realm;
        if (x.IsNotString) return x;
        if (realm.Hardened) throw JsErrors.EvalError(realm, "Code generation from strings is disabled (Hardened profile)");
        var home = caller?.Callee.HomeObject;
        ScriptTemplate t;
        try
        {
            bool inDerivedCtor = caller != null && (caller.ThisBox != null || (caller.Callee.IsDerived && (caller.Flags & FrameFlags.Construct) != 0));
            t = JsCompiler.CompileEval(x.AsStringUnchecked, strictCaller, allowNewTarget, allowSuper: home != null, Token, inFieldInit: (flags & 8) != 0, allowSuperCall: inDerivedCtor);
        }
        catch (JsCompileException ce)
        {
            throw JsErrors.SyntaxError(realm, ce.Description);
        }
        if (t.EvalPrivateNames != null)
        {
            foreach (var pn in t.EvalPrivateNames)
                if (ResolveName(caller?.Env, pn, out _, out _) != NameKind.Slot)
                    throw JsErrors.SyntaxError(realm, $"Private field '{pn}' must be declared in an enclosing class");
        }
        if ((flags & 4) != 0 && t.EvalDeclaresArguments)
            throw JsErrors.SyntaxError(realm, "Eval code in parameter expressions cannot declare 'arguments'");
        if ((flags & 8) != 0 && t.EvalContainsArguments)
            throw JsErrors.SyntaxError(realm, "'arguments' is not allowed in class field initializer or static initialization block");
        var fb = new Feedback(t.Body);
        var c = CreateClosure(t.Body, caller?.Env, fb, null);
        if (caller != null)
        {
            if (caller.ThisBox != null || (caller.Callee.IsDerived && (caller.Flags & FrameFlags.Construct) != 0))
                c.ThisBox = caller.ThisBox ??= new ThisBox { Value = caller.This, Ctor = caller.Callee, NewTarget = caller.NewTarget };
            c.BoundThis = caller.This;
            c.BoundNewTarget = caller.NewTarget;
            c.HomeObject = home;
            c.Module = caller.Callee.Module;
        }
        else
        {
            c.BoundThis = JsValue.FromObject(realm.GlobalObject);
            c.BoundNewTarget = JsValue.Undefined;
        }
        EnsureStack();
        var frame = PushFrame(c, default, ReadOnlySpan<JsValue>.Empty, default, FrameFlags.None);
        return Run(frame);
    }

    /// <summary>Executes the name-based opcodes. Returns the next pc.</summary>
    private int DynamicOp(Frame f, int pc)
    {
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        JsValue[] k = f.T.Constants;
        switch ((Op)code[pc])
        {
            case Op.PushEnvNamed:
            {
                int imm = code[pc + 1];
                f.Env = new Env(f.Env, imm & 0xFFFF, imm >> 16) { Layout = (ScopeLayout)f.T.Objects[code[pc + 2]] };
                f.EnvDepth++;
                return pc + 3;
            }
            case Op.PushWith:
            {
                var v = regs[bp + code[pc + 1]];
                if (v.IsNullOrUndefined) throw JsErrors.TypeError(Realm, $"Cannot convert {v} to object");
                f.Env = new Env(f.Env, 0, 0) { WithObject = Operations.ToObject(Realm, v) };
                f.EnvDepth++;
                return pc + 2;
            }
            case Op.LdName:
                regs[bp + code[pc + 1]] = LoadName(f.Env, k[code[pc + 2]].AsStringUnchecked, code[pc + 3] != 0, out _, f.T.IsStrict);
                return pc + 4;
            case Op.LdNameCall:
            {
                var v = LoadName(f.Env, k[code[pc + 3]].AsStringUnchecked, false, out var thisValue, f.T.IsStrict);
                regs[bp + code[pc + 1]] = v;
                regs[bp + code[pc + 2]] = thisValue;
                return pc + 4;
            }
            case Op.StName:
                StoreName(f.Env, k[code[pc + 1]].AsStringUnchecked, regs[bp + code[pc + 2]], (code[pc + 3] & 1) != 0);
                return pc + 4;
            case Op.DelName:
                regs[bp + code[pc + 1]] = JsValue.FromBool(DeleteName(f.Env, k[code[pc + 2]].AsStringUnchecked));
                return pc + 3;
            case Op.EvalDeclare:
                EvalDeclare(f.Env, (EvalDeclInfo)f.T.Objects[code[pc + 1]]);
                return pc + 2;
            case Op.EvalInitVar:
                EvalInitVar(f.Env, k[code[pc + 1]].AsStringUnchecked, regs[bp + code[pc + 2]], code[pc + 3] != 0);
                return pc + 4;
            case Op.CallEval:
            {
                var fv = regs[bp + code[pc + 2]];
                int tr = code[pc + 3];
                int argStart = bp + code[pc + 4];
                int argc = code[pc + 5];
                int flags = code[pc + 6];
                JsValue result;
                if (fv.IsObject && ReferenceEquals(fv.Ref, Realm.EvalFunction))
                    result = argc == 0 ? JsValue.Undefined : PerformEval(regs[argStart], f, (flags & 1) != 0, (flags & 2) != 0, flags);
                else
                    result = CallValue(fv, tr < 0 ? default : regs[bp + tr], new ReadOnlySpan<JsValue>(regs, argStart, argc));
                regs = f.Regs; // the register file may have been reallocated
                regs[f.Base + code[pc + 1]] = result;
                return pc + 7;
            }
            case Op.CallEvalSpread:
            {
                // eval(...args): still a direct eval when the callee is %eval%
                var fv = regs[bp + code[pc + 2]];
                int tr = code[pc + 3];
                var arr = (JsArray)regs[bp + code[pc + 4]].Ref!;
                int flags = code[pc + 5];
                var args = new ReadOnlySpan<JsValue>(arr.Items, 0, (int)arr.Length);
                JsValue result;
                if (fv.IsObject && ReferenceEquals(fv.Ref, Realm.EvalFunction))
                    result = args.Length == 0 ? JsValue.Undefined : PerformEval(args[0], f, (flags & 1) != 0, (flags & 2) != 0, flags);
                else
                    result = CallValue(fv, tr < 0 ? default : regs[bp + tr], args);
                regs = f.Regs;
                regs[f.Base + code[pc + 1]] = result;
                return pc + 6;
            }
            case Op.ResolveRef:
            {
                string name = k[code[pc + 2]].AsStringUnchecked;
                var kind = ResolveName(f.Env, name, out var holder, out int slot);
                bool unresolvable = false;
                if (kind == NameKind.Global)
                {
                    var realm = Realm;
                    realm.EnsureGlobal(name);
                    unresolvable = !realm.GlobalLex.ContainsKey(name) && !realm.GlobalObject.HasProperty(new PropertyKey(name));
                }
                regs[bp + code[pc + 1]] = new JsValue(JsTag.Empty, 0, new NameReference(name, kind == NameKind.Slot ? 0 : kind == NameKind.EvalVar ? 1 : kind == NameKind.With ? 2 : 3, holder, slot, unresolvable));
                return pc + 3;
            }
            case Op.LdRef:
            {
                var r = (NameReference)regs[bp + code[pc + 2]].Ref!;
                regs[bp + code[pc + 1]] = GetReference(r, f.T.IsStrict);
                return pc + 3;
            }
            case Op.StRef:
                PutReference((NameReference)regs[bp + code[pc + 1]].Ref!, regs[bp + code[pc + 2]], (code[pc + 3] & 1) != 0);
                return pc + 4;
            default:
                throw new InvalidOperationException("Jaunt internal error: not a dynamic-scope opcode.");
        }
    }
}
