using System;
using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace Jaunt.Compiler;

/// <summary>
/// Second compiler pass: walks the Acornima AST (annotated by <see cref="ScopeAnalyzer"/>) and emits register
/// bytecode. The AST is not retained after compilation.
/// </summary>
internal sealed partial class CodeGen
{
    private enum EntryKind { Env, Break, Continue, Iterator, AsyncIterator, Finally, Label }

    private sealed class FinallyInfo
    {
        public int Label;
        public int TypeReg;
        public int ValueReg;
        public bool HasReturn;
        public readonly List<(int targetEntry, bool isContinue)> Jumps = new();
    }

    private sealed class ControlEntry
    {
        public EntryKind Kind;
        public List<string>? Labels;
        public int Label;
        public int Reg;
        public FinallyInfo? Finally;
        public bool IsLoop;
        /// <summary>AsyncIterator: register set to true once the iterator was closed (or must not be closed).</summary>
        public int FlagReg;
    }
    private readonly ScopeAnalyzer _analyzer;
    private readonly ScriptTemplate _script;
    private readonly string _source;
    private readonly Action? _checkpoint;

    // per-function state
    private FunctionBuilder _fb = null!;
    private FunctionInfo _func = null!;
    private Scope _scope = null!;
    private List<ControlEntry> _control = new();
    private int _envDepth;
    private int _completionReg = -1;
    /// <summary>Set by EmitTailReturn for the call expression in tail position (read and cleared by Call).</summary>
    private bool _tailCall;
    /// <summary>Nesting of try blocks that have a catch clause (a tail call there would escape the handler).</summary>
    private int _tryCatchDepth;
    private int _optionalExit = -1;
    private FunctionTemplate _template = null!;
    private int _depth;
    private readonly int _maxDepth;

    public CodeGen(ScriptTemplate script, string source, ScopeAnalyzer analyzer, int maxDepth, Action? checkpoint)
    {
        _script = script;
        _source = source;
        _analyzer = analyzer;
        _maxDepth = maxDepth;
        _checkpoint = checkpoint;
    }

    private void Enter(Node node)
    {
        if (++_depth > _maxDepth) throw new CompileError("NestingTooDeep", "Maximum syntax nesting depth exceeded.", node);
        // the probe leaves room for far more than 16 levels; a recursion that goes deep crosses every multiple of 16
        if ((_depth & 15) == 0) System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
    }

    private void Leave() => _depth--;

    private static void FinalizeEnvLayout(Scope s)
    {
        if (s.Kind == ScopeKind.Script) return;
        if (s.Kind == ScopeKind.With || s.ForceEnv) { s.HasEnv = true; return; }
        bool any = s.Kind == ScopeKind.Module;
        foreach (var b in s.Ordered) if (b.Captured && b.Kind != BindingKind.Import) { any = true; break; }
        s.HasEnv = any;
    }

    // ------------------------------------------------------------------ top level

    public FunctionTemplate CompileProgram(Program program, bool isModule)
    {
        var fi = NodeTable.Function(program);
        var t = new FunctionTemplate
        {
            Name = isModule ? "<module>" : "<script>",
            Script = _script,
            Flags = (fi.Strict ? FunctionFlags.Strict : 0) | (isModule ? FunctionFlags.ModuleBody : FunctionFlags.ScriptBody),
            SourceStart = program.Start,
            SourceEnd = program.End,
        };
        BeginFunction(fi, t);
        var scope = fi.Scope;
        _scope = scope;
        if (isModule)
        {
            if (fi.ContainsAwait) t.Flags |= FunctionFlags.Async;
            // module env is created by the linker; assign slots: vars/functions first, then lexical
            int slot = 0;
            var mod = _script.Module!;
            foreach (var b in scope.Ordered)
            {
                if (b.Kind == BindingKind.Import) continue;
                if (!b.IsLexical) { b.EnvSlot = slot++; mod.EnvSlots[b.Name] = b.EnvSlot; }
            }
            scope.LexStart = slot;
            foreach (var b in scope.Ordered)
            {
                if (b.Kind == BindingKind.Import) continue;
                if (b.IsLexical) { b.EnvSlot = slot++; mod.EnvSlots[b.Name] = b.EnvSlot; mod.LexicalSlots.Add(b.EnvSlot); }
            }
            scope.EnvSize = slot;
            scope.HasEnv = true;
            mod.EnvSize = slot;
            if (scope.Named) mod.Layout = BuildLayout(scope);
            mod.HasTopLevelAwait = fi.ContainsAwait;
            _envDepth = 0;
            // hoisted functions are instantiated by the linker
            foreach (var fd in scope.HoistedFunctions)
            {
                int idx = CompileNestedFunction(fd, fd.Id?.Name ?? "default");
                string name = fd.Id?.Name ?? "*default*";
                mod.HoistedFunctions.Add((idx, mod.EnvSlots[name]));
            }
        }
        else
        {
            // script: var/function → global object, let/const/class → global lexical environment
            var vars = new List<string>();
            var funcs = new List<string>();
            var lex = new List<string>();
            var lexConst = new List<bool>();
            foreach (var b in scope.Ordered)
            {
                if (b.Kind == BindingKind.Var) vars.Add(b.Name);
                else if (b.Kind == BindingKind.Function) funcs.Add(b.Name);
                else if (b.IsLexical) { lex.Add(b.Name); lexConst.Add(b.Kind == BindingKind.Const); }
            }
            _script.VarNames = vars.ToArray();
            _script.FunctionNames = funcs.ToArray();
            _script.LexNames = lex.ToArray();
            _script.LexIsConst = lexConst.ToArray();
            _completionReg = _fb.AllocReg();
            foreach (var fd in scope.HoistedFunctions)
            {
                int r = _fb.AllocReg();
                int idx = CompileNestedFunction(fd, fd.Id!.Name);
                _fb.Emit(Op.Closure, r, idx);
                _fb.Emit(Op.SetGlobal, _fb.Const(fd.Id.Name), r, _fb.NewIc());
                _fb.NextReg--;
            }
        }

        StatementList(program.Body);

        if (isModule) _fb.Emit(Op.RetUndef);
        else _fb.Emit(Op.Ret, _completionReg);
        return EndFunction(t);
    }

    private void BeginFunction(FunctionInfo fi, FunctionTemplate t)
    {
        _fb = FunctionBuilder.Rent();
        _func = fi;
        _template = t;
        _control = new List<ControlEntry>();
        _envDepth = 0;
        _tryCatchDepth = 0;
        _completionReg = -1;
        _optionalExit = -1;
        _inParams = false;
    }

    private FunctionTemplate EndFunction(FunctionTemplate t)
    {
        _fb.Finish(t);
        _fb.Return();
        BytecodeVerifier.Verify(t);
        BytecodeVerifier.MarkThisBinding(t);
        _script.FunctionCount++;
        _script.IcTotal += t.IcCount;
        return t;
    }

