using System;
using System.Runtime.CompilerServices;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

internal sealed partial class Vm
{
    /// <summary>
    /// Op.DelegateNext: Call(iterator.next, iterator, received), IteratorComplete and, when done, IteratorValue, as the
    /// general yield* loop does them, or a direct resume when the iterator is a generator with the original next.
    /// </summary>
    private int DelegateNext(JsValue iter, JsValue received, out JsValue res)
    {
        var realm = Realm;
        JsValue r;
        if (iter.Ref is JsGenerator g && realm.IsGeneratorIterationPristine(g))
        {
            int kind = Coroutines.ResumeStep(realm, g, Coroutines.ModeNext, received, out r);
            if (kind != Coroutines.StepRaw)
            {
                res = r;
                return kind == Coroutines.StepYield ? 0 : 2;
            }
        }
        else
        {
            var fn = Operations.GetV(realm, iter, "next");
            r = Call(fn, iter, new ReadOnlySpan<JsValue>(in received));
            if (r.IsNotObject) throw JsErrors.TypeError(realm, "Iterator result is not an object");
        }
        var ro = r.AsObjectUnchecked;
        if (Operations.ToBoolean(ro.Get("done", r)))
        {
            res = ro.Get("value", r);
            return 2;
        }
        res = r;
        return 1;
    }

