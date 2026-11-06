using Acornima.Ast;
using System.Collections.Generic;

namespace Jaunt.Compiler;

/// <summary>Async generators, <c>for await</c> and async <c>yield*</c>.</summary>
internal sealed partial class CodeGen
{
    private bool IsAsyncGenerator => _template.IsGenerator && _template.IsAsync;

    /// <summary>d = Await(v); a rejection is thrown at this point.</summary>
    private void EmitAwait(int v, int d)
    {
        int mode = Temp();
        _fb.Emit(Op.Await, v, d, mode);
        int ok = _fb.NewLabel();
        _fb.JumpIf(Op.JmpFalse, mode, ok);
        _fb.Emit(Op.Throw, d);
        _fb.Mark(ok);
        Release(mode);
    }

    /// <summary>AsyncIteratorClose with a normal completion (break / continue outer / return through the loop).</summary>
    private void EmitAsyncIteratorClose(ControlEntry e)
    {
        int m = Mark;
        int skip = _fb.NewLabel();
        _fb.JumpIf(Op.JmpTrue, e.FlagReg, skip);
        _fb.Emit(Op.LoadTrue, e.FlagReg);
        int fn = Temp(), r = Temp();
        _fb.Emit(Op.GetProp, fn, e.Reg, _fb.Const("return"), _fb.NewIc());
        _fb.JumpIf(Op.JmpNullish, fn, skip);
        _fb.Emit(Op.Call, r, fn, e.Reg, r, 0);
        EmitAwait(r, r);
        _fb.Emit(Op.ThrowIfNotObject, r, _fb.Const("Iterator result is not an object"));
        _fb.Mark(skip);
        ResetTo(m);
    }