    /// <summary>Compiles a nested function and returns its index in the current function's table.</summary>
    private int CompileNestedFunction(Node fnNode, string name, FunctionFlags extra = 0, int sourceStart = -1)
    {
        var saved = (_fb, _func, _scope, _control, _envDepth, _completionReg, _optionalExit, _template, _tryCatchDepth);
        bool savedInParams = _inParams;
        // loop-hoisted constant registers belong to the enclosing function's register file
        var savedConsts = (_loopConsts, _loopStringConsts);
        (_loopConsts, _loopStringConsts) = (null, null);
        var t = CompileFunction(fnNode, name, extra);
        (_loopConsts, _loopStringConsts) = savedConsts;
        _inParams = savedInParams;
        if (sourceStart >= 0 && sourceStart < t.SourceStart) t.SourceStart = sourceStart;
        (_fb, _func, _scope, _control, _envDepth, _completionReg, _optionalExit, _template, _tryCatchDepth) = saved;
        return _fb.AddFunction(t);
    }

    private FunctionTemplate CompileFunction(Node fnNode, string name, FunctionFlags extra)
    {
        Enter(fnNode);
        var fi = NodeTable.Function(fnNode);
        var f = (IFunction)fnNode;
        var flags = extra;
        if (fi.Strict) flags |= FunctionFlags.Strict;
        if (f.Generator) flags |= FunctionFlags.Generator;
        if (f.Async) flags |= FunctionFlags.Async;
        if (fi.IsArrow) flags |= FunctionFlags.Arrow;
        if ((f.Generator || f.Async) && (flags & FunctionFlags.Method) == 0) flags |= FunctionFlags.NoPrototype;
        if (fi.UsesArguments) flags |= FunctionFlags.UsesArguments;

        var t = new FunctionTemplate
        {
            Name = name,
            Script = _script,
            Flags = flags,
            SourceStart = fnNode.Start,
            SourceEnd = fnNode.End,
        };
        BeginFunction(fi, t);
        var scope = fi.Scope;

        // formal parameters: incoming arguments occupy r0..r(n-1)
        var ps = f.Params;
        int formalCount = 0;
        bool simple = true;
        int paramRegs = 0;
        foreach (var p in ps)
        {
            if (p is RestElement) { simple = false; t.Flags |= FunctionFlags.HasRest; continue; }
            if (p is not Identifier) simple = false;
            paramRegs++;
        }
        foreach (var p in ps)
        {
            if (p is RestElement || p is AssignmentPattern) break;
            formalCount++;
        }
        t.Length = formalCount;
        t.ParamCount = paramRegs;
        if ((t.Flags & FunctionFlags.HasRest) != 0) t.RestIndex = paramRegs;
        if (fi.UsesArguments || (t.Flags & FunctionFlags.HasRest) != 0) t.Flags |= FunctionFlags.NeedsArgsArray;
        if (fi.ArgumentsElided && !fi.ArgumentsIndexed) t.Flags |= FunctionFlags.ArgcOnly;
        if ((t.Flags & FunctionFlags.HasRest) != 0 && !fi.UsesArguments) t.Flags |= FunctionFlags.RestOnlyArgs;
        if (fi.MappedArguments) t.Flags |= FunctionFlags.MappedArguments;
        for (int i = 0; i < paramRegs; i++) _fb.AllocReg();

        FinalizeEnvLayout(scope);
        // storage for function-scope bindings
        AssignFunctionScopeStorage(scope, simple);
        t.InitUndefEnd = _fb.NextReg;
        _scope = scope;
        if (scope.HasEnv)
        {
            EmitPushEnv(scope);
            _envDepth++;
            _control.Add(new ControlEntry { Kind = EntryKind.Env });
        }
        // copy simple captured params into env
        foreach (var b in scope.Ordered)
        {
            if (b.Kind == BindingKind.Param && b.ParamIndex >= 0 && b.Captured && simple)
                _fb.Emit(Op.StEnv, 0, b.EnvSlot, b.ParamIndex);
        }
        if (fi.FuncNameBinding != null)
        {
            int r = Temp();
            _fb.Emit(Op.LoadCallee, r);
            StoreBinding(fi.FuncNameBinding, r, init: true, null);
            Release(r);
        }
        if (fi.ArgumentsBinding != null && !fi.ArgumentsElided)
        {
            int r = Temp();
            if (fi.MappedArguments)
            {
                // index → environment slot of the parameter it aliases (last duplicate wins)
                var map = new int[paramRegs];
                for (int i = 0; i < map.Length; i++) map[i] = -1;
                foreach (var b in scope.Ordered)
                    if (b.Kind == BindingKind.Param && b.ParamIndex >= 0 && b.ParamIndex < map.Length && b.Captured) map[b.ParamIndex] = b.EnvSlot;
                _fb.Emit(Op.CreateMappedArguments, r, _fb.AddObject(map));
            }
            else _fb.Emit(Op.CreateArguments, r);
            StoreBinding(fi.ArgumentsBinding, r, init: true, null);
            Release(r);
        }
        if (!simple)
        {
            _inParams = true;
            int pi = 0;
            foreach (var p in ps)
            {
                if (p is RestElement re)
                {
                    int r = Temp();
                    _fb.Emit(Op.CreateRestArray, r, pi);
                    BindPattern(re.Argument, r, PatternMode.Init);
                    Release(r);
                    continue;
                }
                BindPattern(p, pi, PatternMode.Init);
                pi++;
            }
            _inParams = false;
        }
        // hoisted function declarations
        InstantiateHoisted(scope);
        if (fi.BodyScope != null && f.Body is BlockStatement bodyBlock)
        {
            // separate variable environment for the body: same-named vars start with the parameter's value
            EnterScope(bodyBlock);
            foreach (var b in fi.BodyScope.Ordered)
            {
                if (b.Kind is not (BindingKind.Var or BindingKind.Function)) continue;
                var pb = scope.Lookup(b.Name);
                if (pb == null || pb.Kind is not (BindingKind.Param or BindingKind.Arguments)) continue;
                int r = LoadBinding(pb, -1, false);
                StoreBinding(b, r, init: true, null);
            }
            InstantiateHoisted(fi.BodyScope);
        }

        if (f.Generator) _fb.Emit(Op.InitialYield, Temp(), Temp());
        if (f.Generator) _fb.NextReg -= 2;

        if (f.Body is BlockStatement body)
        {
            StatementList(body.Body);
            _fb.Emit(Op.RetUndef);
        }
        else if (TailCallsAllowed()) EmitTailReturn((Expression)f.Body);
        else
        {
            int r = Expr((Expression)f.Body);
            _fb.Emit(Op.Ret, r);
        }
        Leave();
        return EndFunction(t);
    }