    /// <summary>
    /// Executes an infrequent opcode outside the hot dispatch loop and returns the next pc. None of these
    /// opcodes switch frames; JS errors are raised as <see cref="JsThrowException"/> (f.Pc is already synced).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int RareOp(Frame f, int pc)
    {
        var realm = Realm;
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        JsValue[] k = f.K;
        switch ((Op)code[pc])
        {
            case Op.Nop:
            case Op.Debugger:
            case Op.InitFields:
                return pc + 1;
            case Op.LoadNewTarget: regs[bp + code[pc + 1]] = f.NewTargetValue; return pc + 2;
            case Op.LoadCallee: regs[bp + code[pc + 1]] = JsValue.FromObject(f.Callee); return pc + 2;
            case Op.ThrowConstAssign:
                throw JsErrors.TypeError(realm, "Assignment to constant variable.");
            case Op.ThrowTypeError:
                throw JsErrors.TypeError(realm, k[code[pc + 1]].AsStringUnchecked);
            case Op.ThrowRefError:
                throw JsErrors.ReferenceError(realm, k[code[pc + 1]].AsStringUnchecked + " is not defined");
            case Op.GetGlobalTypeof:
            {
                ref var ic = ref f.Feedback.Ics[code[pc + 3]];
                regs[bp + code[pc + 1]] = GetGlobalSlow(k[code[pc + 2]].AsStringUnchecked, ref ic, true);
                return pc + 4;
            }
            case Op.InitGlobalLex:
                realm.InitGlobalLexical(k[code[pc + 1]].AsStringUnchecked, regs[bp + code[pc + 2]]);
                return pc + 3;

            // ------------------------------------------------------------ properties
            case Op.DelProp:
            {
                var obj = Operations.ToObject(realm, regs[bp + code[pc + 2]]);
                bool ok = obj.Delete(new PropertyKey(k[code[pc + 3]].AsStringUnchecked));
                if (!ok && f.T.IsStrict) throw JsErrors.TypeError(realm, $"Cannot delete property '{k[code[pc + 3]]}'");
                regs[bp + code[pc + 1]] = JsValue.FromBool(ok);
                return pc + 4;
            }
            case Op.DelElem:
            {
                var obj = Operations.ToObject(realm, regs[bp + code[pc + 2]]);
                var key = Operations.ToPropertyKey(realm, regs[bp + code[pc + 3]]);
                bool ok = obj.Delete(key);
                if (!ok && f.T.IsStrict) throw JsErrors.TypeError(realm, $"Cannot delete property '{key}'");
                regs[bp + code[pc + 1]] = JsValue.FromBool(ok);
                return pc + 4;
            }
            case Op.LoadSuperBase:
            {
                var home = SuperBase(f);
                regs[bp + code[pc + 1]] = home == null ? JsValue.Null : JsValue.FromObject(home);
                return pc + 2;
            }
            case Op.GetSuper:
            {
                var bv = regs[bp + code[pc + 4]];
                var key = Operations.ToPropertyKey(realm, regs[bp + code[pc + 2]]);
                if (bv.IsNotObject) throw JsErrors.TypeError(realm, $"Cannot read properties of null (reading '{key}')");
                regs[bp + code[pc + 1]] = bv.AsObjectUnchecked.Get(key, regs[bp + code[pc + 3]]);
                return pc + 5;
            }
            case Op.SetSuper:
            {
                var bv = regs[bp + code[pc + 4]];
                var key = Operations.ToPropertyKey(realm, regs[bp + code[pc + 1]]);
                if (bv.IsNotObject) throw JsErrors.TypeError(realm, "Cannot set property on null super base");
                if (!bv.AsObjectUnchecked.Set(key, regs[bp + code[pc + 2]], regs[bp + code[pc + 3]]) && f.T.IsStrict)
                    throw JsErrors.TypeError(realm, $"Cannot assign to read only property '{key}'");
                return pc + 5;
            }
            case Op.DefineProp:
            {
                var obj = regs[bp + code[pc + 1]].AsObjectUnchecked;
                var key = Operations.ToPropertyKey(realm, regs[bp + code[pc + 2]]);
                obj.CreateDataPropertyOrThrow(key, regs[bp + code[pc + 3]]);
                return pc + 4;
            }
            case Op.DefineAccessor:
                DefineAccessorOp(regs[bp + code[pc + 1]].AsObjectUnchecked, regs[bp + code[pc + 2]], regs[bp + code[pc + 3]], code[pc + 4]);
                return pc + 5;
            case Op.DefineMethod:
                DefineAccessorOp(regs[bp + code[pc + 1]].AsObjectUnchecked, regs[bp + code[pc + 2]], regs[bp + code[pc + 3]], 2);
                return pc + 4;
            case Op.SetProto:
            {
                var v = regs[bp + code[pc + 2]];
                if (v.IsObject) regs[bp + code[pc + 1]].AsObjectUnchecked.SetPrototypeOf(v.AsObjectUnchecked);
                else if (v.IsNull) regs[bp + code[pc + 1]].AsObjectUnchecked.SetPrototypeOf(null);
                return pc + 3;
            }
            case Op.CopyDataProps:
                CopyDataProperties(regs[bp + code[pc + 1]].AsObjectUnchecked, regs[bp + code[pc + 2]], ReadOnlySpan<JsValue>.Empty, code[pc + 3]);
                return pc + 4;
            case Op.ObjectRest:
            {
                var target = new JsObject(realm, realm.ObjectPrototype);
                int start = code[pc + 3], count = code[pc + 4];
                var excl = count == 0 ? ReadOnlySpan<JsValue>.Empty : new ReadOnlySpan<JsValue>(regs, bp + start, count);
                CopyDataProperties(target, regs[bp + code[pc + 2]], excl);
                regs[bp + code[pc + 1]] = JsValue.FromObject(target);
                return pc + 5;
            }
            case Op.ArrayPushHole:
                ((JsArray)regs[bp + code[pc + 1]].Ref!).PushHole();
                return pc + 2;
            case Op.ArraySpread:
            {
                var arr = ((JsArray)regs[bp + code[pc + 1]].Ref!);
                var src = regs[bp + code[pc + 2]];
                if (src.IsObject && src.Ref is JsArray sa && realm.IsArrayIterationPristine(sa) && arr.TryAppendDense(sa, (uint)code[pc + 3]))
                {
                    Tick((int)Math.Min(sa.Length, int.MaxValue));
                    return pc + 4;
                }
                var rec = IteratorRecord.Get(realm, src);
                while (rec.Step(out var v)) { arr.Push(v); Tick(); }
                return pc + 4;
            }

            // ------------------------------------------------------------ operators
            case Op.Eq:
                regs[bp + code[pc + 1]] = JsValue.FromBool(Operations.LooseEquals(realm, regs[bp + code[pc + 2]], regs[bp + code[pc + 3]]));
                return pc + 4;
            case Op.Ne:
                regs[bp + code[pc + 1]] = JsValue.FromBool(!Operations.LooseEquals(realm, regs[bp + code[pc + 2]], regs[bp + code[pc + 3]]));
                return pc + 4;
            case Op.InstanceOf:
                regs[bp + code[pc + 1]] = JsValue.FromBool(Operations.InstanceOf(realm, regs[bp + code[pc + 2]], regs[bp + code[pc + 3]]));
                return pc + 4;
            case Op.In:
                regs[bp + code[pc + 1]] = JsValue.FromBool(Operations.HasProperty(realm, regs[bp + code[pc + 3]], regs[bp + code[pc + 2]]));
                return pc + 4;
            case Op.BitNot:
                regs[bp + code[pc + 1]] = BigIntOps.BitNot(realm, regs[bp + code[pc + 2]]);
                return pc + 3;
            case Op.ToPropertyKey:
                regs[bp + code[pc + 1]] = Operations.ToPropertyKeyValue(realm, regs[bp + code[pc + 2]]);
                return pc + 3;
            case Op.ToStringOp:
            {
                var a = regs[bp + code[pc + 2]];
                regs[bp + code[pc + 1]] = a.IsString ? a : new JsValue(Operations.ToString(realm, a));
                return pc + 3;
            }
            case Op.ToStrPart:
            {
                // the conversion can run user code: store through the frame's current register file afterwards
                var v = Operations.ToStringPart(realm, regs[bp + code[pc + 2]], code[pc + 5] != 0);
                regs = f.Regs;
                regs[bp + code[pc + 1]] = v;
                Operations.CheckPartsLength(realm, new ReadOnlySpan<JsValue>(regs, bp + code[pc + 3], code[pc + 4]));
                return pc + 6;
            }
            case Op.StrConcat:
                StrConcat(f, code[pc + 1], code[pc + 2], code[pc + 3], code[pc + 4] != 0);
                return pc + 5;
            case Op.AddConst:
                regs[bp + code[pc + 1]] = Operations.Add(realm, regs[bp + code[pc + 2]], k[code[pc + 3]]);
                return pc + 4;

            // ------------------------------------------------------------ calls that never push frames inline
            case Op.CallSpread:
            {
                var fv = regs[bp + code[pc + 2]];
                int tr = code[pc + 3];
                var arr = ((JsArray)regs[bp + code[pc + 4]].Ref!);
                int dst = bp + code[pc + 1];
                f.Pc = pc + 5;
                regs[dst] = CallValue(fv, tr < 0 ? default : regs[bp + tr], arr.ContiguousElements());
                return pc + 5;
            }
            case Op.NewSpread:
            {
                var fv = regs[bp + code[pc + 2]];
                var arr = ((JsArray)regs[bp + code[pc + 3]].Ref!);
                int dst = bp + code[pc + 1];
                f.Pc = pc + 4;
                regs[dst] = Construct(fv, arr.ContiguousElements());
                return pc + 4;
            }
            case Op.SuperCallSpread:
            {
                int dst = bp + code[pc + 1];
                var arr = ((JsArray)regs[bp + code[pc + 2]].Ref!);
                f.Pc = pc + 3;
                regs[dst] = SuperConstruct(f, arr.ContiguousElements());
                return pc + 3;
            }
            // ------------------------------------------------------------ classes and private names
            case Op.ClassCreate:
                ClassCreate(f, regs, bp, code[pc + 1], code[pc + 2], code[pc + 3], code[pc + 4], code[pc + 5]);
                return pc + 6;
            case Op.SetHomeObject:
                ((JsClosure)regs[bp + code[pc + 1]].Ref!).HomeObject = regs[bp + code[pc + 2]].AsObjectUnchecked;
                return pc + 3;
            case Op.Intrinsic:
            {
                // decorators run arbitrary code: pass a copy of the operands and re-read the register file afterwards
                var result = Intrinsic((ClassIntrinsic)code[pc + 2], new ReadOnlySpan<JsValue>(regs, bp + code[pc + 3], code[pc + 4]).ToArray());
                f.Regs[bp + code[pc + 1]] = result;
            }
                return pc + 5;
            case Op.SetFieldInit:
                ((JsClosure)regs[bp + code[pc + 1]].Ref!).FieldInit = ((JsClosure)regs[bp + code[pc + 2]].Ref!);
                return pc + 3;
            case Op.NewPrivateName:
                regs[bp + code[pc + 1]] = new JsValue(JsTag.Empty, 0, new JsPrivateName(k[code[pc + 2]].AsStringUnchecked, code[pc + 3]));
                return pc + 4;
            case Op.PrivateMethod:
            {
                var name = (JsPrivateName)regs[bp + code[pc + 1]].Ref!;
                var fn = regs[bp + code[pc + 2]].AsObjectUnchecked;
                switch (code[pc + 3])
                {
                    case 0: name.Method = fn; break;
                    case 1: name.Getter = fn; break;
                    default: name.Setter = fn; break;
                }
                return pc + 4;
            }
            case Op.PrivateIn:
            {
                var ov = regs[bp + code[pc + 2]];
                if (ov.IsNotObject) throw JsErrors.TypeError(realm, "Cannot use 'in' operator to search for a private field in a non-object");
                var name = (JsPrivateName)regs[bp + code[pc + 3]].Ref!;
                regs[bp + code[pc + 1]] = JsValue.FromBool(ov.AsObjectUnchecked.FindPrivate(name) >= 0);
                return pc + 4;
            }

            // ------------------------------------------------------------ iteration
            case Op.GetIterator:
                regs[bp + code[pc + 1]] = new JsValue(JsTag.Empty, 0, IteratorRecord.Get(realm, regs[bp + code[pc + 2]]));
                return pc + 3;
            case Op.GetIteratorObj:
            {
                var src = regs[bp + code[pc + 2]];
                var method = Operations.GetV(realm, src, JsSymbol.Iterator);
                if (!method.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(src)} is not iterable");
                // %IteratorPrototype%[@@iterator] returns its this value and does nothing else
                var it = ReferenceEquals(method.Ref, realm.IteratorProtoIterator) ? src : Call(method, src, ReadOnlySpan<JsValue>.Empty);
                if (it.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.iterator method is not an object");
                regs[bp + code[pc + 1]] = it;
                return pc + 3;
            }
            case Op.DelegateNext:
            {
                int kind = DelegateNext(regs[bp + code[pc + 2]], regs[bp + code[pc + 3]], out var res);
                regs[bp + code[pc + 1]] = res;
                regs[bp + code[pc + 4]] = new JsValue(kind);
                return pc + 5;
            }
            case Op.IterNextOrUndef:
            {
                var rec = (IteratorRecord)regs[bp + code[pc + 2]].Ref!;
                regs[bp + code[pc + 1]] = rec.Step(out var v) ? v : default;
                return pc + 3;
            }
            case Op.IterRest:
            {
                var rec = (IteratorRecord)regs[bp + code[pc + 2]].Ref!;
                var arr = new JsArray(realm);
                while (rec.Step(out var v)) { arr.Push(v); Tick(); }
                regs[bp + code[pc + 1]] = JsValue.FromObject(arr);
                return pc + 3;
            }
            case Op.PushEnvNamed:
            case Op.PushWith:
            case Op.LdName:
            case Op.LdNameCall:
            case Op.StName:
            case Op.DelName:
            case Op.EvalDeclare:
            case Op.EvalInitVar:
            case Op.CallEval:
            case Op.CallEvalSpread:
            case Op.ResolveRef:
            case Op.LdRef:
            case Op.StRef:
                return DynamicOp(f, pc);
            case Op.GetAsyncIterator:
                regs[bp + code[pc + 1]] = AsyncGenerators.GetAsyncIterator(realm, regs[bp + code[pc + 2]]);
                return pc + 3;
            case Op.ThrowIfNotObject:
                if (regs[bp + code[pc + 1]].IsNotObject)
                    throw JsErrors.TypeError(realm, k[code[pc + 2]].AsStringUnchecked);
                return pc + 3;
            case Op.IterClose:
                ((IteratorRecord)regs[bp + code[pc + 1]].Ref!).Close();
                return pc + 2;
            case Op.IterCloseQuiet:
                ((IteratorRecord)regs[bp + code[pc + 1]].Ref!).CloseQuiet();
                return pc + 2;
            case Op.ForInPrepare:
            {
                var src = regs[bp + code[pc + 2]];
                var it = new ForInIterator(src.IsNullOrUndefined ? null : Operations.ToObject(realm, src));
                regs[bp + code[pc + 1]] = new JsValue(JsTag.Empty, 0, it);
                return pc + 3;
            }

            // ------------------------------------------------------------ misc
            case Op.GetTemplateObject:
            {
                ref var ic = ref f.Feedback.Ics[code[pc + 3]];
                if (ic.Cell is not JsArray tobj)
                {
                    tobj = CreateTemplateObject((TemplateSite)f.T.Objects[code[pc + 2]]);
                    ic.Cell = tobj;
                }
                regs[bp + code[pc + 1]] = JsValue.FromObject(tobj);
                return pc + 4;
            }
            case Op.NewRegExp:
                regs[bp + code[pc + 1]] = JsValue.FromObject(RegExpBuiltins.CreateAtSite(realm, k[code[pc + 2]].AsStringUnchecked, k[code[pc + 3]].AsStringUnchecked, ref f.Feedback.Ics[code[pc + 4]].Cell));
                return pc + 5;
            case Op.CreateArguments:
                regs[bp + code[pc + 1]] = JsValue.FromObject(CreateArgumentsObject(f));
                return pc + 2;
            case Op.CreateMappedArguments:
                regs[bp + code[pc + 1]] = JsValue.FromObject(CreateMappedArgumentsObject(f, (int[])f.T.Objects[code[pc + 2]]));
                return pc + 3;
            case Op.CreateRestArray:
            {
                int start = code[pc + 2];
                var args = f.Args ?? Array.Empty<JsValue>();
                // a function without an arguments object kept only these arguments, in an array nothing else will read
                if ((f.T.Flags & FunctionFlags.RestOnlyArgs) != 0) f.Args = null;
                else args = args.Length <= start ? Array.Empty<JsValue>() : args.AsSpan(start).ToArray();
                regs[bp + code[pc + 1]] = JsValue.FromObject(JsArray.TakingItems(realm, args));
                return pc + 3;
            }
            case Op.ArgCount:
                regs[bp + code[pc + 1]] = new JsValue(f.Argc);
                return pc + 2;
            case Op.ArgLength:
            {
                ref var holder = ref regs[bp + code[pc + 2]];
                regs[bp + code[pc + 1]] = holder.IsObject ? holder.AsObjectUnchecked.Get("length", holder) : new JsValue(f.Args!.Length);
                return pc + 3;
            }
            case Op.ArgElem:
                regs[bp + code[pc + 1]] = ArgumentsElement(f, regs[bp + code[pc + 2]], ref regs[bp + code[pc + 3]]);
                return pc + 4;
            case Op.LdImport:
            {
                var v = Modules.ReadImport(realm, f.Callee.Module!, code[pc + 2]);
                if (v.IsEmpty) throw JsErrors.ReferenceError(realm, $"Cannot access '{k[code[pc + 3]]}' before initialization");
                regs[bp + code[pc + 1]] = v;
                return pc + 4;
            }
            case Op.ImportMeta:
                regs[bp + code[pc + 1]] = JsValue.FromObject(Modules.ImportMeta(realm, f.Callee.Module!));
                return pc + 2;
            case Op.NewDisposeStack:
            case Op.AddDisposable:
            case Op.DisposeSync:
            case Op.DisposeNext:
            case Op.DisposeMerge:
                return DisposeOp(f, pc);
            case Op.ParseJsonConst:
                regs[bp + code[pc + 1]] = JsonBuiltins.Parse(realm, k[code[pc + 2]].AsStringUnchecked, 100_000);
                return pc + 3;
            case Op.DynamicImport:
                regs[bp + code[pc + 1]] = Modules.DynamicImport(realm, f.Callee.Module, regs[bp + code[pc + 2]],
                    code[pc + 3] < 0 ? JsValue.Undefined : regs[bp + code[pc + 3]], (ModulePhase)code[pc + 4]);
                return pc + 5;
            default:
                throw new InvalidOperationException($"Jaunt internal error: unknown opcode {code[pc]}.");
        }
    }

    /// <summary>
    /// <see cref="Op.StrConcat"/>: converts each part in order (parts already converted by <see cref="Op.ToStrPart"/> stay as
    /// they are), throws the pairwise additions' RangeError at the part that makes the result too long, then concatenates.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StrConcat(Frame f, int dst, int start, int count, bool primitiveFirst)
    {
        var realm = Realm;
        int bp = f.Base;
        long total = 0;
        for (int i = bp + start, end = i + count; i < end; i++)
        {
            var v = f.Regs[i];
            if (!v.IsString && !(v.IsNumber && v.Num == (int)v.Num))
            {
                v = Operations.ToStringPart(realm, v, primitiveFirst);   // may run user code
                f.Regs[i] = v;
            }
            total += Operations.PartLength(v);
            if (total > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
        }
        var regs = f.Regs;
        regs[bp + dst] = Operations.ConcatN(realm, regs, bp + start, count, (int)total);
    }
}
