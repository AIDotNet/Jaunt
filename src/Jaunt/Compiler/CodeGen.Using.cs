using System;
using System.Collections.Generic;
using Acornima.Ast;

namespace Jaunt.Compiler;

/// <summary>
/// Explicit resource management: statement lists containing <c>using</c> / <c>await using</c> declarations are
/// wrapped in a dispose region that behaves like try/finally (DisposeResources runs on every exit and turns a
/// failing dispose into a throw completion, with SuppressedError when one was already pending).
/// </summary>
internal sealed partial class CodeGen
{
    private readonly Stack<(int Reg, bool Async)> _disposeStacks = new();

    private static bool HasUsing(in NodeList<Statement> stmts, out bool async)
    {
        async = false;
        bool any = false;
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            if (s is VariableDeclaration { Kind: VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing } vd)
            {
                any = true;
                if (vd.Kind == VariableDeclarationKind.AwaitUsing) async = true;
            }
        }
        return any;
    }

    /// <summary>Compiles a statement list, adding a dispose region when it declares resources.</summary>
    private void StatementList(in NodeList<Statement> stmts)
    {
        if (!HasUsing(stmts, out bool async))
        {
            for (int i = 0; i < stmts.Count; i++) Statement(stmts[i]);
            return;
        }
        StatementListWithDispose(stmts, async);
    }

    // separate method: a lambda in StatementList would allocate its closure on every call
    private void StatementListWithDispose(NodeList<Statement> stmts, bool async) =>
        DisposeRegion(async, () => { for (int i = 0; i < stmts.Count; i++) Statement(stmts[i]); });

    /// <summary>for (using x of/in …) body: each iteration's value is disposed at the end of that iteration.</summary>
    private void UsingLoopBody(Node left, Statement body, int val, bool async) => DisposeRegion(async, () =>
    {
        _fb.Emit(Op.AddDisposable, _disposeStacks.Peek().Reg, val, async ? 1 : 0);
        BindForTarget(left, val);
        Statement(body);
    });

    private void ForWithDispose(ForStatement f, List<string>? labels, bool async) => DisposeRegion(async, () => For(f, labels));

    private void DisposeRegion(bool async, Action body)
    {
        int m = Mark;
        int depth = _envDepth;
        int disp = Temp();
        _fb.Emit(Op.NewDisposeStack, disp);
        var fin = new FinallyInfo { Label = _fb.NewLabel(), TypeReg = Temp(), ValueReg = Temp() };
        var entry = new ControlEntry { Kind = EntryKind.Finally, Finally = fin };
        _control.Add(entry);
        _disposeStacks.Push((disp, async));
        int tryStart = _fb.Pc;
        body();
        int tryEnd = _fb.Pc;
        _disposeStacks.Pop();
        _fb.Emit(Op.LoadInt, fin.TypeReg, 0);
        _fb.Jump(fin.Label);
        int throwStub = _fb.NewLabel();
        _fb.Mark(throwStub);
        _fb.Handlers.Add(new HandlerEntry { Start = tryStart, End = tryEnd, Target = _fb.LabelPosition(throwStub), ExceptionReg = fin.ValueReg, EnvDepth = depth });
        _fb.Emit(Op.LoadInt, fin.TypeReg, 1);
        _control.Remove(entry);
        _fb.Mark(fin.Label);
        if (async) EmitAsyncDispose(disp, fin, depth);
        else _fb.Emit(Op.DisposeSync, disp, fin.TypeReg, fin.ValueReg);
        // dispatch on the (possibly updated) completion
        int after = _fb.NewLabel();
        int n = 3 + fin.Jumps.Count;
        int throwL = _fb.NewLabel(), retL = _fb.NewLabel();
        var targets = new int[n];
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
        _fb.Mark(after);
        ResetTo(m);
    }

    /// <summary>Async DisposeResources: awaits each resource's dispose result in reverse order.</summary>
    private void EmitAsyncDispose(int disp, FinallyInfo fin, int depth)
    {
        int fn = Temp(), thisV = Temp(), r = Temp(), exc = Temp();
        int loop = _fb.NewLabel(), done = _fb.NewLabel(), awaitOnly = _fb.NewLabel(), handler = _fb.NewLabel();
        _fb.Mark(loop);
        _fb.Emit(Op.DisposeNext, fn, thisV, disp);
        _fb.LabelRef(done);
        int start = _fb.Pc;
        _fb.JumpIf(Op.JmpUndef, fn, awaitOnly);
        _fb.Emit(Op.Call, r, fn, thisV, r, 0);
        EmitAwait(r, r);
        _fb.Jump(loop);
        _fb.Mark(awaitOnly);
        _fb.Emit(Op.LoadUndef, r);
        EmitAwait(r, r);
        _fb.Jump(loop);
        int end = _fb.Pc;
        _fb.Mark(handler);
        _fb.Handlers.Add(new HandlerEntry { Start = start, End = end, Target = _fb.LabelPosition(handler), ExceptionReg = exc, EnvDepth = depth });
        _fb.Emit(Op.DisposeMerge, fin.TypeReg, fin.ValueReg, exc);
        _fb.Jump(loop);
        _fb.Mark(done);
    }

    /// <summary><c>using x = v</c> / <c>await using x = v</c>.</summary>
    private void UsingDeclaration(VariableDeclaration vd)
    {
        if (_disposeStacks.Count == 0) throw new CompileError("SyntaxError", "'using' declarations are not allowed here", vd);
        var (disp, _) = _disposeStacks.Peek();
        int hint = vd.Kind == VariableDeclarationKind.AwaitUsing ? 1 : 0;
        foreach (var d in vd.Declarations)
        {
            _fb.Position(d);
            int m = Mark;
            int r = d.Init == null ? Temp() : Expr(d.Init, -1, (d.Id as Identifier)?.Name);
            if (d.Init == null) _fb.Emit(Op.LoadUndef, r);
            _fb.Emit(Op.AddDisposable, disp, r, hint);
            BindPattern(d.Id, r, PatternMode.Init);
            ResetTo(m);
        }
    }
}