    private void AssignFunctionScopeStorage(Scope scope, bool simpleParams)
    {
        int slot = 0;
        foreach (var b in scope.Ordered)
        {
            if (b.Captured && !b.HasTdz) b.EnvSlot = slot++;
        }
        scope.LexStart = slot;
        foreach (var b in scope.Ordered)
        {
            if (b.Captured && b.HasTdz) b.EnvSlot = slot++;
        }
        scope.EnvSize = slot;
        foreach (var b in scope.Ordered)
        {
            if (b.Captured) continue;
            if (b.ArgumentsElided && !scope.Function.ArgumentsIndexed) continue;   // no slow path that could need the object
            if (b.Kind == BindingKind.Param && b.ParamIndex >= 0 && simpleParams) { b.Register = b.ParamIndex; continue; }
            b.Register = _fb.AllocReg();
        }
        foreach (var b in scope.Ordered)
        {
            if (!b.Captured && b.HasTdz && b.NeedsTdz) _fb.Emit(Op.LoadEmpty, b.Register);
        }
    }

    private void InstantiateHoisted(Scope scope)
    {
        foreach (var fd in scope.HoistedFunctions)
        {
            if (fd.Id == null) continue;
            var b = scope.Lookup(fd.Id.Name)!;
            int r = Temp();
            int idx = CompileNestedFunction(fd, fd.Id.Name);
            _fb.Emit(Op.Closure, r, idx);
            StoreBinding(b, r, init: true, null);
            Release(r);
        }
    }

    // ------------------------------------------------------------------ registers

    private int Temp() => _fb.AllocReg();
    private void Release(int reg) { if (reg == _fb.NextReg - 1) _fb.NextReg--; }
    private int Mark => _fb.NextReg;
    private void ResetTo(int mark) => _fb.NextReg = mark;

    // ------------------------------------------------------------------ block scopes

    private Scope? EnterScope(Node owner, bool pushEnv = true)
    {
        if (!NodeTable.TryGetScope(owner, out var s)) return null;
        FinalizeEnvLayout(s);
        int slot = 0;
        foreach (var b in s.Ordered) if (b.Captured && !b.IsLexical && b.Kind != BindingKind.Import) b.EnvSlot = slot++;
        s.LexStart = slot;
        foreach (var b in s.Ordered) if (b.Captured && b.IsLexical) b.EnvSlot = slot++;
        s.EnvSize = slot;
        foreach (var b in s.Ordered)
        {
            if (b.Captured || b.Kind == BindingKind.Import) continue;
            b.Register = _fb.AllocReg();
            if (b.IsLexical && b.NeedsTdz) _fb.Emit(Op.LoadEmpty, b.Register);
        }
        _scope = s;
        if (s.HasEnv && pushEnv)
        {
            EmitPushEnv(s);
            _envDepth++;
            _control.Add(new ControlEntry { Kind = EntryKind.Env });
        }
        return s;
    }

    private void ExitScope(Scope? s, int mark, bool popEnv = true)
    {
        if (s == null) return;
        if (s.HasEnv && popEnv)
        {
            _fb.Emit(Op.PopEnv);
            _envDepth--;
            _control.RemoveAt(_control.Count - 1);
        }
        _scope = s.Parent!;
        ResetTo(mark);
    }

    private int EnvDepthTo(Scope target)
    {
        int depth = 0;
        for (var s = _scope; s != null && s != target; s = s.Parent)
        {
            if (s.HasEnv) depth++;
        }
        return depth;
    }

    // ------------------------------------------------------------------ binding load/store

    private int LoadIdentifier(Identifier id, int dst)
    {
        var r = ResolvedRef.Get(id);
        var b = r.Binding;
        if (b == null)
        {
            if (dst < 0) dst = Temp();
            if (r.Dynamic) _fb.Emit(Op.LdName, dst, _fb.Const(id.Name), 0);
            else _fb.Emit(Op.GetGlobal, dst, _fb.Const(id.Name), _fb.NewIc());
            return dst;
        }
        return LoadBinding(b, dst, r.NeedsTdzCheck);
    }

    private int LoadBinding(Binding b, int dst, bool tdz)
    {
        if (b.ArgumentsElided) throw new InvalidOperationException("Elided arguments object read as a value.");   // ScopeAnalyzer allows only the forms Member handles
        if (b.IsEvalDynamic)
        {
            if (dst < 0) dst = Temp();
            _fb.Emit(Op.LdName, dst, _fb.Const(b.Name), 0);
            return dst;
        }
        if (b.Scope.Kind == ScopeKind.Script)
        {
            // script top-level declarations live on the global object / global lexical environment
            if (dst < 0) dst = Temp();
            _fb.Emit(Op.GetGlobal, dst, _fb.Const(b.Name), _fb.NewIc());
            return dst;
        }
        if (b.Kind == BindingKind.Import)
        {
            if (dst < 0) dst = Temp();
            _fb.Emit(Op.LdImport, dst, b.ImportIndex, _fb.Const(b.Name));
            return dst;
        }
        if (b.Captured)
        {
            if (dst < 0) dst = Temp();
            int depth = EnvDepthTo(b.Scope);
            if (tdz && b.HasTdz) _fb.Emit(Op.LdEnvChk, dst, depth, b.EnvSlot, _fb.Const(b.Name));
            else _fb.Emit(Op.LdEnv, dst, depth, b.EnvSlot);
            return dst;
        }
        if (tdz && b.HasTdz) _fb.Emit(Op.CheckTdz, b.Register, _fb.Const(b.Name));
        if (dst >= 0 && dst != b.Register) { _fb.Emit(Op.Mov, dst, b.Register); return dst; }
        return b.Register;
    }

    /// <summary>Stores <paramref name="src"/> into an identifier reference (assignment or initialization).</summary>
    private void StoreIdentifier(Identifier id, int src, bool init)
    {
        var r = ResolvedRef.Get(id);
        if (r.Binding == null)
        {
            if (r.Dynamic)
            {
                _fb.Emit(Op.StName, _fb.Const(id.Name), src, _func.Strict ? 1 : 0);
                return;
            }
            if (init && _scope.Function.IsScriptTop && IsScriptLexical(id.Name))
                _fb.Emit(Op.InitGlobalLex, _fb.Const(id.Name), src);
            else
                _fb.Emit(Op.SetGlobal, _fb.Const(id.Name), src, _fb.NewIc());
            return;
        }
        StoreBinding(r.Binding, src, init, id, r.NeedsTdzCheck);
    }

    private bool IsScriptLexical(string name)
    {
        var top = _scope;
        while (top.Parent != null) top = top.Parent;
        var b = top.Lookup(name);
        return b != null && b.IsLexical;
    }

