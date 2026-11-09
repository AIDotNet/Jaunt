using System.Collections.Generic;
using Acornima.Ast;
using Jaunt.Runtime;

namespace Jaunt.Compiler;

/// <summary>Sloppy-eval var declarations of one eval code (run-time EvalDeclarationInstantiation input).</summary>
internal sealed class EvalDeclInfo
{
    public required string[] VarNames;
    public required string[] FunctionNames;
}

/// <summary>
/// Direct eval and <c>with</c>: scopes that eval / with code may inspect carry slot names; references that cannot
/// be resolved statically are compiled to name lookups (LdName / StName / ...).
/// </summary>
internal sealed partial class CodeGen
{
    private bool _evalAllowNewTarget;
    private bool _evalInFieldInit;
    /// <summary>True while the formal parameters of the current function are being bound.</summary>
    private bool _inParams;

    private void EmitPushEnv(Scope s)
    {
        int imm = s.EnvSize | (s.LexStart << 16);
        if (s.Named) _fb.Emit(Op.PushEnvNamed, imm, _fb.AddObject(BuildLayout(s)));
        else _fb.Emit(Op.PushEnv, imm);
    }

    private static ScopeLayout BuildLayout(Scope s)
    {
        var names = new string?[s.EnvSize];
        var flags = new SlotFlags[s.EnvSize];
        foreach (var b in s.Ordered)
        {
            if (!b.Captured || b.EnvSlot < 0 || b.EnvSlot >= names.Length || b.Kind == BindingKind.Import) continue;
            names[b.EnvSlot] = b.Name;
            var f = SlotFlags.None;
            if (b.HasTdz) f |= SlotFlags.Lexical;
            if (b.IsConst) f |= SlotFlags.Const;
            if (b.Kind == BindingKind.FuncName) f |= SlotFlags.FuncName;
            if (b.Kind == BindingKind.CatchParam) f |= SlotFlags.CatchParam;
            flags[b.EnvSlot] = f;
        }
        bool varScope = (s.Kind is ScopeKind.Function or ScopeKind.Module || s == s.Function.BodyScope) && s.Function.EvalVarNames == null;
        return new ScopeLayout { Names = names, Flags = flags, IsVarScope = varScope };
    }

    private bool IsDynamicRef(Identifier id) => ResolvedRef.Get(id) is { Binding: null, Dynamic: true } || ResolvedRef.Get(id).Binding?.IsEvalDynamic == true;

    /// <summary>Resolves a dynamic identifier reference once (before the right-hand side is evaluated).</summary>
    private int EmitResolveRef(Identifier id)
    {
        int r = Temp();
        _fb.Emit(Op.ResolveRef, r, _fb.Const(id.Name));
        return r;
    }

    private void EmitStoreRef(int refReg, int src) => _fb.Emit(Op.StRef, refReg, src, _func.Strict ? 1 : 0);

    /// <summary>Source text of a class method excludes a leading <c>static</c> keyword (15.7 ClassElement).</summary>
    private int MethodSourceStart(MethodDefinition md)
    {
        int p = md.Start;
        if (!md.Static) return p;
        var src = _source;
        if (string.CompareOrdinal(src, p, "static", 0, 6) != 0) return p;
        p += 6;
        while (p < src.Length)
        {
            char c = src[p];
            if (char.IsWhiteSpace(c) || c == '\uFEFF') { p++; continue; }
            if (c == '/' && p + 1 < src.Length && src[p + 1] == '/') { while (p < src.Length && src[p] != '\n' && src[p] != '\r' && src[p] != '\u2028' && src[p] != '\u2029') p++; continue; }
            if (c == '/' && p + 1 < src.Length && src[p + 1] == '*') { int e = src.IndexOf("*/", p + 2, System.StringComparison.Ordinal); p = e < 0 ? src.Length : e + 2; continue; }
            break;
        }
        return p;
    }