    private void ForAwait(ForOfStatement f, List<string>? labels)
    {
        _fb.Position(f);
        int m = Mark;
        int iter = Temp(), next = Temp(), flag = Temp(), res = Temp(), val = Temp();
        Scope? outer = _scopes.ContainsKey(f) ? EnterScope(f, pushEnv: false) : null;
        int src = ForHeadExpr(f.Right, outer);
        _fb.Position(f.Right);
        _fb.Emit(Op.GetAsyncIterator, iter, src);
        _fb.Emit(Op.GetProp, next, iter, _fb.Const("next"), _fb.NewIc());
        _fb.Emit(Op.LoadFalse, flag);
        int start = _fb.NewLabel(), cont = _fb.NewLabel(), end = _fb.NewLabel(), brk = _fb.NewLabel();
        _fb.Mark(start);
        _fb.Emit(Op.Call, res, next, iter, res, 0);
        EmitAwait(res, res);
        _fb.Emit(Op.ThrowIfNotObject, res, _fb.Const("Iterator result is not an object"));
        _fb.Emit(Op.GetProp, val, res, _fb.Const("done"), _fb.NewIc());
        _fb.JumpIf(Op.JmpTrue, val, end);
        _fb.Emit(Op.GetProp, val, res, _fb.Const("value"), _fb.NewIc());
        int regionStart = _fb.Pc;
        int handlerDepth = _envDepth;
        _control.Add(new ControlEntry { Kind = EntryKind.Break, Labels = labels, Label = brk, IsLoop = true });
        _control.Add(new ControlEntry { Kind = EntryKind.AsyncIterator, Reg = iter, FlagReg = flag });
        _control.Add(new ControlEntry { Kind = EntryKind.Continue, Labels = labels, Label = cont, IsLoop = true });
        if (outer != null && outer.HasEnv)
        {
            EmitPushEnv(outer);
            _envDepth++;
            _control.Add(new ControlEntry { Kind = EntryKind.Env });
        }
        if (f.Left is VariableDeclaration { Kind: VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing } uvd)
        {
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

        // throw completion inside the body: AsyncIteratorClose, errors from return() are suppressed
        int handler = _fb.NewLabel(), rethrow = _fb.NewLabel();
        _fb.Mark(handler);
        int exc = Temp();
        _fb.Handlers.Add(new HandlerEntry { Start = regionStart, End = regionEnd, Target = _fb.LabelPosition(handler), ExceptionReg = exc, EnvDepth = handlerDepth });
        _fb.JumpIf(Op.JmpTrue, flag, rethrow);
        _fb.Emit(Op.LoadTrue, flag);
        int fn = Temp(), r = Temp(), ignored = Temp();
        int closeStart = _fb.Pc;
        _fb.Emit(Op.GetProp, fn, iter, _fb.Const("return"), _fb.NewIc());
        _fb.JumpIf(Op.JmpNullish, fn, rethrow);
        _fb.Emit(Op.Call, r, fn, iter, r, 0);
        EmitAwait(r, r);
        int closeEnd = _fb.Pc;
        _fb.Jump(rethrow);
        int swallow = _fb.NewLabel();
        _fb.Mark(swallow);
        _fb.Handlers.Add(new HandlerEntry { Start = closeStart, End = closeEnd, Target = _fb.LabelPosition(swallow), ExceptionReg = ignored, EnvDepth = handlerDepth });
        _fb.Mark(rethrow);
        _fb.Emit(Op.Throw, exc);
        _fb.Mark(brk);
        _fb.Mark(end);
        if (outer != null) ExitScope(outer, m, popEnv: false);
        ResetTo(m);
    }

    /// <summary>yield in an async generator: Await(value), yield, and on a return resumption Await(received) before returning.</summary>
    private int YieldAsync(YieldExpression y, int d, int m)
    {
        int v = Temp();
        if (y.Argument != null)
        {
            int a = Expr(y.Argument);
            EmitAwait(a, v);
        }
        else _fb.Emit(Op.LoadUndef, v);
        int mode = Temp();
        _fb.Position(y);
        _fb.Emit(Op.Yield, v, d, mode);
        EmitAsyncResumeDispatch(d, mode);
        ResetTo(System.Math.Max(m, d + 1));
        return d;
    }

    private void EmitAsyncResumeDispatch(int d, int mode)
    {
        int next = _fb.NewLabel(), isReturn = _fb.NewLabel();
        _fb.JumpIf(Op.JmpFalse, mode, next);
        int two = Temp();
        _fb.Emit(Op.LoadInt, two, 2);
        _fb.Emit(Op.StrictEq, two, mode, two);
        _fb.JumpIf(Op.JmpTrue, two, isReturn);
        _fb.Emit(Op.Throw, d);
        _fb.Mark(isReturn);
        EmitAwait(d, d);
        EmitReturn(d);
        _fb.Mark(next);
    }

    /// <summary>yield* in an async generator (14.4.4, generatorKind = async).</summary>
    private int YieldDelegateAsync(YieldExpression y, int d, int m)
    {
        int src = Expr(y.Argument!);
        int iter = Temp(), next = Temp();
        _fb.Emit(Op.GetAsyncIterator, iter, src);
        _fb.Emit(Op.GetProp, next, iter, _fb.Const("next"), _fb.NewIc());
        int received = Temp(), mode = Temp(), res = Temp(), tmp = Temp(), fn = Temp();
        _fb.Emit(Op.LoadUndef, received);
        _fb.Emit(Op.LoadInt, mode, 0);
        int loop = _fb.NewLabel(), done = _fb.NewLabel(), notNext = _fb.NewLabel(), notThrow = _fb.NewLabel(), haveRes = _fb.NewLabel();
        const string notObject = "Iterator result is not an object";
        _fb.Mark(loop);
        _fb.JumpIf(Op.JmpTrue, mode, notNext);
        _fb.Emit(Op.Call, res, next, iter, received, 1);
        _fb.Jump(haveRes);
        _fb.Mark(notNext);
        _fb.Emit(Op.LoadInt, tmp, 1);
        _fb.Emit(Op.StrictEq, tmp, mode, tmp);
        _fb.JumpIf(Op.JmpFalse, tmp, notThrow);
        // throw mode
        {
            _fb.Emit(Op.GetProp, fn, iter, _fb.Const("throw"), _fb.NewIc());
            int hasThrow = _fb.NewLabel();
            _fb.JumpIf(Op.JmpNotNullish, fn, hasThrow);
            // no throw(): AsyncIteratorClose(normal), then TypeError
            _fb.Emit(Op.GetProp, fn, iter, _fb.Const("return"), _fb.NewIc());
            int noRet = _fb.NewLabel();
            _fb.JumpIf(Op.JmpNullish, fn, noRet);
            _fb.Emit(Op.Call, tmp, fn, iter, tmp, 0);
            EmitAwait(tmp, tmp);
            _fb.Emit(Op.ThrowIfNotObject, tmp, _fb.Const(notObject));
            _fb.Mark(noRet);
            _fb.Emit(Op.ThrowTypeError, _fb.Const("The iterator does not provide a 'throw' method"));
            _fb.Mark(hasThrow);
            _fb.Emit(Op.Call, res, fn, iter, received, 1);
            _fb.Jump(haveRes);
        }
        _fb.Mark(notThrow);
        // return mode
        {
            _fb.Emit(Op.GetProp, fn, iter, _fb.Const("return"), _fb.NewIc());
            int hasReturn = _fb.NewLabel();
            _fb.JumpIf(Op.JmpNotNullish, fn, hasReturn);
            EmitAwait(received, received);
            EmitReturn(received);
            _fb.Mark(hasReturn);
            _fb.Emit(Op.Call, res, fn, iter, received, 1);
            EmitAwait(res, res);
            _fb.Emit(Op.ThrowIfNotObject, res, _fb.Const(notObject));
            int notDone = _fb.NewLabel();
            _fb.Emit(Op.GetProp, tmp, res, _fb.Const("done"), _fb.NewIc());
            _fb.JumpIf(Op.JmpFalse, tmp, notDone);
            _fb.Emit(Op.GetProp, tmp, res, _fb.Const("value"), _fb.NewIc());
            EmitReturn(tmp);
            _fb.Mark(notDone);
            EmitDelegateYield(res, received, mode, tmp);
            _fb.Jump(loop);
        }
        _fb.Mark(haveRes);
        EmitAwait(res, res);
        _fb.Emit(Op.ThrowIfNotObject, res, _fb.Const(notObject));
        _fb.Emit(Op.GetProp, tmp, res, _fb.Const("done"), _fb.NewIc());
        _fb.JumpIf(Op.JmpTrue, tmp, done);
        EmitDelegateYield(res, received, mode, tmp);
        _fb.Jump(loop);
        _fb.Mark(done);
        _fb.Emit(Op.GetProp, d, res, _fb.Const("value"), _fb.NewIc());
        ResetTo(System.Math.Max(m, d + 1));
        return d;
    }

    /// <summary>
    /// received = AsyncGeneratorYield(IteratorValue(res)). On a return resumption the value is awaited first; a
    /// rejection turns the received completion into a throw completion.
    /// </summary>
    private void EmitDelegateYield(int res, int received, int mode, int tmp)
    {
        _fb.Emit(Op.GetProp, tmp, res, _fb.Const("value"), _fb.NewIc());
        _fb.Emit(Op.Yield, tmp, received, mode);
        int after = _fb.NewLabel();
        int two = Temp();
        _fb.Emit(Op.LoadInt, two, 2);
        _fb.Emit(Op.StrictEq, two, mode, two);
        _fb.JumpIf(Op.JmpFalse, two, after);
        int awaitMode = Temp();
        _fb.Emit(Op.Await, received, received, awaitMode);
        _fb.JumpIf(Op.JmpFalse, awaitMode, after);
        _fb.Emit(Op.LoadInt, mode, 1); // rejected: continue with a throw completion
        _fb.Mark(after);
        Release(awaitMode);
        Release(two);
    }
}