    private void StoreBinding(Binding b, int src, bool init, Node? node, bool tdz = false)
    {
        if (b.IsEvalDynamic)
        {
            _fb.Emit(Op.StName, _fb.Const(b.Name), src, _func.Strict ? 1 : 0);
            return;
        }
        if (b.Scope.Kind == ScopeKind.Script)
        {
            if (init && b.IsLexical) _fb.Emit(Op.InitGlobalLex, _fb.Const(b.Name), src);
            else _fb.Emit(Op.SetGlobal, _fb.Const(b.Name), src, _fb.NewIc());
            return;
        }
        if (!init)
        {
            if (b.Kind == BindingKind.FuncName)
            {
                if (_func.Strict) _fb.Emit(Op.ThrowTypeError, _fb.Const("Assignment to constant variable."));
                return;
            }
            if (b.IsConst)
            {
                if (tdz) LoadBinding(b, -1, true);
                _fb.Emit(Op.ThrowConstAssign, _fb.Const(b.Name));
                return;
            }
        }
        if (b.Captured)
        {
            int depth = EnvDepthTo(b.Scope);
            if (!init && tdz && b.HasTdz) _fb.Emit(Op.StEnvChk, depth, b.EnvSlot, src, _fb.Const(b.Name));
            else _fb.Emit(Op.StEnv, depth, b.EnvSlot, src);
            return;
        }
        if (!init && tdz && b.HasTdz) _fb.Emit(Op.CheckTdz, b.Register, _fb.Const(b.Name));
        if (b.Register != src) _fb.Emit(Op.Mov, b.Register, src);
    }

    // ------------------------------------------------------------------ control flow helpers

    private void EmitJump(int targetEntry, bool isContinue)
    {
        for (int i = _control.Count - 1; i > targetEntry; i--)
        {
            var e = _control[i];
            switch (e.Kind)
            {
                case EntryKind.Env: _fb.Emit(Op.PopEnv); break;
                case EntryKind.Iterator: _fb.Emit(Op.IterClose, e.Reg); break;
                case EntryKind.AsyncIterator: EmitAsyncIteratorClose(e); break;
                case EntryKind.Finally:
                {
                    var f = e.Finally!;
                    int id = 3 + f.Jumps.Count;
                    f.Jumps.Add((targetEntry, isContinue));
                    _fb.Emit(Op.LoadInt, f.TypeReg, id);
                    _fb.Jump(f.Label);
                    return;
                }
            }
        }
        _fb.Jump(_control[targetEntry].Label);
    }

    // ------------------------------------------------------------------ proper tail calls (15.10)

    /// <summary>
    /// A `return` (or concise arrow body) is a tail position in strict, non-generator, non-async function code
    /// when nothing must run after the call: no enclosing try-with-catch, finally, for-of iterator or using region.
    /// </summary>
    private bool TailCallsAllowed()
    {
        if (!_func.Strict || _func.IsScriptTop || _func.IsModuleTop || _func.IsEvalTop || _func.IsFieldInit || _func.IsSynthetic) return false;
        if ((_template.Flags & (FunctionFlags.Generator | FunctionFlags.Async)) != 0) return false;
        if (_tryCatchDepth > 0) return false;
        foreach (var e in _control)
            if (e.Kind is EntryKind.Iterator or EntryKind.AsyncIterator or EntryKind.Finally) return false;
        return true;
    }

    /// <summary>Returns the value of an expression, marking calls in tail position (conditional / logical / comma operands).</summary>
    private void EmitTailReturn(Expression e)
    {
        int m = Mark;
        switch (e)
        {
            case ConditionalExpression c:
            {
                int elseL = _fb.NewLabel();
                Condition(c.Test, elseL);
                ResetTo(m);
                EmitTailReturn(c.Consequent);
                _fb.Mark(elseL);
                EmitTailReturn(c.Alternate);
                return;
            }
            case LogicalExpression le:
            {
                int l = Expr(le.Left);
                int rhs = _fb.NewLabel();
                var jump = le.Operator switch
                {
                    Operator.LogicalAnd => Op.JmpTrue,
                    Operator.LogicalOr => Op.JmpFalse,
                    _ => Op.JmpNullish,
                };
                _fb.JumpIf(jump, l, rhs);
                _fb.Emit(Op.Ret, l);
                _fb.Mark(rhs);
                ResetTo(m);
                EmitTailReturn(le.Right);
                return;
            }
            case SequenceExpression se:
            {
                for (int i = 0; i < se.Expressions.Count - 1; i++) { ExprDiscard(se.Expressions[i]); ResetTo(m); }
                EmitTailReturn(se.Expressions[se.Expressions.Count - 1]);
                return;
            }
            case ParenthesizedExpression pe:
                EmitTailReturn(pe.Expression);
                return;
            case CallExpression { Optional: false } call when call.Callee is not Super and not ChainExpression:
            case TaggedTemplateExpression:
            {
                _tailCall = true;
                int r = Expr(e);
                _tailCall = false;
                _fb.Emit(Op.Ret, r);
                ResetTo(m);
                return;
            }
            default:
            {
                int r = Expr(e);
                _fb.Emit(Op.Ret, r);
                ResetTo(m);
                return;
            }
        }
    }

    /// <summary><c>return c ? a : b</c> as <c>if (c) return a; else return b;</c>: without a move into a common register and a jump to one Ret.</summary>
    private void EmitConditionalReturn(Expression e)
    {
        Enter(e);
        int m = Mark;
        if (e is ConditionalExpression c)
        {
            int elseL = _fb.NewLabel();
            Condition(c.Test, elseL);
            ResetTo(m);
            EmitConditionalReturn(c.Consequent);
            _fb.Mark(elseL);
            EmitConditionalReturn(c.Alternate);
        }
        else EmitReturn(Expr(e));
        ResetTo(m);
        Leave();
    }

    private void EmitReturn(int valueReg)
    {
        for (int i = _control.Count - 1; i >= 0; i--)
        {
            var e = _control[i];
            switch (e.Kind)
            {
                case EntryKind.Iterator: _fb.Emit(Op.IterClose, e.Reg); break;
                case EntryKind.AsyncIterator: EmitAsyncIteratorClose(e); break;
                case EntryKind.Finally:
                {
                    var f = e.Finally!;
                    f.HasReturn = true;
                    if (valueReg != f.ValueReg) _fb.Emit(Op.Mov, f.ValueReg, valueReg);
                    _fb.Emit(Op.LoadInt, f.TypeReg, 2);
                    _fb.Jump(f.Label);
                    return;
                }
            }
        }
        _fb.Emit(Op.Ret, valueReg);
    }

    private int FindBreakTarget(string? label, Node node)
    {
        for (int i = _control.Count - 1; i >= 0; i--)
        {
            var e = _control[i];
            if (e.Kind == EntryKind.Break && (label == null ? e.IsLoop : e.Labels != null && e.Labels.Contains(label))) return i;
            if (e.Kind == EntryKind.Label && label != null && e.Labels != null && e.Labels.Contains(label)) return i;
        }
        throw new CompileError("IllegalBreak", label == null ? "Illegal break statement" : $"Undefined label '{label}'", node);
    }