    /// <summary>
    /// ForIn/OfHeadEvaluation: the expression is evaluated in a TDZ environment holding the loop's lexical names
    /// (closures created there see those names uninitialized forever).
    /// </summary>
    private int ForHeadExpr(Expression right, Scope? outer)
    {
        if (outer == null || !outer.HasEnv) return Expr(right);
        EmitPushEnv(outer);
        _envDepth++;
        _control.Add(new ControlEntry { Kind = EntryKind.Env });
        int src = Temp();
        Expr(right, src);
        _fb.Emit(Op.PopEnv);
        _envDepth--;
        _control.RemoveAt(_control.Count - 1);
        return src;
    }

    private int EvalCallFlags()
    {
        int flags = _func.Strict ? 1 : 0;
        var fn = _func;
        while (fn.IsArrow && !fn.IsEvalTop && fn.Parent != null) fn = fn.Parent;
        bool inFunction = fn.IsEvalTop ? _evalAllowNewTarget : !fn.IsScriptTop && !fn.IsModuleTop;
        if (inFunction) flags |= 2;
        if (_inParams && !_func.IsArrow) flags |= 4;
        if (fn.IsFieldInit || (fn.IsEvalTop && _evalInFieldInit)) flags |= 8;
        return flags;
    }

    private void With(WithStatement w)
    {
        _fb.Position(w);
        int m = Mark;
        int o = Expr(w.Object);
        var s = NodeTable.Scope(w);
        FinalizeEnvLayout(s);
        if (_completionReg >= 0) _fb.Emit(Op.LoadUndef, _completionReg);
        _fb.Emit(Op.PushWith, o);
        _envDepth++;
        _control.Add(new ControlEntry { Kind = EntryKind.Env });
        _scope = s;
        ResetTo(m);
        Statement(w.Body);
        _fb.Emit(Op.PopEnv);
        _envDepth--;
        _control.RemoveAt(_control.Count - 1);
        _scope = s.Parent!;
        ResetTo(m);
    }

    /// <summary>Compiles eval code (direct or indirect). The body runs with the caller's environment, this and new.target.</summary>
    public FunctionTemplate CompileEval(Program program, bool allowNewTarget, bool inFieldInit)
    {
        var fi = NodeTable.Function(program);
        _evalAllowNewTarget = allowNewTarget;
        _evalInFieldInit = inFieldInit;
        var t = new FunctionTemplate
        {
            Name = "eval",
            Script = _script,
            Flags = FunctionFlags.Arrow | (fi.Strict ? FunctionFlags.Strict : 0),
            SourceStart = program.Start,
            SourceEnd = program.End,
        };
        BeginFunction(fi, t);
        var scope = fi.Scope;
        _completionReg = _fb.AllocReg();
        _fb.Emit(Op.LoadUndef, _completionReg);
        bool sloppy = fi.EvalVarNames != null;
        if (sloppy && (fi.EvalVarNames!.Contains("arguments") || fi.EvalFunctionNames!.Contains("arguments"))) _script.EvalDeclaresArguments = true;
        if (sloppy && (fi.EvalVarNames!.Count > 0 || fi.EvalFunctionNames!.Count > 0))
        {
            var info = new EvalDeclInfo { VarNames = fi.EvalVarNames.ToArray(), FunctionNames = fi.EvalFunctionNames!.ToArray() };
            _fb.Emit(Op.EvalDeclare, _fb.AddObject(info));
        }
        FinalizeEnvLayout(scope);
        AssignFunctionScopeStorage(scope, simpleParams: true);
        _scope = scope;
        EmitPushEnv(scope);
        _envDepth++;
        _control.Add(new ControlEntry { Kind = EntryKind.Env });
        if (sloppy)
        {
            var seen = new HashSet<string>();
            for (int i = scope.HoistedFunctions.Count - 1; i >= 0; i--)
            {
                // the last declaration of a name wins
                var fd = scope.HoistedFunctions[i];
                if (fd.Id == null || !seen.Add(fd.Id.Name)) continue;
                int r = Temp();
                int idx = CompileNestedFunction(fd, fd.Id.Name);
                _fb.Emit(Op.Closure, r, idx);
                _fb.Emit(Op.EvalInitVar, _fb.Const(fd.Id.Name), r, 0);
                Release(r);
            }
        }
        else InstantiateHoisted(scope);
        foreach (var st in program.Body) Statement(st);
        _fb.Emit(Op.Ret, _completionReg);
        return EndFunction(t);
    }
}
