using System;
using System.Runtime.CompilerServices;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

internal sealed partial class Vm
{
    private static readonly JsValue TypeofUndefined = new("undefined");
    private static readonly JsValue TypeofObject = new("object");
    private static readonly JsValue TypeofBoolean = new("boolean");
    private static readonly JsValue TypeofNumber = new("number");
    private static readonly JsValue TypeofString = new("string");
    private static readonly JsValue TypeofSymbol = new("symbol");
    private static readonly JsValue TypeofBigInt = new("bigint");
    private static readonly JsValue TypeofFunction = new("function");

    /// <summary>
    /// Executes <paramref name="entry"/> (already pushed) until it returns. JS-to-JS calls and returns are handled
    /// inside this loop without growing the CLR stack; JS exceptions are dispatched through handler tables.
    /// </summary>
    /// <remarks>
    /// Only frequent opcodes are implemented inline (with fast paths that cannot throw); everything else is
    /// delegated to <see cref="RareOp"/>, and slow paths are out-of-line calls. This keeps register pressure and
    /// the method's stack frame small. Invariant: the local <c>pc</c> is the start of the executing instruction
    /// until it completes; <c>f.Pc</c> is synchronized before any operation that can throw or call out.
    /// <para>
    /// The catch handler must not read any local of the loop: a local that is live into a handler is kept on the
    /// stack by the JIT (every instruction would then load and store <c>pc</c>, <c>f</c> and <c>regs</c> through
    /// memory). It parks the thrown value in a field and jumps to <c>Recover</c>, which rebuilds the locals from
    /// <see cref="Current"/> and the <c>f.Pc</c> the faulting instruction recorded. The entry frame is marked with
    /// <see cref="FrameFlags.Entry"/> for the same reason.
    /// </para>
    /// </remarks>
    private JsValue RunLoop(Frame entry)
    {
        Frame f = entry;
        f.Flags |= FrameFlags.Entry;
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        int pc = f.Pc;
        JsValue[] k = f.K;
        JsValue thrown;
        JsValue result;

        while (true)
        {
            try
            {
                while (true)
                {
                    switch ((Op)code[pc])
                    {
                        case Op.Mov: regs[bp + code[pc + 1]] = regs[bp + code[pc + 2]]; pc += 3; continue;
                        case Op.LoadUndef: regs[bp + code[pc + 1]] = default; pc += 2; continue;
                        case Op.LoadNull: regs[bp + code[pc + 1]] = JsValue.Null; pc += 2; continue;
                        case Op.LoadTrue: regs[bp + code[pc + 1]] = JsValue.True; pc += 2; continue;
                        case Op.LoadFalse: regs[bp + code[pc + 1]] = JsValue.False; pc += 2; continue;
                        case Op.LoadEmpty: regs[bp + code[pc + 1]] = JsValue.Empty; pc += 2; continue;
                        case Op.LoadInt: regs[bp + code[pc + 1]] = new JsValue((double)code[pc + 2]); pc += 3; continue;
                        case Op.LoadConst: regs[bp + code[pc + 1]] = k[code[pc + 2]]; pc += 3; continue;
                        case Op.LoadThis:
                        {
                            var t = f.This;
                            if (t.IsEmpty)
                            {
                                if (f.ThisBox != null && f.ThisBox.Value.IsNotEmpty) t = f.This = f.ThisBox.Value;
                                else { f.Pc = pc + 1; thrown = ThisTdzError(); goto Unwind; }
                            }
                            regs[bp + code[pc + 1]] = t;
                            pc += 2;
                            continue;
                        }
                        case Op.CheckTdz:
                            if (regs[bp + code[pc + 1]].IsEmpty) { f.Pc = pc + 1; thrown = TdzError(k[code[pc + 2]]); goto Unwind; }
                            pc += 3;
                            continue;

                        // ---------------------------------------------------------- environments
                        case Op.LdEnv:
                        {
                            var e = f.Env!;
                            for (int d = code[pc + 2]; d > 0; d--) e = e.Parent!;
                            regs[bp + code[pc + 1]] = e.Slots[code[pc + 3]];
                            pc += 4;
                            continue;
                        }
                        case Op.LdEnvChk:
                        {
                            var e = f.Env!;
                            for (int d = code[pc + 2]; d > 0; d--) e = e.Parent!;
                            var v = e.Slots[code[pc + 3]];
                            if (v.IsEmpty) { f.Pc = pc + 1; thrown = TdzError(k[code[pc + 4]]); goto Unwind; }
                            regs[bp + code[pc + 1]] = v;
                            pc += 5;
                            continue;
                        }
                        case Op.StEnv:
                        {
                            var e = f.Env!;
                            for (int d = code[pc + 1]; d > 0; d--) e = e.Parent!;
                            e.Slots[code[pc + 2]] = regs[bp + code[pc + 3]];
                            pc += 4;
                            continue;
                        }
                        case Op.StEnvChk:
                        {
                            var e = f.Env!;
                            for (int d = code[pc + 1]; d > 0; d--) e = e.Parent!;
                            if (e.Slots[code[pc + 2]].IsEmpty) { f.Pc = pc + 1; thrown = TdzError(k[code[pc + 4]]); goto Unwind; }
                            e.Slots[code[pc + 2]] = regs[bp + code[pc + 3]];
                            pc += 5;
                            continue;
                        }
                        case Op.PushEnv:
                        {
                            int imm = code[pc + 1];
                            f.Env = new Env(f.Env, imm & 0xFFFF, imm >> 16);
                            f.EnvDepth++;
                            pc += 2;
                            continue;
                        }
                        case Op.PopEnv: f.Env = f.Env!.Parent; f.EnvDepth--; pc++; continue;
                        case Op.CopyEnv: f.Env = f.Env!.CopyForIteration(); pc++; continue;

                        // ---------------------------------------------------------- globals
                        case Op.GetGlobal:
                        {
                            ref var ic = ref f.Feedback.Ics[code[pc + 3]];
                            if (ic.Kind == IcKind.GlobalProp && ic.Epoch == Realm.GlobalVersion)
                            {
                                var v = Realm.GlobalObject.Dict!.Values[ic.Slot];
                                if (v.IsNotEmpty) { regs[bp + code[pc + 1]] = v; pc += 4; continue; }
                            }
                            else if (ic.Kind == IcKind.GlobalCell)
                            {
                                var v = Unsafe.As<GlobalCell>(ic.Cell!).Value;
                                if (v.IsNotEmpty) { regs[bp + code[pc + 1]] = v; pc += 4; continue; }
                            }
                            f.Pc = pc + 1;
                            regs[bp + code[pc + 1]] = GetGlobalSlow(k[code[pc + 2]].AsStringUnchecked, ref ic, false);
                            pc += 4;
                            continue;
                        }
                        case Op.SetGlobal:
                        {
                            ref var ic = ref f.Feedback.Ics[code[pc + 3]];
                            if (ic.Kind == IcKind.GlobalProp && ic.Epoch == Realm.GlobalVersion)
                            {
                                Realm.GlobalObject.Dict!.Values[ic.Slot] = regs[bp + code[pc + 2]];
                                pc += 4;
                                continue;
                            }
                            f.Pc = pc + 1;
                            SetGlobalSlow(k[code[pc + 1]].AsStringUnchecked, regs[bp + code[pc + 2]], ref ic, f.T.IsStrict);
                            pc += 4;
                            continue;
                        }

                        // ---------------------------------------------------------- properties
                        case Op.GetProp:
                        {
                            var ov = regs[bp + code[pc + 2]];
                            ref var ic = ref f.Feedback.Ics[code[pc + 4]];
                            if (ov.IsObject)
                            {
                                var obj = Unsafe.As<JsObject>(ov.Ref!);
                                if (ReferenceEquals(obj.Shape, ic.Shape))
                                {
                                    if (ic.Kind == IcKind.OwnData)
                                    {
                                        var v = obj.Slots[ic.Slot];
                                        if (v.IsNotEmpty) { regs[bp + code[pc + 1]] = v; pc += 5; continue; }
                                    }
                                    else if (ic.Kind == IcKind.ProtoData && ic.Epoch == ProtoEpoch)
                                    {
                                        var v = ic.Holder!.Slots[ic.Slot];
                                        if (v.IsNotEmpty) { regs[bp + code[pc + 1]] = v; pc += 5; continue; }
                                    }
                                    else if (ic.Kind == IcKind.ProtoGetter && ic.Epoch == ProtoEpoch)
                                    {
                                        // accessor on the prototype chain (typed array length, Map size, class getters)
                                        // an accessor slot carries the Accessor as an Empty-tagged reference
                                        if (ic.Holder!.Slots[ic.Slot].Ref is Accessor acc)
                                        {
                                            f.Pc = pc + 1;
                                            var gv = CallCachedGetter(acc, ov);
                                            regs = f.Regs;
                                            regs[bp + code[pc + 1]] = gv;
                                            pc += 5;
                                            continue;
                                        }
                                    }
                                    else if (ic.Kind == IcKind.OwnGetter)
                                    {
                                        if (obj.Slots[ic.Slot].Ref is Accessor acc)
                                        {
                                            f.Pc = pc + 1;
                                            var gv = CallCachedGetter(acc, ov);
                                            regs = f.Regs;
                                            regs[bp + code[pc + 1]] = gv;
                                            pc += 5;
                                            continue;
                                        }
                                    }
                                    else if (ic.Kind == IcKind.NotFound && ic.Epoch == ProtoEpoch)
                                    {
                                        regs[bp + code[pc + 1]] = default;
                                        pc += 5;
                                        continue;
                                    }
                                }
                                else if (ic.Kind == IcKind.ArrayLength && obj is JsArray la)
                                {
                                    regs[bp + code[pc + 1]] = new JsValue((double)la.Length);
                                    pc += 5;
                                    continue;
                                }
                            }
                            else if (ov.IsString)
                            {
                                if (ic.Kind == IcKind.StringLength)
                                {
                                    regs[bp + code[pc + 1]] = new JsValue((double)ov.StringLength);
                                    pc += 5;
                                    continue;
                                }
                                if (ic.Kind == IcKind.StringProtoData && ic.Epoch == ProtoEpoch && ReferenceEquals(ic.Holder!.Shape, ic.Shape))
                                {
                                    var v = ic.Holder.Slots[ic.Slot];
                                    if (v.IsNotEmpty) { regs[bp + code[pc + 1]] = v; pc += 5; continue; }
                                }
                            }
                            f.Pc = pc + 1;
                            if (ov.IsNullOrUndefined) { thrown = NullishReadError(ov, k[code[pc + 3]]); goto Unwind; }
                            regs[bp + code[pc + 1]] = GetPropSlow(ov, k[code[pc + 3]], ref ic);
                            pc += 5;
                            continue;
                        }
                        case Op.SetProp:
                        {
                            var ov = regs[bp + code[pc + 1]];
                            ref var ic = ref f.Feedback.Ics[code[pc + 4]];
                            if (ov.IsObject)
                            {
                                var obj = Unsafe.As<JsObject>(ov.Ref!);
                                if (ReferenceEquals(obj.Shape, ic.Shape))
                                {
                                    if (ic.Kind == IcKind.OwnData) { obj.Slots[ic.Slot] = regs[bp + code[pc + 3]]; pc += 5; continue; }
                                    if (ic.Kind == IcKind.AddTransition && ic.Epoch == ProtoEpoch && obj.Extensible)
                                    {
                                        int slot = ic.Slot;
                                        if (slot >= obj.Slots.Length) GrowSlots(obj, slot);
                                        obj.Slots[slot] = regs[bp + code[pc + 3]];
                                        obj.Shape = ic.NewShape!;
                                        Realm.ChargeProperty();
                                        pc += 5;
                                        continue;
                                    }
                                }
                            }
                            f.Pc = pc + 1;
                            if (ov.IsNullOrUndefined) { thrown = NullishSetError(ov, k[code[pc + 2]]); goto Unwind; }
                            SetPropSlow(ov, k[code[pc + 2]], regs[bp + code[pc + 3]], ref ic, f.T.IsStrict);
                            pc += 5;
                            continue;
                        }
                        case Op.GetElem:
                        {
                            var ov = regs[bp + code[pc + 2]];
                            var kv = regs[bp + code[pc + 3]];
                            if (ov.IsObject)
                            {
                                if (kv.IsNumber)
                                {
                                    if (ov.Ref is JsArray arr)
                                    {
                                        double dk = kv.Num;
                                        uint idx = (uint)dk;
                                        if (idx == dk && arr.TryGetDense(idx, out var ev)) { regs[bp + code[pc + 1]] = ev; pc += 5; continue; }
                                    }
                                    else if (ov.Ref is JsTypedArray ta && ta.TryGetElementFast(kv.Num, out var tv)) { regs[bp + code[pc + 1]] = tv; pc += 5; continue; }
                                }
                                // ordinary object with a computed key: cached (shape, key) → slot
                                ref var ic = ref f.Feedback.Ics[code[pc + 4]];
                                if (ic.Kind == IcKind.KeyedOwnData)
                                {
                                    var obj = Unsafe.As<JsObject>(ov.Ref!);
                                    if (ReferenceEquals(obj.Shape, ic.Shape) && (kv.IsNumber ? ic.Cell == null && kv.Num == ic.Epoch : ReferenceEquals(ic.Cell, kv.Ref)))
                                    {
                                        var v = obj.Slots[ic.Slot];
                                        if (v.IsNotEmpty) { regs[bp + code[pc + 1]] = v; pc += 5; continue; }
                                    }
                                }
                                f.Pc = pc + 1;
                                regs[bp + code[pc + 1]] = GetElemKeyed(ov, kv, ref ic);
                                pc += 5;
                                continue;
                            }
                            f.Pc = pc + 1;
                            if (ov.IsNullOrUndefined) { thrown = NullishReadError(ov, kv); goto Unwind; }
                            regs[bp + code[pc + 1]] = GetElemSlow(ov, kv);
                            pc += 5;
                            continue;
                        }
                        case Op.SetElem:
                        {
                            var ov = regs[bp + code[pc + 1]];
                            var kv = regs[bp + code[pc + 2]];
                            if (ov.IsObject)
                            {
                                if (kv.IsNumber && ov.Ref is JsArray arr && arr.Dense)
                                {
                                    double dk = kv.Num;
                                    uint idx = (uint)dk;
                                    if (idx == dk && idx < arr.Length && idx < (uint)arr.Items.Length && arr.Items[idx].IsNotEmpty)
                                    {
                                        arr.Items[idx] = regs[bp + code[pc + 3]];
                                        pc += 5;
                                        continue;
                                    }
                                    // append (a[a.length] = v): no setters/indices can intercept on a clean prototype chain
                                    if (idx == dk && idx == arr.Length && idx < uint.MaxValue - 1 && arr.LengthWritable && arr.Extensible
                                        && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
                                    {
                                        f.Pc = pc + 1;
                                        arr.Push(regs[bp + code[pc + 3]]);
                                        pc += 5;
                                        continue;
                                    }
                                }
                                else if (kv.IsNumber && ov.Ref is JsTypedArray ta && ta.TrySetElementFast(kv.Num, regs[bp + code[pc + 3]])) { pc += 5; continue; }
                                ref var ic = ref f.Feedback.Ics[code[pc + 4]];
                                if (ic.Kind == IcKind.KeyedOwnData)
                                {
                                    var obj = Unsafe.As<JsObject>(ov.Ref!);
                                    if (ReferenceEquals(obj.Shape, ic.Shape) && (kv.IsNumber ? ic.Cell == null && kv.Num == ic.Epoch : ReferenceEquals(ic.Cell, kv.Ref)))
                                    {
                                        obj.Slots[ic.Slot] = regs[bp + code[pc + 3]];
                                        pc += 5;
                                        continue;
                                    }
                                }
                                f.Pc = pc + 1;
                                SetElemKeyed(ov, kv, regs[bp + code[pc + 3]], f.T.IsStrict, ref ic);
                                pc += 5;
                                continue;
                            }
                            f.Pc = pc + 1;
                            if (ov.IsNullOrUndefined && (kv.IsString || kv.IsNumber)) { thrown = NullishSetError(ov, kv); goto Unwind; }
                            SetElemSlow(ov, kv, regs[bp + code[pc + 3]], f.T.IsStrict);
                            pc += 5;
                            continue;
                        }
                        case Op.DefinePropK:
                        {
                            f.Pc = pc + 1;
                            var obj = regs[bp + code[pc + 1]].AsObjectUnchecked;
                            obj.CreateDataPropertyOrThrow(new PropertyKey(k[code[pc + 2]].AsStringUnchecked), regs[bp + code[pc + 3]]);
                            pc += 4;
                            continue;
                        }
                        case Op.NewObject:
                            regs[bp + code[pc + 1]] = JsValue.FromObject(new JsObject(Realm, Shape.SharedObjectRoot, 0));
                            pc += 2;
                            continue;
                        case Op.NewArray:
                            f.Pc = pc + 1;
                            regs[bp + code[pc + 1]] = JsValue.FromObject(new JsArray(Realm, code[pc + 2]));
                            pc += 3;
                            continue;
                        case Op.ArrayPush:
                            f.Pc = pc + 1;
                            Unsafe.As<JsArray>(regs[bp + code[pc + 1]].Ref!).Push(regs[bp + code[pc + 2]]);
                            pc += 3;
                            continue;

                        // ---------------------------------------------------------- arithmetic (number fast paths)
                        case Op.Add:
                        {
                            ref var a = ref regs[bp + code[pc + 2]];
                            ref var b = ref regs[bp + code[pc + 3]];
                            if (a.IsNumber && b.IsNumber) regs[bp + code[pc + 1]] = new JsValue(a.Num + b.Num);
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = AddSlow(a, b); }
                            pc += 4;
                            continue;
                        }
                        case Op.Sub:
                        {
                            ref var a = ref regs[bp + code[pc + 2]];
                            ref var b = ref regs[bp + code[pc + 3]];
                            if (a.IsNumber && b.IsNumber) regs[bp + code[pc + 1]] = new JsValue(a.Num - b.Num);
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = ArithSlow(Op.Sub, a, b); }
                            pc += 4;
                            continue;
                        }
                        case Op.Mul:
                        {
                            ref var a = ref regs[bp + code[pc + 2]];
                            ref var b = ref regs[bp + code[pc + 3]];
                            if (a.IsNumber && b.IsNumber) regs[bp + code[pc + 1]] = new JsValue(a.Num * b.Num);
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = ArithSlow(Op.Mul, a, b); }
                            pc += 4;
                            continue;
                        }
                        case Op.Div:
                        {
                            ref var a = ref regs[bp + code[pc + 2]];
                            ref var b = ref regs[bp + code[pc + 3]];
                            if (a.IsNumber && b.IsNumber) regs[bp + code[pc + 1]] = new JsValue(a.Num / b.Num);
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = ArithSlow(Op.Div, a, b); }
                            pc += 4;
                            continue;
                        }
                        case Op.Mod:
                        {
                            ref var a = ref regs[bp + code[pc + 2]];
                            ref var b = ref regs[bp + code[pc + 3]];
                            if (a.IsNumber && b.IsNumber) regs[bp + code[pc + 1]] = new JsValue(JsMod(a.Num, b.Num));
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = ArithSlow(Op.Mod, a, b); }
                            pc += 4;
                            continue;
                        }
                        case Op.BitAnd:
                        case Op.BitOr:
                        case Op.BitXor:
                        case Op.Shl:
                        case Op.Shr:
                        case Op.UShr:
                        {
                            ref var a = ref regs[bp + code[pc + 2]];
                            ref var b = ref regs[bp + code[pc + 3]];
                            int x, y;
                            if (a.IsNumber && b.IsNumber) { x = Operations.DoubleToInt32(a.Num); y = Operations.DoubleToInt32(b.Num); }
                            else
                            {
                                f.Pc = pc + 1;
                                regs[bp + code[pc + 1]] = ArithSlow((Op)code[pc], a, b);
                                pc += 4;
                                continue;
                            }
                            regs[bp + code[pc + 1]] = (Op)code[pc] switch
                            {
                                Op.BitAnd => new JsValue(x & y),
                                Op.BitOr => new JsValue(x | y),
                                Op.BitXor => new JsValue(x ^ y),
                                Op.Shl => new JsValue(x << (y & 31)),
                                Op.Shr => new JsValue(x >> (y & 31)),
                                _ => new JsValue((double)((uint)x >> (y & 31))),
                            };
                            pc += 4;
                            continue;
                        }
                        case Op.StrictEq:
                        case Op.StrictNe:
                        {
                            ref var a = ref regs[bp + code[pc + 2]];
                            ref var b = ref regs[bp + code[pc + 3]];
                            bool eq = a.IsNumber && b.IsNumber ? a.Num == b.Num : StrictEqualsSlow(a, b);
                            regs[bp + code[pc + 1]] = JsValue.FromBool(eq == ((Op)code[pc] == Op.StrictEq));
                            pc += 4;
                            continue;
                        }
                        case Op.Lt:
                        case Op.Le:
                        case Op.Gt:
                        case Op.Ge:
                        {
                            ref var a = ref regs[bp + code[pc + 2]];
                            ref var b = ref regs[bp + code[pc + 3]];
                            var op = (Op)code[pc];
                            bool r;
                            if (a.IsNumber && b.IsNumber)
                            {
                                double x = a.Num, y = b.Num;
                                r = op == Op.Lt ? x < y : op == Op.Le ? x <= y : op == Op.Gt ? x > y : x >= y;
                            }
                            else { f.Pc = pc + 1; r = CompareSlow(op, a, b); }
                            regs[bp + code[pc + 1]] = JsValue.FromBool(r);
                            pc += 4;
                            continue;
                        }
                        case Op.Neg:
                        {
                            var a = regs[bp + code[pc + 2]];
                            if (a.IsNumber) regs[bp + code[pc + 1]] = new JsValue(-a.Num);
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = BigIntOps.Negate(Realm, a); }
                            pc += 3;
                            continue;
                        }
                        case Op.Plus:
                        case Op.ToNumeric:
                        {
                            var a = regs[bp + code[pc + 2]];
                            if (a.IsNumber) regs[bp + code[pc + 1]] = a;
                            else
                            {
                                f.Pc = pc + 1;
                                regs[bp + code[pc + 1]] = (Op)code[pc] == Op.ToNumeric ? Operations.ToNumericValue(Realm, a) : new JsValue(ToNumberSlow(a));
                            }
                            pc += 3;
                            continue;
                        }
                        case Op.Inc:
                        {
                            var a = regs[bp + code[pc + 2]];
                            if (a.IsNumber) regs[bp + code[pc + 1]] = new JsValue(a.Num + 1);
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = BigIntOps.Increment(Realm, a, 1); }
                            pc += 3;
                            continue;
                        }
                        case Op.Dec:
                        {
                            var a = regs[bp + code[pc + 2]];
                            if (a.IsNumber) regs[bp + code[pc + 1]] = new JsValue(a.Num - 1);
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = BigIntOps.Increment(Realm, a, -1); }
                            pc += 3;
                            continue;
                        }
                        case Op.Not:
                        {
                            ulong bits = regs[bp + code[pc + 2]].Bits;
                            regs[bp + code[pc + 1]] = bits == JsValue.TrueBits ? JsValue.False
                                : bits == JsValue.FalseBits ? JsValue.True
                                : JsValue.FromBool(!ToBooleanSlow(regs[bp + code[pc + 2]]));
                            pc += 3;
                            continue;
                        }
                        case Op.Typeof:
                            regs[bp + code[pc + 1]] = TypeofValue(regs[bp + code[pc + 2]]);
                            pc += 3;
                            continue;
                        case Op.ToPropertyKey:
                        {
                            // strings, symbols and array-index numbers are already keys (see Operations.ToPropertyKeyValue)
                            var a = regs[bp + code[pc + 2]];
                            if (a.IsString || a.IsSymbol || (a.IsNumber && Operations.IsArrayIndexNumber(a.Num))) regs[bp + code[pc + 1]] = a;
                            else { f.Pc = pc + 1; regs[bp + code[pc + 1]] = Operations.ToPropertyKeyValue(Realm, a); }
                            pc += 3;
                            continue;
                        }

                        // ---------------------------------------------------------- fused compare-and-branch
                        case Op.LtJmpFalse:
                        case Op.LeJmpFalse:
                        case Op.GtJmpFalse:
                        case Op.GeJmpFalse:
                        {
                            ref var a = ref regs[bp + code[pc + 1]];
                            ref var b = ref regs[bp + code[pc + 2]];
                            var op = (Op)code[pc];
                            bool r;
                            if (a.IsNumber && b.IsNumber)
                            {
                                double x = a.Num, y = b.Num;
                                r = op == Op.LtJmpFalse ? x < y : op == Op.LeJmpFalse ? x <= y : op == Op.GtJmpFalse ? x > y : x >= y;
                            }
                            else
                            {
                                f.Pc = pc + 1;
                                r = CompareSlow(op == Op.LtJmpFalse ? Op.Lt : op == Op.LeJmpFalse ? Op.Le : op == Op.GtJmpFalse ? Op.Gt : Op.Ge, a, b);
                            }
                            if (r) { pc += 4; continue; }
                            int target = code[pc + 3];
                            if (target <= pc && --_ticks <= 0) { f.Pc = pc + 1; Checkpoint(); }
                            pc = target;
                            continue;
                        }
                        case Op.StrictEqJmpFalse:
                        case Op.StrictNeJmpFalse:
                        {
                            ref var a = ref regs[bp + code[pc + 1]];
                            ref var b = ref regs[bp + code[pc + 2]];
                            bool eq = a.IsNumber && b.IsNumber ? a.Num == b.Num : StrictEqualsSlow(a, b);
                            if (eq == ((Op)code[pc] == Op.StrictEqJmpFalse)) { pc += 4; continue; }
                            int target = code[pc + 3];
                            if (target <= pc && --_ticks <= 0) { f.Pc = pc + 1; Checkpoint(); }
                            pc = target;
                            continue;
                        }

                        // ---------------------------------------------------------- jumps
                        case Op.Jmp:
                        {
                            int target = code[pc + 1];
                            if (target <= pc && --_ticks <= 0) { f.Pc = pc + 1; Checkpoint(); }
                            pc = target;
                            continue;
                        }
                        case Op.JmpTrue:
                        case Op.JmpFalse:
                        {
                            ulong bits = regs[bp + code[pc + 1]].Bits;
                            bool truthy = bits == JsValue.TrueBits || (bits != JsValue.FalseBits && ToBooleanSlow(regs[bp + code[pc + 1]]));
                            if (truthy == ((Op)code[pc] == Op.JmpTrue))
                            {
                                int target = code[pc + 2];
                                if (target <= pc && --_ticks <= 0) { f.Pc = pc + 1; Checkpoint(); }
                                pc = target;
                            }
                            else pc += 3;
                            continue;
                        }
                        case Op.JmpNullish:
                            pc = regs[bp + code[pc + 1]].IsNullOrUndefined ? code[pc + 2] : pc + 3;
                            continue;
                        case Op.JmpNotNullish:
                            pc = regs[bp + code[pc + 1]].IsNotNullish ? code[pc + 2] : pc + 3;
                            continue;
                        case Op.JmpUndef:
                            pc = regs[bp + code[pc + 1]].IsUndefined ? code[pc + 2] : pc + 3;
                            continue;
                        case Op.JmpNotUndef:
                            pc = regs[bp + code[pc + 1]].IsNotUndefined ? code[pc + 2] : pc + 3;
                            continue;
                        case Op.SwitchInt:
                        {
                            int v = (int)regs[bp + code[pc + 1]].Num;
                            int n = code[pc + 2];
                            pc = (uint)v < (uint)n ? code[pc + 3 + v] : pc + 3 + n;
                            continue;
                        }

                        // ---------------------------------------------------------- returns and throws
                        case Op.Ret:
                            result = regs[bp + code[pc + 1]];
                            goto Return;
                        case Op.RetUndef:
                            result = default;
                            goto Return;
                        case Op.Throw:
                            f.Pc = pc + 1;
                            thrown = regs[bp + code[pc + 1]];
                            goto Unwind;

                        // ---------------------------------------------------------- calls
                        case Op.TailCall:
                        case Op.TailCallMethod:
                        case Op.TailCallSpread:
                        case Op.TailCallEval:
                        {
                            // proper tail call: the callee replaces this frame (constant stack depth); otherwise an
                            // ordinary call whose result the following Ret returns
                            var top = (Op)code[pc];
                            int len = top switch { Op.TailCall => 6, Op.TailCallMethod => 7, Op.TailCallSpread => 5, _ => 7 };
                            int dst = bp + code[pc + 1];
                            JsValue fv, thisV;
                            ReadOnlySpan<JsValue> targs;
                            f.Pc = pc + 1;
                            if (top == Op.TailCallMethod)
                            {
                                thisV = regs[bp + code[pc + 2]];
                                fv = GetPropSlow(thisV, k[code[pc + 3]], ref f.Feedback.Ics[code[pc + 4]]);
                                if (fv.IsNotObject || fv.Ref is not JsFunctionObject) throw NotAFunction(thisV, k[code[pc + 3]]);
                                targs = new ReadOnlySpan<JsValue>(regs, bp + code[pc + 5], code[pc + 6]);
                            }
                            else
                            {
                                fv = regs[bp + code[pc + 2]];
                                int tr = code[pc + 3];
                                thisV = tr < 0 ? default : regs[bp + tr];
                                if (top == Op.TailCallSpread)
                                {
                                    var sarr = (JsArray)regs[bp + code[pc + 4]].Ref!;
                                    targs = new ReadOnlySpan<JsValue>(sarr.Items, 0, (int)sarr.Length);
                                }
                                else targs = new ReadOnlySpan<JsValue>(regs, bp + code[pc + 4], code[pc + 5]);
                                if (top == Op.TailCallEval && fv.IsObject && ReferenceEquals(fv.Ref, Realm.EvalFunction))
                                {
                                    // a direct eval is never a tail call
                                    int flags = code[pc + 6];
                                    var ev = targs.Length == 0 ? JsValue.Undefined : PerformEval(targs[0], f, (flags & 1) != 0, (flags & 2) != 0, flags);
                                    regs = f.Regs;
                                    regs[bp + code[pc + 1]] = ev;
                                    pc += len;
                                    continue;
                                }
                            }
                            if (fv.IsObject && fv.Ref is JsClosure tc && (tc.Template.Flags & InlineCallMask) == 0
                                && (f.Flags & (FrameFlags.Construct | FrameFlags.Coroutine | FrameFlags.OwnsRegs)) == 0)
                            {
                                if (--_ticks <= 0) Checkpoint();
                                // the arguments live in the registers being released: stage them in a reusable buffer
                                // (no user code runs between the copy and PushFrame)
                                int nargs = targs.Length;
                                if (_tailArgs.Length < nargs) _tailArgs = new JsValue[Math.Max(nargs, _tailArgs.Length * 2)];
                                targs.CopyTo(_tailArgs);
                                bool wasEntry = (f.Flags & FrameFlags.Entry) != 0;
                                int retIndex = f.RetIndex;
                                PopFrame(f);
                                var nf = PushFrame(tc, thisV, new ReadOnlySpan<JsValue>(_tailArgs, 0, nargs), default, FrameFlags.None);
                                Array.Clear(_tailArgs, 0, nargs);
                                nf.RetIndex = retIndex;
                                if (wasEntry) nf.Flags |= FrameFlags.Entry;
                                f = nf;
                                code = f.Code; regs = f.Regs; bp = f.Base; pc = 0; k = f.K;
                                continue;
                            }
                            var tres = CallValue(fv, thisV, targs);
                            regs = f.Regs;
                            regs[dst] = tres;
                            pc += len;
                            continue;
                        }
                        case Op.Call:
                        {
                            var fv = regs[bp + code[pc + 2]];
                            int tr = code[pc + 3];
                            int argStart = bp + code[pc + 4];
                            int argc = code[pc + 5];
                            int dst = bp + code[pc + 1];
                            f.Pc = pc + 6;
                            if (fv.IsObject && fv.Ref is JsClosure c && (c.Template.Flags & InlineCallMask) == 0)
                            {
                                if (--_ticks <= 0) Checkpoint();
                                var nf = PushCallFrame(c, regs, tr < 0 ? -1 : bp + tr, argStart, argc);
                                nf.RetIndex = dst;
                                f = nf;
                                code = f.Code; regs = f.Regs; bp = f.Base; pc = 0; k = f.K;
                                continue;
                            }
                            if (fv.IsNotObject || fv.Ref is not JsFunctionObject fo) { thrown = NotCallableError(fv); goto Unwind; }
                            {
                                // a builtin is called directly so an error it raises (Vm.Raise) unwinds here without a CLR throw
                                var r = fo is JsBuiltin b
                                    ? b.Fn(new NativeArgs(b.Realm, tr < 0 ? default : regs[bp + tr], new ReadOnlySpan<JsValue>(regs, argStart, argc), JsValue.Undefined, b))
                                    : fo.Call(tr < 0 ? default : regs[bp + tr], new ReadOnlySpan<JsValue>(regs, argStart, argc));
                                if (r.IsEmpty) { f.Pc = pc + 1; thrown = TakePending(); goto Unwind; }
                                regs[dst] = r;
                            }
                            pc += 6;
                            continue;
                        }
                        case Op.CallMethod:
                        {
                            var ov = regs[bp + code[pc + 2]];
                            ref var ic = ref f.Feedback.Ics[code[pc + 4]];
                            JsValue fv = default;
                            bool hit = false;
                            if (ov.IsObject)
                            {
                                var obj = Unsafe.As<JsObject>(ov.Ref!);
                                if (ReferenceEquals(obj.Shape, ic.Shape))
                                {
                                    if (ic.Kind == IcKind.OwnData) { fv = obj.Slots[ic.Slot]; hit = fv.IsNotEmpty; }
                                    else if (ic.Kind == IcKind.ProtoData && ic.Epoch == ProtoEpoch) { fv = ic.Holder!.Slots[ic.Slot]; hit = fv.IsNotEmpty; }
                                }
                            }
                            else if (ov.IsString && ic.Kind == IcKind.StringProtoData && ic.Epoch == ProtoEpoch && ReferenceEquals(ic.Holder!.Shape, ic.Shape))
                            {
                                fv = ic.Holder.Slots[ic.Slot];
                                hit = fv.IsNotEmpty;
                            }
                            if (!hit)
                            {
                                f.Pc = pc + 1;
                                if (ov.IsNullOrUndefined) { thrown = NullishReadError(ov, k[code[pc + 3]]); goto Unwind; }
                                fv = GetPropSlow(ov, k[code[pc + 3]], ref ic);
                            }
                            f.Pc = pc + 7;
                            int argStart = bp + code[pc + 5];
                            int argc = code[pc + 6];
                            int dst = bp + code[pc + 1];
                            if (fv.IsObject && fv.Ref is JsClosure c && (c.Template.Flags & InlineCallMask) == 0)
                            {
                                if (--_ticks <= 0) Checkpoint();
                                var nf = PushCallFrame(c, regs, bp + code[pc + 2], argStart, argc);
                                nf.RetIndex = dst;
                                f = nf;
                                code = f.Code; regs = f.Regs; bp = f.Base; pc = 0; k = f.K;
                                continue;
                            }
                            if (fv.IsNotObject || fv.Ref is not JsFunctionObject)
                            {
                                f.Pc = pc + 1;
                                thrown = NotAFunctionError(ov, k[code[pc + 3]]);
                                goto Unwind;
                            }
                            // arr.push(v) on a plain array: append without building NativeArgs
                            if (argc == 1 && fv.Ref is JsBuiltin pb && ReferenceEquals(pb.Fn, ArrayBuiltins.PushFn) && ov.Ref is JsArray pa
                                && pa.Dense && pa.LengthWritable && pa.Extensible && pa.Length < uint.MaxValue - 1 && !JsArray.ProtoChainMayHaveIndices(pa.Proto))
                            {
                                pa.Push(regs[argStart]);
                                regs[dst] = new JsValue((double)pa.Length);
                                pc += 7;
                                continue;
                            }
                            {
                                var r = fv.Ref is JsBuiltin b
                                    ? b.Fn(new NativeArgs(b.Realm, ov, new ReadOnlySpan<JsValue>(regs, argStart, argc), JsValue.Undefined, b))
                                    : Unsafe.As<JsFunctionObject>(fv.Ref!).Call(ov, new ReadOnlySpan<JsValue>(regs, argStart, argc));
                                if (r.IsEmpty) { f.Pc = pc + 1; thrown = TakePending(); goto Unwind; }
                                regs[dst] = r;
                            }
                            pc += 7;
                            continue;
                        }
                        case Op.New:
                        {
                            var fv = regs[bp + code[pc + 2]];
                            int argStart = bp + code[pc + 3];
                            int argc = code[pc + 4];
                            int dst = bp + code[pc + 1];
                            f.Pc = pc + 5;
                            if (fv.IsObject && fv.Ref is JsClosure c && (c.Template.Flags & InlineNewMask) == 0 && !c.IsDerived && c.FieldInit == null && c.Template.IsConstructor)
                            {
                                if (--_ticks <= 0) Checkpoint();
                                var obj = new JsObject(Realm, ConstructPrototype(c));
                                var nf = PushNewFrame(c, regs, argStart, argc, obj, fv);
                                nf.RetIndex = dst;
                                f = nf;
                                code = f.Code; regs = f.Regs; bp = f.Base; pc = 0; k = f.K;
                                continue;
                            }
                            regs[dst] = Construct(fv, new ReadOnlySpan<JsValue>(regs, argStart, argc));
                            pc += 5;
                            continue;
                        }
                        case Op.Closure:
                        {
                            int fi = code[pc + 2];
                            regs[bp + code[pc + 1]] = JsValue.FromObject(CreateClosure(f.T.Functions[fi], f.Env, f.Feedback.Child(fi), f));
                            pc += 3;
                            continue;
                        }

                        // ---------------------------------------------------------- loops over iterators
                        case Op.IterNext:
                        {
                            f.Pc = pc + 1;
                            var rec = Unsafe.As<IteratorRecord>(regs[bp + code[pc + 2]].Ref!);
                            if (rec.Step(out var v)) { regs[bp + code[pc + 1]] = v; pc += 4; }
                            else pc = code[pc + 3];
                            continue;
                        }
                        case Op.ForInNext:
                        {
                            f.Pc = pc + 1;
                            var it = Unsafe.As<ForInIterator>(regs[bp + code[pc + 2]].Ref!);
                            if (it.Next(out var key)) { regs[bp + code[pc + 1]] = new JsValue(key); pc += 4; }
                            else pc = code[pc + 3];
                            continue;
                        }

                        // ---------------------------------------------------------- coroutines
                        case Op.Yield:
                        case Op.YieldRaw:
                        case Op.Await:
                        {
                            if ((f.Flags & FrameFlags.Entry) == 0) throw new InvalidOperationException("Jaunt internal error: suspension outside coroutine entry frame.");
                            var op = (Op)code[pc];
                            result = regs[bp + code[pc + 1]];
                            f.ResumeSentReg = code[pc + 2];
                            f.ResumeModeReg = code[pc + 3];
                            f.Pc = pc + 4;
                            f.Suspend = op == Op.Await ? SuspendKind.Await : op == Op.Yield ? SuspendKind.Yield : SuspendKind.YieldRaw;
                            LastSuspend = f.Suspend;
                            DetachFrame(f);
                            return result;
                        }
                        case Op.InitialYield:
                        {
                            f.ResumeSentReg = code[pc + 1];
                            f.ResumeModeReg = code[pc + 2];
                            f.Pc = pc + 3;
                            f.Suspend = SuspendKind.Initial;
                            LastSuspend = SuspendKind.Initial;
                            DetachFrame(f);
                            return default;
                        }

                        default:
                            f.Pc = pc + 1;
                            pc = RareOp(f, pc);
                            continue;
                    }
                }
            }
            catch (JsThrowException ex)
            {
                // no loop local is read here (see remarks); the faulting frame is Current and recorded its pc
                Pending = ex.Value;
                goto Recover;
            }

        Return:
            {
                var done = f;
                if ((done.Flags & FrameFlags.Construct) != 0 && !TryConstructResult(done, ref result, out var ctorError))
                {
                    // raised in the caller's context, after the callee frame is removed
                    if ((done.Flags & FrameFlags.Entry) != 0) { PopFrame(done); throw new JsThrowException(ctorError); }
                    PopFrame(done);
                    f = FrameAt(Sp - 1);
                    code = f.Code; regs = f.Regs; bp = f.Base; k = f.K;
                    thrown = ctorError;
                    goto Unwind;
                }
                if ((done.Flags & FrameFlags.Entry) != 0)
                {
                    if ((done.Flags & FrameFlags.Coroutine) != 0) { done.Suspend = SuspendKind.None; LastSuspend = SuspendKind.None; DetachFrame(done); }
                    else PopFrame(done);
                    return result;
                }
                int ret = done.RetIndex;
                PopFrame(done);
                f = FrameAt(Sp - 1);
                regs = f.Regs;
                regs[ret] = result;
                code = f.Code; bp = f.Base; pc = f.Pc; k = f.K;
                continue;
            }

        Recover:
            // a CLR exception left the loop: every frame above the faulting one has already been popped by the
            // nested loops that owned them, so the innermost frame is the one executing the instruction at f.Pc - 1
            f = FrameAt(Sp - 1);
            code = f.Code; regs = f.Regs; bp = f.Base; k = f.K;
            thrown = TakePending();

        Unwind:
            while (true)
            {
                int at = f.Pc - 1;
                var handlers = f.T.Handlers;
                int found = -1;
                for (int i = 0; i < handlers.Length; i++)
                {
                    if (at >= handlers[i].Start && at < handlers[i].End) { found = i; break; }
                }
                if (found >= 0)
                {
                    var h = handlers[found];
                    while (f.EnvDepth > h.EnvDepth) { f.Env = f.Env!.Parent; f.EnvDepth--; }
                    code = f.Code; regs = f.Regs; bp = f.Base; k = f.K;
                    regs[bp + h.ExceptionReg] = thrown;
                    pc = h.Target;
                    break;
                }
                var done = f;
                if ((done.Flags & FrameFlags.Entry) != 0)
                {
                    if ((done.Flags & FrameFlags.Coroutine) != 0) { done.Suspend = SuspendKind.None; LastSuspend = SuspendKind.None; DetachFrame(done); }
                    else PopFrame(done);
                    throw new JsThrowException(thrown);
                }
                PopFrame(done);
                f = FrameAt(Sp - 1);
            }
        }
    }

    private JsValue[] _tailArgs = new JsValue[8];

    /// <summary>
    /// Template flags that keep a call out of the in-loop frame push: coroutines run on heap frames, and a class
    /// constructor throws when called (JsClosure.IsClassConstructor mirrors the template flag; the template is
    /// read anyway, so the check costs no extra load).
    /// </summary>
    private const FunctionFlags InlineCallMask = FunctionFlags.Generator | FunctionFlags.Async | FunctionFlags.ClassConstructor;
    /// <summary>Template flags that keep <c>new</c> out of the in-loop frame push (class constructors are constructed inline).</summary>
    private const FunctionFlags InlineNewMask = FunctionFlags.Generator | FunctionFlags.Async;

    // ------------------------------------------------------------------ out-of-line helpers for the hot loop

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue TdzError(JsValue name) =>
        JsValue.FromObject(JsError.Create(Realm, ErrorKind.ReferenceError, $"Cannot access '{name}' before initialization"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ThisTdzError() =>
        JsValue.FromObject(JsError.Create(Realm, ErrorKind.ReferenceError, "Must call super constructor in derived class before accessing 'this' or returning from derived constructor"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue NotAFunctionError(JsValue receiver, JsValue name) =>
        ErrorValue(ErrorKind.TypeError, $"{DescribeReceiver(receiver)}.{name.AsStringUnchecked} is not a function");

    private JsThrowException NotAFunction(JsValue receiver, JsValue name) => new(NotAFunctionError(receiver, name));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue AddSlow(in JsValue a, in JsValue b) => Operations.Add(Realm, a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ArithSlow(Op op, in JsValue a, in JsValue b) => BigIntOps.Arith(Realm, op, a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private double ToNumberSlow(in JsValue a) => Operations.ToNumber(Realm, a);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ToInt32Slow(in JsValue a) => Operations.ToInt32(Realm, a);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ToBooleanSlow(in JsValue a) => Operations.ToBoolean(a);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool StrictEqualsSlow(in JsValue a, in JsValue b) => Operations.StrictEquals(a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsObject ConstructPrototype(JsClosure c)
    {
        var proto = c.Get("prototype");
        return proto.IsObject ? proto.AsObjectUnchecked : Realm.ObjectPrototype;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double JsMod(double a, double b)
    {
        // fast path for positive int operands
        if (a > 0 && b > 0 && a <= int.MaxValue && b <= int.MaxValue)
        {
            int ia = (int)a, ib = (int)b;
            if (ia == a && ib == b) return ia % ib;
        }
        return a % b;
    }

    private static JsValue TypeofValue(in JsValue v) => v.Tag switch
    {
        JsTag.Undefined => TypeofUndefined,
        JsTag.Null => TypeofObject,
        JsTag.Boolean => TypeofBoolean,
        JsTag.Number => TypeofNumber,
        JsTag.String => TypeofString,
        JsTag.Symbol => TypeofSymbol,
        JsTag.BigInt => TypeofBigInt,
        JsTag.Object => v.Ref is JsHtmlDda ? TypeofUndefined : v.Ref is JsFunctionObject ? TypeofFunction : TypeofObject,
        _ => TypeofUndefined,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool CompareSlow(Op op, in JsValue a, in JsValue b)
    {
        if (a.IsString && b.IsString)
        {
            int c = string.CompareOrdinal(a.AsStringUnchecked, b.AsStringUnchecked);
            return op switch { Op.Lt => c < 0, Op.Le => c <= 0, Op.Gt => c > 0, _ => c >= 0 };
        }
        switch (op)
        {
            case Op.Lt: return Operations.LessThan(Realm, a, b, true) == true;
            case Op.Gt: return Operations.LessThan(Realm, b, a, false) == true;
            case Op.Le: { var r = Operations.LessThan(Realm, b, a, false); return r == false; }
            default: { var r = Operations.LessThan(Realm, a, b, true); return r == false; }
        }
    }
}