    private int FindContinueTarget(string? label, Node node)
    {
        for (int i = _control.Count - 1; i >= 0; i--)
        {
            var e = _control[i];
            if (e.Kind == EntryKind.Continue && (label == null || (e.Labels != null && e.Labels.Contains(label)))) return i;
        }
        throw new CompileError("IllegalContinue", label == null ? "Illegal continue statement" : $"Undefined label '{label}'", node);
    }

    // ------------------------------------------------------------------ statements

    private List<string>? _pendingLabels;
    private Dictionary<double, int>? _loopConsts;
    private Dictionary<string, int>? _loopStringConsts;

    /// <summary>
    /// Loads literal operands that occur in a loop (outside nested functions) into registers once, before the
    /// loop, so the loop body does not re-materialize them every iteration. Returns the previous maps to restore.
    /// </summary>
    private (Dictionary<double, int>?, Dictionary<string, int>?) HoistLoopConstants(params Node?[] parts)
    {
        var saved = (_loopConsts, _loopStringConsts);
        var nums = new List<double>();
        var strs = new List<string>();
        void Scan(Node? n, int depth)
        {
            if (n == null || depth > 64 || nums.Count + strs.Count >= 8) return;
            if (n is IFunction or ClassBody) return;
            if (n is BinaryExpression be && be is not LogicalExpression)
            {
                if (ImmediateForm(be) != null || be.Operator is Operator.StrictEquality or Operator.StrictInequality) { }   // the literal travels in the instruction
                else if (be.Right is NumericLiteral rn && !nums.Contains(rn.Value) && !(_loopConsts?.ContainsKey(rn.Value) ?? false)) nums.Add(rn.Value);
                else if (be.Right is StringLiteral rs && !strs.Contains(rs.Value) && !(_loopStringConsts?.ContainsKey(rs.Value) ?? false)) strs.Add(rs.Value);
            }
            else if (n is AssignmentExpression ae && ae.Operator != Operator.Assignment && ae.Right is NumericLiteral an && !nums.Contains(an.Value) && !(_loopConsts?.ContainsKey(an.Value) ?? false))
                nums.Add(an.Value);
            foreach (var c in n.ChildNodes) Scan(c, depth + 1);
        }
        foreach (var p in parts) Scan(p, 0);
        if (nums.Count == 0 && strs.Count == 0) return saved;
        _loopConsts = _loopConsts == null ? new Dictionary<double, int>() : new Dictionary<double, int>(_loopConsts);
        _loopStringConsts = _loopStringConsts == null ? new Dictionary<string, int>(StringComparer.Ordinal) : new Dictionary<string, int>(_loopStringConsts, StringComparer.Ordinal);
        foreach (var v in nums)
        {
            int r = Temp();
            if (v == Math.Floor(v) && v >= int.MinValue && v <= int.MaxValue && !(v == 0 && double.IsNegative(v))) _fb.Emit(Op.LoadInt, r, (int)v);
            else _fb.Emit(Op.LoadConst, r, _fb.Const(v));
            _loopConsts[v] = r;
        }
        foreach (var v in strs)
        {
            int r = Temp();
            _fb.Emit(Op.LoadConst, r, _fb.Const(v));
            _loopStringConsts[v] = r;
        }
        return saved;
    }

    private void Statement(Statement node)
    {
        Enter(node);
        _checkpoint?.Invoke();
        var labels = _pendingLabels;
        _pendingLabels = null;
        // completion values (script / eval code): these statements complete with UpdateEmpty(result, undefined)
        if (_completionReg >= 0 && node is IfStatement or ForStatement or ForInStatement or ForOfStatement or WhileStatement
            or DoWhileStatement or SwitchStatement or TryStatement)
            _fb.Emit(Op.LoadUndef, _completionReg);
        switch (node)
        {
            case ExpressionStatement es:
            {
                _fb.Position(es);
                int m = Mark;
                if (_completionReg >= 0) Expr(es.Expression, _completionReg);
                else ExprDiscard(es.Expression);
                ResetTo(m);
                break;
            }
            case VariableDeclaration vd: VariableDeclaration(vd); break;
            case FunctionDeclaration fd:
                // hoisted; Annex B: copy block binding into the var binding when evaluated
                if (fd.Id != null && ResolvedRef.TryGet(fd.Id, out var fref) && fref.Binding?.AnnexBVar is { } annex)
                {
                    int m = Mark;
                    int r = LoadBinding(fref.Binding, -1, false);
                    if (annex.IsEvalDynamic) _fb.Emit(Op.EvalInitVar, _fb.Const(annex.Name), r, 1);   // B.3.2.3: SetMutableBinding
                    else StoreBinding(annex, r, init: false, fd);
                    ResetTo(m);
                }
                break;
            case ClassDeclaration cd:
            {
                _fb.Position(cd);
                int m = Mark;
                int r = Class(cd, cd.Id, cd.SuperClass, cd.Body, cd.Id?.Name ?? "", -1);
                StoreIdentifier(cd.Id!, r, init: true);
                ResetTo(m);
                break;
            }
            case ReturnStatement rs:
            {
                _fb.Position(rs);
                int m = Mark;
                int r;
                if (rs.Argument != null && TailCallsAllowed())
                {
                    EmitTailReturn(rs.Argument);
                    ResetTo(m);
                    break;
                }
                if (rs.Argument is ConditionalExpression && !IsAsyncGenerator)
                {
                    EmitConditionalReturn(rs.Argument);
                    ResetTo(m);
                    break;
                }
                if (rs.Argument != null)
                {
                    r = Expr(rs.Argument);
                    if (IsAsyncGenerator)
                    {
                        int t = Temp();
                        EmitAwait(r, t);
                        r = t;
                    }
                }
                else { r = Temp(); _fb.Emit(Op.LoadUndef, r); }
                EmitReturn(r);
                ResetTo(m);
                break;
            }
            case IfStatement s:
            {
                _fb.Position(s);
                int elseL = _fb.NewLabel();
                int m = Mark;
                Condition(s.Test, elseL);
                ResetTo(m);
                Statement(_analyzer.ClauseBlock(s.Consequent)!);
                if (s.Alternate != null)
                {
                    int end = _fb.NewLabel();
                    _fb.Jump(end);
                    _fb.Mark(elseL);
                    Statement(_analyzer.ClauseBlock(s.Alternate)!);
                    _fb.Mark(end);
                }
                else _fb.Mark(elseL);
                break;
            }
            case BlockStatement b:
            {
                if (labels != null)
                {
                    var brk = _fb.NewLabel();
                    _control.Add(new ControlEntry { Kind = EntryKind.Label, Labels = labels, Label = brk });
                    Block(b);
                    _control.RemoveAt(_control.Count - 1);
                    _fb.Mark(brk);
                }
                else Block(b);
                break;
            }
            case ForStatement f:
                if (f.Init is VariableDeclaration { Kind: VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing } fuvd)
                    ForWithDispose(f, labels, fuvd.Kind == VariableDeclarationKind.AwaitUsing);
                else For(f, labels);
                break;
            case ForInStatement fin: ForIn(fin, labels); break;
            case ForOfStatement fof: if (fof.Await) ForAwait(fof, labels); else ForOf(fof, labels); break;
            case WhileStatement w: While(w, labels); break;
            case DoWhileStatement dw: DoWhile(dw, labels); break;
            case BreakStatement bs:
                _fb.Position(bs);
                EmitJump(FindBreakTarget(bs.Label?.Name, bs), false);
                break;
            case ContinueStatement cs:
                _fb.Position(cs);
                EmitJump(FindContinueTarget(cs.Label?.Name, cs), true);
                break;
            case ThrowStatement t:
            {
                _fb.Position(t);
                int m = Mark;
                int r = Expr(t.Argument);
                _fb.Position(t);
                _fb.Emit(Op.Throw, r);
                ResetTo(m);
                break;
            }
            case TryStatement t: Try(t, labels); break;
            case SwitchStatement sw: Switch(sw, labels); break;
            case LabeledStatement ls:
            {
                var list = labels ?? new List<string>();
                list.Add(ls.Label.Name);
                if (ls.Body is ForStatement or ForInStatement or ForOfStatement or WhileStatement or DoWhileStatement or BlockStatement)
                {
                    _pendingLabels = list;
                    Statement(ls.Body);
                }
                else
                {
                    var brk = _fb.NewLabel();
                    _control.Add(new ControlEntry { Kind = EntryKind.Label, Labels = list, Label = brk });
                    Statement(ls.Body);
                    _control.RemoveAt(_control.Count - 1);
                    _fb.Mark(brk);
                }
                break;
            }
            case EmptyStatement: break;
            case DebuggerStatement: _fb.Emit(Op.Debugger); break;
            case ImportDeclaration: break;
            case ExportNamedDeclaration e:
                if (e.Declaration != null) Statement(e.Declaration);
                break;
            case ExportDefaultDeclaration e:
                ExportDefault(e);
                break;
            case ExportAllDeclaration: break;
            case WithStatement ws: With(ws); break;
            default:
                throw new CompileError("Unsupported", $"Unsupported statement '{node.Type}'.", node);
        }
        Leave();
    }

    private void ExportDefault(ExportDefaultDeclaration e)
    {
        var scope = _func.Scope;
        switch (e.Declaration)
        {
            case FunctionDeclaration:
                break; // hoisted by linker
            case ClassDeclaration cd:
            {
                int m = Mark;
                int r = Class(cd, cd.Id, cd.SuperClass, cd.Body, cd.Id?.Name ?? "default", -1);
                var b = scope.Lookup(cd.Id?.Name ?? "*default*")!;
                StoreBinding(b, r, init: true, cd);
                ResetTo(m);
                break;
            }
            default:
            {
                int m = Mark;
                int r = Expr((Expression)e.Declaration, -1, "default");
                StoreBinding(scope.Lookup("*default*")!, r, init: true, e);
                ResetTo(m);
                break;
            }
        }
    }

    private void Block(BlockStatement b)
    {
        int m = Mark;
        var s = EnterScope(b);
        if (s != null) InstantiateHoisted(s);
        StatementList(b.Body);
        ExitScope(s, m);
    }

    private void VariableDeclaration(VariableDeclaration vd)
    {
        if (vd.Kind is VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing) { UsingDeclaration(vd); return; }
        foreach (var d in vd.Declarations)
        {
            _fb.Position(d);
            int m = Mark;
            if (d.Init == null)
            {
                if (vd.Kind == VariableDeclarationKind.Var) continue;
                int r = Temp();
                _fb.Emit(Op.LoadUndef, r);
                BindPattern(d.Id, r, PatternMode.Init);
            }
            else if (d.Id is Identifier did && vd.Kind == VariableDeclarationKind.Var && IsDynamicRef(did))
            {
                // ResolveBinding happens before the initializer is evaluated (13.3.2.4)
                int rr = EmitResolveRef(did);
                int v = Expr(d.Init, -1, did.Name);
                EmitStoreRef(rr, v);
            }
            else if (d.Id is Identifier id)
            {
                var res = ResolvedRef.Get(id);
                if (res.Binding != null && !res.Binding.Captured && res.Binding.Kind != BindingKind.Import && res.Binding.Register >= 0
                    && !res.NeedsTdzCheck && !Mentions(d.Init, id.Name))
                {
                    // compute directly into the binding register
                    Expr(d.Init, res.Binding.Register, id.Name);
                }
                else
                {
                    int r = Expr(d.Init, -1, id.Name);
                    StoreIdentifier(id, r, init: vd.Kind != VariableDeclarationKind.Var);
                }
            }
            else
            {
                int r = Expr(d.Init);
                BindPattern(d.Id, r, vd.Kind == VariableDeclarationKind.Var ? PatternMode.Assign : PatternMode.Init);
            }
            ResetTo(m);
        }
    }

    private static bool ContainsAssignmentTo(Node n) => true;

    /// <summary>True when <paramref name="node"/> (excluding nested functions) mentions identifier <paramref name="name"/>.</summary>
    private static bool Mentions(Node node, string name)
    {
        if (node is Identifier id) return id.Name == name;
        if (node is IFunction) return true; // conservative
        foreach (var c in node.ChildNodes)
        {
            if (c != null && Mentions(c, name)) return true;
        }
        return false;
    }

    private void While(WhileStatement w, List<string>? labels)
    {
        _fb.Position(w);
        int cm = Mark;
        var savedConsts = HoistLoopConstants(w.Test, w.Body);
        int start = _fb.NewLabel(), test = _fb.NewLabel(), end = _fb.NewLabel();
        LoopEntry(w.Test, test, end);
        _fb.Mark(start);
        PushLoop(labels, end, test);
        Statement(w.Body);
        PopLoop();
        _fb.Mark(test);
        int m = Mark;
        ConditionTrue(w.Test, start);
        ResetTo(m);
        _fb.Mark(end);
        (_loopConsts, _loopStringConsts) = savedConsts;
        ResetTo(cm);
    }

    private void DoWhile(DoWhileStatement dw, List<string>? labels)
    {
        int start = _fb.NewLabel(), cont = _fb.NewLabel(), end = _fb.NewLabel();
        _fb.Mark(start);
        PushLoop(labels, end, cont);
        Statement(dw.Body);
        PopLoop();
        _fb.Mark(cont);
        int m = Mark;
        _fb.Position(dw.Test);
        int r = Expr(dw.Test);
        _fb.JumpIf(Op.JmpTrue, r, start);
        ResetTo(m);
        _fb.Mark(end);
    }

    /// <summary>
    /// Enters a loop whose test is compiled at its bottom (see <see cref="ConditionTrue"/>): a simple test is also compiled here,
    /// jumping past the loop when it fails; any other test is reached by a jump.
    /// </summary>
    private void LoopEntry(Expression test, int testLabel, int endLabel)
    {
        int budget = 16;
        if (CanDuplicateTest(test, ref budget))
        {
            int m = Mark;
            Condition(test, endLabel);
            ResetTo(m);
        }
        else _fb.Jump(testLabel);
    }

    private void PushLoop(List<string>? labels, int breakLabel, int continueLabel)
    {
        _control.Add(new ControlEntry { Kind = EntryKind.Break, Labels = labels, Label = breakLabel, IsLoop = true });
        _control.Add(new ControlEntry { Kind = EntryKind.Continue, Labels = labels, Label = continueLabel, IsLoop = true });
    }

    private void PopLoop() => _control.RemoveRange(_control.Count - 2, 2);

    private void For(ForStatement f, List<string>? labels)
    {
        _fb.Position(f);
        int m = Mark;
        var scope = EnterScope(f);
        if (f.Init is VariableDeclaration vd) VariableDeclaration(vd);
        else if (f.Init is Expression ie) ExprDiscard(ie);
        var savedConsts = HoistLoopConstants(f.Test, f.Update, f.Body);
        // CreatePerIterationEnvironment runs once before the first test: closures created by the initializer
        // keep the loop environment, the first iteration gets a copy
        if (scope != null && scope.HasEnv && f.Init is VariableDeclaration { Kind: VariableDeclarationKind.Let }) _fb.Emit(Op.CopyEnv);
        int start = _fb.NewLabel(), cont = _fb.NewLabel(), test = _fb.NewLabel(), end = _fb.NewLabel();
        if (f.Test != null) LoopEntry(f.Test, test, end);
        _fb.Mark(start);
        PushLoop(labels, end, cont);
        Statement(f.Body);
        PopLoop();
        _fb.Mark(cont);
        if (scope != null && scope.HasEnv) _fb.Emit(Op.CopyEnv);
        if (f.Update != null) ExprDiscard(f.Update);
        if (f.Test != null)
        {
            _fb.Mark(test);
            int mm = Mark;
            ConditionTrue(f.Test, start);
            ResetTo(mm);
        }
        else _fb.Jump(start);
        _fb.Mark(end);
        (_loopConsts, _loopStringConsts) = savedConsts;
        ExitScope(scope, m);
    }

    private void ForIn(ForInStatement f, List<string>? labels)
    {
        _fb.Position(f);
        int m = Mark;
        int iter = Temp(), val = Temp();
        // lexical bindings: registers/slots are assigned here; the environment is created per iteration
        Scope? outer = NodeTable.TryGetScope(f, out _) ? EnterScope(f, pushEnv: false) : null;
        if (f.Left is VariableDeclaration { Kind: VariableDeclarationKind.Var } ivd
            && ivd.Declarations[0] is { Init: { } init, Id: Identifier initId })
        {
            // Annex B.3.5 (sloppy only): the initializer is evaluated and assigned before the object expression
            int im = Mark;
            int iv = Expr(init, -1, initId.Name);
            StoreIdentifier(initId, iv, init: false);
            ResetTo(im);
        }
        int src = ForHeadExpr(f.Right, outer);
        _fb.Emit(Op.ForInPrepare, iter, src);
        int start = _fb.NewLabel(), cont = _fb.NewLabel(), end = _fb.NewLabel();
        _fb.Mark(start);
        _fb.Emit(Op.ForInNext, val, iter);
        _fb.LabelRef(end);
        PushLoop(labels, end, cont);
        if (outer != null && outer.HasEnv)
        {
            // fresh per-iteration environment
            EmitPushEnv(outer);
            _envDepth++;
            _control.Add(new ControlEntry { Kind = EntryKind.Env });
        }
        if (f.Left is VariableDeclaration { Kind: VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing } uvd)
        {
            // for (using x of ...): each iteration's value is disposed at the end of that iteration
            UsingLoopBody(f.Left, f.Body, val, uvd.Kind == VariableDeclarationKind.AwaitUsing);
        }
        else
        {
            BindForTarget(f.Left, val);
            Statement(f.Body);
        }
        if (outer != null && outer.HasEnv)
        {
            _fb.Emit(Op.PopEnv);
            _envDepth--;
            _control.RemoveAt(_control.Count - 1);
        }
        PopLoop();
        _fb.Mark(cont);
        _fb.Jump(start);
        _fb.Mark(end);
        if (outer != null) ExitScope(outer, m, popEnv: false);
        ResetTo(m);
    }

    private void BindForTarget(Node left, int val)
    {
        if (left is VariableDeclaration vd)
        {
            var d = vd.Declarations[0];
            BindPattern(d.Id, val, vd.Kind == VariableDeclarationKind.Var ? PatternMode.Assign : PatternMode.Init);
        }
        else if (left is CallExpression call) CallAssignmentTarget(call, -1);
        else BindPattern(left, val, PatternMode.Assign);
    }

    private void ForOf(ForOfStatement f, List<string>? labels)
    {
        _fb.Position(f);
        int m = Mark;
        int iter = Temp(), val = Temp();
        Scope? outer = NodeTable.TryGetScope(f, out _) ? EnterScope(f, pushEnv: false) : null;
        int src = ForHeadExpr(f.Right, outer);
        _fb.Position(f.Right);
        _fb.Emit(Op.GetIterator, iter, src);
        int start = _fb.NewLabel(), cont = _fb.NewLabel(), end = _fb.NewLabel(), brk = _fb.NewLabel();
        _fb.Mark(start);
        _fb.Emit(Op.IterNext, val, iter);
        _fb.LabelRef(end);
        int regionStart = _fb.Pc;
        int handlerDepth = _envDepth;
        _control.Add(new ControlEntry { Kind = EntryKind.Break, Labels = labels, Label = brk, IsLoop = true });
        _control.Add(new ControlEntry { Kind = EntryKind.Iterator, Reg = iter });
        _control.Add(new ControlEntry { Kind = EntryKind.Continue, Labels = labels, Label = cont, IsLoop = true });
        if (outer != null && outer.HasEnv)
        {
            EmitPushEnv(outer);
            _envDepth++;
            _control.Add(new ControlEntry { Kind = EntryKind.Env });
        }
        if (f.Left is VariableDeclaration { Kind: VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing } uvd)
        {
            // for (using x of ...): each iteration's value is disposed at the end of that iteration
            UsingLoopBody(f.Left, f.Body, val, uvd.Kind == VariableDeclarationKind.AwaitUsing);
        }
        else
        {
            BindForTarget(f.Left, val);
            Statement(f.Body);
        }
        if (outer != null && outer.HasEnv)
        {
            _fb.Emit(Op.PopEnv);
            _envDepth--;
            _control.RemoveAt(_control.Count - 1);
        }
        int regionEnd = _fb.Pc;
        _control.RemoveRange(_control.Count - 3, 3);
        _fb.Mark(cont);
        _fb.Jump(start);
        // exception inside the body closes the iterator
        int handler = _fb.NewLabel();
        _fb.Mark(handler);
        int exc = Temp();
        _fb.Handlers.Add(new HandlerEntry { Start = regionStart, End = regionEnd, Target = _fb.LabelPosition(handler), ExceptionReg = exc, EnvDepth = handlerDepth });
        _fb.Emit(Op.IterCloseQuiet, iter);
        _fb.Emit(Op.Throw, exc);
        _fb.Mark(brk);
        _fb.Mark(end);
        if (outer != null) ExitScope(outer, m, popEnv: false);
        ResetTo(m);
    }

    private void Switch(SwitchStatement sw, List<string>? labels)
    {
        _fb.Position(sw);
        int m = Mark;
        int disc = Temp();
        Expr(sw.Discriminant, disc);
        var scope = EnterScope(sw);
        if (scope != null) InstantiateHoisted(scope);
        int end = _fb.NewLabel();
        var caseLabels = new int[sw.Cases.Count];
        int defaultIdx = -1;
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            caseLabels[i] = _fb.NewLabel();
            var c = sw.Cases[i];
            if (c.Test == null) { defaultIdx = i; continue; }
            int mm = Mark;
            if (LiteralConstant(c.Test) is { } lit)
            {
                // "jump unless they differ": the instruction falls through to the next case test when they differ
                _fb.JumpCmp(Op.StrictNeKJmpFalse, disc, lit, caseLabels[i]);
                continue;
            }
            int t = Expr(c.Test);
            int cmp = Temp();
            _fb.Emit(Op.StrictEq, cmp, disc, t);
            _fb.JumpIf(Op.JmpTrue, cmp, caseLabels[i]);
            ResetTo(mm);
        }
        _fb.Jump(defaultIdx >= 0 ? caseLabels[defaultIdx] : end);
        _control.Add(new ControlEntry { Kind = EntryKind.Break, Labels = labels, Label = end, IsLoop = true });
        // a switch is a valid unlabeled break target but not a continue target
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            _fb.Mark(caseLabels[i]);
            foreach (var st in sw.Cases[i].Consequent) Statement(st);
        }
        _control.RemoveAt(_control.Count - 1);
        _fb.Mark(end);
        ExitScope(scope, m);
        ResetTo(m);
    }

    private void Try(TryStatement t, List<string>? labels)
    {
        _fb.Position(t);
        int m = Mark;
        int depth = _envDepth;
        FinallyInfo? fin = null;
        ControlEntry? finEntry = null;
        int after = _fb.NewLabel();
        int labelBreak = -1;
        if (labels != null)
        {
            labelBreak = _fb.NewLabel();
            _control.Add(new ControlEntry { Kind = EntryKind.Label, Labels = labels, Label = labelBreak });
        }
        if (t.Finalizer != null)
        {
            fin = new FinallyInfo { Label = _fb.NewLabel(), TypeReg = Temp(), ValueReg = Temp() };
            finEntry = new ControlEntry { Kind = EntryKind.Finally, Finally = fin };
            _control.Add(finEntry);
        }
        int tryStart = _fb.Pc;
        if (t.Handler != null) _tryCatchDepth++;
        Block(t.Block);
        if (t.Handler != null) _tryCatchDepth--;
        int tryEnd = _fb.Pc;
        if (fin != null) { _fb.Emit(Op.LoadInt, fin.TypeReg, 0); _fb.Jump(fin.Label); }
        else _fb.Jump(after);

        int catchEnd = tryEnd;
        if (t.Handler != null)
        {
            int catchLabel = _fb.NewLabel();
            _fb.Mark(catchLabel);
            int mm = Mark;
            int exc = Temp();
            _fb.Handlers.Add(new HandlerEntry { Start = tryStart, End = tryEnd, Target = _fb.LabelPosition(catchLabel), ExceptionReg = exc, EnvDepth = depth });
            var cs = EnterScope(t.Handler);
            if (t.Handler.Param != null) BindPattern(t.Handler.Param, exc, PatternMode.Init);
            if (cs != null) InstantiateHoisted(cs);
            if (t.Handler.Param is null or Identifier) StatementList(t.Handler.Body.Body);
            else Block(t.Handler.Body);   // separate block environment (see ScopeAnalyzer)
            ExitScope(cs, mm);
            ResetTo(mm);
            catchEnd = _fb.Pc;
            if (fin != null) { _fb.Emit(Op.LoadInt, fin.TypeReg, 0); _fb.Jump(fin.Label); }
            else _fb.Jump(after);
        }

        if (fin != null)
        {
            int throwStub = _fb.NewLabel();
            _fb.Mark(throwStub);
            _fb.Handlers.Add(new HandlerEntry { Start = tryStart, End = catchEnd, Target = _fb.LabelPosition(throwStub), ExceptionReg = fin.ValueReg, EnvDepth = depth });
            _fb.Emit(Op.LoadInt, fin.TypeReg, 1);
            _control.Remove(finEntry!);
            _fb.Mark(fin.Label);
            // a normally completing finally block does not change the completion value
            int savedCompletion = -1;
            if (_completionReg >= 0)
            {
                savedCompletion = Temp();
                _fb.Emit(Op.Mov, savedCompletion, _completionReg);
                // break/continue out of the finally block carries its own value (UpdateEmpty(F, undefined))
                _fb.Emit(Op.LoadUndef, _completionReg);
            }
            Block(t.Finalizer!);
            if (savedCompletion >= 0) _fb.Emit(Op.Mov, _completionReg, savedCompletion);
            // dispatch on completion type
            int n = 3 + fin.Jumps.Count;
            int[] targets = new int[n];
            int throwL = _fb.NewLabel(), retL = _fb.NewLabel();
            targets[0] = after;
            targets[1] = throwL;
            targets[2] = fin.HasReturn ? retL : after;
            var jumpLabels = new int[fin.Jumps.Count];
            for (int i = 0; i < fin.Jumps.Count; i++) { jumpLabels[i] = _fb.NewLabel(); targets[3 + i] = jumpLabels[i]; }
            _fb.Emit(Op.SwitchInt, fin.TypeReg, n);
            foreach (var l in targets) _fb.LabelRef(l);
            _fb.Mark(throwL);
            _fb.Emit(Op.Throw, fin.ValueReg);
            if (fin.HasReturn)
            {
                _fb.Mark(retL);
                EmitReturn(fin.ValueReg);
            }
            for (int i = 0; i < fin.Jumps.Count; i++)
            {
                _fb.Mark(jumpLabels[i]);
                var (target, isCont) = fin.Jumps[i];
                EmitJump(target, isCont);
            }
        }
        _fb.Mark(after);
        if (labels != null)
        {
            _control.RemoveAt(_control.Count - 1);
            _fb.Mark(labelBreak);
        }
        ResetTo(m);
    }
}
