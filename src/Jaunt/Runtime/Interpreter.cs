using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    /// Runs <paramref name="start"/> (already pushed, or with <paramref name="recovering"/> the frame whose instruction at
    /// <c>f.Pc - 1</c> threw the value parked in <see cref="Pending"/>) until the entry frame returns. JS-to-JS calls and returns
    /// are handled inside this loop without growing the CLR stack; JS exceptions are dispatched through handler tables.
    /// </summary>
    /// <remarks>
    /// Only frequent opcodes are implemented inline (with fast paths that cannot throw); everything else is
    /// delegated to <see cref="RareOp"/>, and slow paths are out-of-line calls. Invariant: the local <c>pc</c> is the start of
    /// the executing instruction until it completes; <c>f.Pc</c> is synchronized before any operation that can throw or call out.
    /// <para>
    /// Every native-to-JS call enters this method, so its prologue is on the hot path of callbacks, accessors and sort
    /// comparators, and the JIT zeroes each stack slot that can hold a reference there. Two rules keep the frame small:
    /// the method has no exception handling (see <see cref="RunGuarded"/>), and it must stay well under the JIT's limit on locals.
    /// Above <c>0.75 * JitMaxLocalsToTrack</c> (768 locals) RyuJIT stops promoting structs, and every <see cref="JsValue"/> temporary
    /// becomes a zeroed stack slot copied with a byref write barrier. NativeAOT compiles every path; the JIT with PGO would
    /// have skipped inlining into the cold ones. So: rare opcode bodies live in helpers, a helper's <c>in JsValue</c> gets a
    /// register or constant in place instead of a local copy (a local whose address is taken stays on the stack), and a call
    /// path is inlined once. <c>samples/Jaunt.AotHost</c> built with <c>--codegenopt:JitDisasm=RunLoop</c> shows the frame.
    /// </para>
    /// </remarks>
    private JsValue RunLoop(Frame start, bool recovering)
    {
        Frame f = start;
        // Bytecode is verified when it is compiled (BytecodeVerifier: operands in range, jumps on instruction boundaries) and a
        // frame's register window covers the function's registers, so the loop addresses both directly: ip is the executing
        // instruction, r0 the frame's first register, code0 the first instruction. The arrays cannot move out from under
        // these while the frame is live (the GC tracks interior references; register chunks are never reallocated while in use).
        ref int code0 = ref MemoryMarshal.GetArrayDataReference(f.Code);
        ref int ip = ref Unsafe.Add(ref code0, f.Pc);
        ref JsValue r0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(f.Regs), f.Base);
        JsValue thrown;
        JsValue result;
        JsClosure callee;
        int callThis, callArgs, callArgc;

        if (recovering)
        {
            thrown = TakePending();
            goto Unwind;
        }

    Dispatch:
                while (true)
                {
                    switch ((Op)ip)
                    {
                        case Op.Nop: ip = ref Unsafe.Add(ref ip, 1); continue;   // a case for opcode 0 saves an instruction per dispatch
                        case Op.Mov: JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2))); ip = ref Unsafe.Add(ref ip, 3); continue;
                        case Op.LoadUndef: Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = default; ip = ref Unsafe.Add(ref ip, 2); continue;
                        case Op.LoadNull: Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.Null; ip = ref Unsafe.Add(ref ip, 2); continue;
                        case Op.LoadTrue: Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.True; ip = ref Unsafe.Add(ref ip, 2); continue;
                        case Op.LoadFalse: Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.False; ip = ref Unsafe.Add(ref ip, 2); continue;
                        case Op.LoadEmpty: Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.Empty; ip = ref Unsafe.Add(ref ip, 2); continue;
                        case Op.LoadInt: Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = new JsValue((double)Unsafe.Add(ref ip, 2)); ip = ref Unsafe.Add(ref ip, 3); continue;
                        case Op.LoadConst: JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), f.K[Unsafe.Add(ref ip, 2)]); ip = ref Unsafe.Add(ref ip, 3); continue;
                        case Op.LoadThis:
                        {
                            var t = f.This;
                            if (t.IsEmpty)
                            {
                                if (f.ThisBox != null && f.ThisBox.Value.IsNotEmpty) t = f.This = f.ThisBox.Value;
                                else { f.Pc = PcAt(ref code0, ref ip) + 1; thrown = ThisTdzError(); goto Unwind; }
                            }
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = t;
                            ip = ref Unsafe.Add(ref ip, 2);
                            continue;
                        }
                        case Op.CheckTdz:
                            if (Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)).IsEmpty) { f.Pc = PcAt(ref code0, ref ip) + 1; thrown = TdzError(f.K[Unsafe.Add(ref ip, 2)]); goto Unwind; }
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;

                        // ---------------------------------------------------------- environments
                        case Op.LdEnv:
                        {
                            var e = f.Env!;
                            for (int d = Unsafe.Add(ref ip, 2); d > 0; d--) e = e.Parent!;
                            JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), e.Slots[Unsafe.Add(ref ip, 3)]);
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }
                        case Op.LdEnvChk:
                        {
                            var e = f.Env!;
                            for (int d = Unsafe.Add(ref ip, 2); d > 0; d--) e = e.Parent!;
                            var v = e.Slots[Unsafe.Add(ref ip, 3)];
                            if (v.IsEmpty) { f.Pc = PcAt(ref code0, ref ip) + 1; thrown = TdzError(f.K[Unsafe.Add(ref ip, 4)]); goto Unwind; }
                            JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), v);
                            ip = ref Unsafe.Add(ref ip, 5);
                            continue;
                        }
                        case Op.StEnv:
                        {
                            var e = f.Env!;
                            for (int d = Unsafe.Add(ref ip, 1); d > 0; d--) e = e.Parent!;
                            JsValue.Store(ref e.Slots[Unsafe.Add(ref ip, 2)], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }
                        case Op.StEnvChk:
                        {
                            var e = f.Env!;
                            for (int d = Unsafe.Add(ref ip, 1); d > 0; d--) e = e.Parent!;
                            if (e.Slots[Unsafe.Add(ref ip, 2)].IsEmpty) { f.Pc = PcAt(ref code0, ref ip) + 1; thrown = TdzError(f.K[Unsafe.Add(ref ip, 4)]); goto Unwind; }
                            JsValue.Store(ref e.Slots[Unsafe.Add(ref ip, 2)], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
                            ip = ref Unsafe.Add(ref ip, 5);
                            continue;
                        }
                        case Op.PushEnv:
                        {
                            int imm = Unsafe.Add(ref ip, 1);
                            f.Env = new Env(f.Env, imm & 0xFFFF, imm >> 16);
                            f.EnvDepth++;
                            ip = ref Unsafe.Add(ref ip, 2);
                            continue;
                        }
                        case Op.PopEnv: f.Env = f.Env!.Parent; f.EnvDepth--; ip = ref Unsafe.Add(ref ip, 1); continue;
                        case Op.CopyEnv: f.Env = f.Env!.CopyForIteration(); ip = ref Unsafe.Add(ref ip, 1); continue;

                        // ---------------------------------------------------------- globals
                        case Op.GetGlobal:
                        {
                            ref var ic = ref f.Feedback.Ics[Unsafe.Add(ref ip, 3)];
                            var realm = Realm;
                            if (ic.Kind == IcKind.GlobalProp && ic.Epoch == realm.GlobalVersion)
                            {
                                var v = realm.GlobalObject.Dict!.Values[ic.Slot];
                                if (v.IsNotEmpty) { JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), v); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            }
                            else if (ic.Kind == IcKind.GlobalCell)
                            {
                                var v = Unsafe.As<GlobalCell>(ic.Cell!).Value;
                                if (v.IsNotEmpty) { JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), v); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            }
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = GetGlobalSlow(f.K[Unsafe.Add(ref ip, 2)].AsStringUnchecked, ref ic, false);
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }
                        case Op.SetGlobal:
                        {
                            ref var ic = ref f.Feedback.Ics[Unsafe.Add(ref ip, 3)];
                            if (ic.Kind == IcKind.GlobalProp && ic.Epoch == Realm.GlobalVersion)
                            {
                                Realm.GlobalObject.Dict!.Values[ic.Slot] = Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            SetGlobalSlow(f.K[Unsafe.Add(ref ip, 1)].AsStringUnchecked, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2)), ref ic, f.T.IsStrict);
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }

                        // ---------------------------------------------------------- properties
                        case Op.GetProp:
                        {
                            ref readonly var ov = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var ic = ref f.Feedback.Ics[Unsafe.Add(ref ip, 4)];
                            if (ov.IsObject)
                            {
                                var obj = Unsafe.As<JsObject>(ov.Ref!);
                                if (ReferenceEquals(obj.Shape, ic.Shape))
                                {
                                    if (ic.Kind == IcKind.OwnData)
                                    {
                                        var v = obj.Slots[ic.Slot];
                                        if (v.IsNotEmpty) { JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), v); ip = ref Unsafe.Add(ref ip, 5); continue; }
                                    }
                                    else if (ic.Kind == IcKind.ProtoData && ic.Epoch == ProtoEpoch)
                                    {
                                        var v = ic.Holder!.Slots[ic.Slot];
                                        if (v.IsNotEmpty) { JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), v); ip = ref Unsafe.Add(ref ip, 5); continue; }
                                    }
                                    else if (ic.Kind == IcKind.ProtoGetter && ic.Epoch == ProtoEpoch)
                                    {
                                        // accessor on the prototype chain (typed array length, Map size, class getters)
                                        // an accessor slot carries the Accessor as an Empty-tagged reference
                                        if (ic.Holder!.Slots[ic.Slot].Ref is Accessor acc)
                                        {
                                            var gf = GetterOp(f, PcAt(ref code0, ref ip), acc, ov);
                                            if (gf != null) { f = gf; goto Reload; }
                                            ip = ref Unsafe.Add(ref ip, 5);
                                            continue;
                                        }
                                    }
                                    else if (ic.Kind == IcKind.OwnGetter)
                                    {
                                        if (obj.Slots[ic.Slot].Ref is Accessor acc)
                                        {
                                            var gf = GetterOp(f, PcAt(ref code0, ref ip), acc, ov);
                                            if (gf != null) { f = gf; goto Reload; }
                                            ip = ref Unsafe.Add(ref ip, 5);
                                            continue;
                                        }
                                    }
                                    else if (ic.Kind == IcKind.NotFound && ic.Epoch == ProtoEpoch)
                                    {
                                        Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = default;
                                        ip = ref Unsafe.Add(ref ip, 5);
                                        continue;
                                    }
                                }
                                else if (ic.Kind == IcKind.ArrayLength && obj is JsArray la)
                                {
                                    Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = new JsValue((double)la.Length);
                                    ip = ref Unsafe.Add(ref ip, 5);
                                    continue;
                                }
                            }
                            else if (ov.IsString)
                            {
                                if (ic.Kind == IcKind.StringLength)
                                {
                                    Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = new JsValue((double)ov.StringLength);
                                    ip = ref Unsafe.Add(ref ip, 5);
                                    continue;
                                }
                                if (ic.Kind == IcKind.StringProtoData && ic.Epoch == ProtoEpoch && ReferenceEquals(ic.Holder!.Shape, ic.Shape))
                                {
                                    var v = ic.Holder.Slots[ic.Slot];
                                    if (v.IsNotEmpty) { JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), v); ip = ref Unsafe.Add(ref ip, 5); continue; }
                                }
                            }
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            if (ov.IsNullOrUndefined) { thrown = NullishReadError(ov, f.K[Unsafe.Add(ref ip, 3)]); goto Unwind; }
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = GetPropSlow(ov, f.K[Unsafe.Add(ref ip, 3)], ref ic);
                            ip = ref Unsafe.Add(ref ip, 5);
                            continue;
                        }
                        case Op.SetProp:
                        {
                            ref readonly var ov = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var ic = ref f.Feedback.Ics[Unsafe.Add(ref ip, 4)];
                            if (ov.IsObject)
                            {
                                var obj = Unsafe.As<JsObject>(ov.Ref!);
                                if (ReferenceEquals(obj.Shape, ic.Shape))
                                {
                                    if (ic.Kind == IcKind.OwnData) { JsValue.Store(ref obj.Slots[ic.Slot], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3))); ip = ref Unsafe.Add(ref ip, 5); continue; }
                                    if (ic.Kind == IcKind.AddTransition && ic.Epoch == ProtoEpoch && obj.Extensible && !obj.IsPrototypeObject)
                                    {
                                        int slot = ic.Slot;
                                        if (slot >= obj.Slots.Length) GrowSlots(obj, slot);
                                        JsValue.Store(ref obj.Slots[slot], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
                                        obj.Shape = ic.NewShape!;
                                        Realm.ChargeProperty();
                                        ip = ref Unsafe.Add(ref ip, 5);
                                        continue;
                                    }
                                }
                            }
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            if (ov.IsNullOrUndefined) { thrown = NullishSetError(ov, f.K[Unsafe.Add(ref ip, 2)]); goto Unwind; }
                            SetPropSlow(ov, f.K[Unsafe.Add(ref ip, 2)], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)), ref ic, f.T.IsStrict);
                            ip = ref Unsafe.Add(ref ip, 5);
                            continue;
                        }
                        case Op.GetElem:
                        {
                            ref readonly var ov = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref readonly var kv = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (ov.IsObject)
                            {
                                if (kv.IsNumber)
                                {
                                    if (ov.Ref is JsArray arr)
                                    {
                                        double dk = JsValue.LoadNumber(kv);
                                        uint idx = (uint)dk;
                                        if (idx == dk && arr.TryGetDense(idx, out var ev)) { JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), ev); ip = ref Unsafe.Add(ref ip, 5); continue; }
                                    }
                                    else if (ov.Ref is JsTypedArray ta && TypedGetFast(ta, JsValue.LoadNumber(kv), ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)))) { ip = ref Unsafe.Add(ref ip, 5); continue; }
                                }
                                // ordinary object with a computed key: cached (shape, key) → slot
                                ref var ic = ref f.Feedback.Ics[Unsafe.Add(ref ip, 4)];
                                if (ic.Kind == IcKind.KeyedOwnData)
                                {
                                    var obj = Unsafe.As<JsObject>(ov.Ref!);
                                    if (ReferenceEquals(obj.Shape, ic.Shape) && (kv.IsNumber ? ic.Cell == null && kv.Num == ic.Epoch : ReferenceEquals(ic.Cell, kv.Ref)))
                                    {
                                        var v = obj.Slots[ic.Slot];
                                        if (v.IsNotEmpty) { JsValue.Store(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), v); ip = ref Unsafe.Add(ref ip, 5); continue; }
                                    }
                                }
                                f.Pc = PcAt(ref code0, ref ip) + 1;
                                Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = GetElemKeyed(ov, kv, ref ic);
                                ip = ref Unsafe.Add(ref ip, 5);
                                continue;
                            }
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            if (ov.IsNullOrUndefined) { thrown = NullishReadError(ov, kv); goto Unwind; }
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = GetElemSlow(ov, kv);
                            ip = ref Unsafe.Add(ref ip, 5);
                            continue;
                        }
                        case Op.SetElem:
                        {
                            ref readonly var ov = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref readonly var kv = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (ov.IsObject)
                            {
                                if (kv.IsNumber && ov.Ref is JsArray arr && arr.Dense)
                                {
                                    double dk = JsValue.LoadNumber(kv);
                                    uint idx = (uint)dk;
                                    if (idx == dk && idx < arr.Length && idx < (uint)arr.Items.Length && arr.Items[idx].IsNotEmpty)
                                    {
                                        JsValue.Store(ref arr.Items[idx], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
                                        ip = ref Unsafe.Add(ref ip, 5);
                                        continue;
                                    }
                                    // append (a[a.length] = v): no setters/indices can intercept on a clean prototype chain
                                    if (idx == dk && idx == arr.Length && idx < uint.MaxValue - 1 && arr.LengthWritable && arr.Extensible
                                        && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
                                    {
                                        f.Pc = PcAt(ref code0, ref ip) + 1;
                                        arr.Push(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
                                        ip = ref Unsafe.Add(ref ip, 5);
                                        continue;
                                    }
                                }
                                else if (kv.IsNumber && ov.Ref is JsTypedArray ta && TypedSetFast(ta, JsValue.LoadNumber(kv), Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)))) { ip = ref Unsafe.Add(ref ip, 5); continue; }
                                ref var ic = ref f.Feedback.Ics[Unsafe.Add(ref ip, 4)];
                                if (ic.Kind == IcKind.KeyedOwnData)
                                {
                                    var obj = Unsafe.As<JsObject>(ov.Ref!);
                                    if (ReferenceEquals(obj.Shape, ic.Shape) && (kv.IsNumber ? ic.Cell == null && kv.Num == ic.Epoch : ReferenceEquals(ic.Cell, kv.Ref)))
                                    {
                                        JsValue.Store(ref obj.Slots[ic.Slot], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
                                        ip = ref Unsafe.Add(ref ip, 5);
                                        continue;
                                    }
                                }
                                f.Pc = PcAt(ref code0, ref ip) + 1;
                                SetElemKeyed(ov, kv, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)), f.T.IsStrict, ref ic);
                                ip = ref Unsafe.Add(ref ip, 5);
                                continue;
                            }
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            if (ov.IsNullOrUndefined && (kv.IsString || kv.IsNumber)) { thrown = NullishSetError(ov, kv); goto Unwind; }
                            SetElemSlow(ov, kv, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)), f.T.IsStrict);
                            ip = ref Unsafe.Add(ref ip, 5);
                            continue;
                        }
                        case Op.DefinePropK:
                        {
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            DefineLiteralProp(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), f.K[Unsafe.Add(ref ip, 2)], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)), ref f.Feedback.Ics[Unsafe.Add(ref ip, 4)]);
                            ip = ref Unsafe.Add(ref ip, 5);
                            continue;
                        }
                        case Op.NewObject:
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromObject(new JsObject(Realm, Realm.ObjectRootShape, Unsafe.Add(ref ip, 2)));
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        case Op.NewArray:
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = NewArrayValue(Unsafe.Add(ref ip, 2));
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        case Op.ArrayPush:
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            Unsafe.As<JsArray>(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)).Ref!).Push(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2)));
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;

                        // ---------------------------------------------------------- arithmetic (number fast paths)
                        case Op.Add:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a) + JsValue.LoadNumber(b));
                            else { f.Pc = PcAt(ref code0, ref ip) + 1; Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = AddSlow(a, b); }
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }
                        case Op.Sub:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a) - JsValue.LoadNumber(b)); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto ArithOutOfLine;
                        }
                        case Op.Mul:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a) * JsValue.LoadNumber(b)); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto ArithOutOfLine;
                        }
                        case Op.Div:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a) / JsValue.LoadNumber(b)); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto ArithOutOfLine;
                        }
                        case Op.Mod:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsMod(JsValue.LoadNumber(a), JsValue.LoadNumber(b))); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto ArithOutOfLine;
                        }
                        case Op.Exp:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), MathBuiltins.Pow(JsValue.LoadNumber(a), JsValue.LoadNumber(b))); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto ArithOutOfLine;
                        }
                        case Op.BitAnd:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber)
                            {
                                int x = Operations.NumberBitsToInt32(a.Bits), y = Operations.NumberBitsToInt32(b.Bits);
                                JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), x & y);
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto ArithOutOfLine;
                        }
                        case Op.BitOr:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber)
                            {
                                int x = Operations.NumberBitsToInt32(a.Bits), y = Operations.NumberBitsToInt32(b.Bits);
                                JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), x | y);
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto ArithOutOfLine;
                        }
                        case Op.BitXor:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber)
                            {
                                int x = Operations.NumberBitsToInt32(a.Bits), y = Operations.NumberBitsToInt32(b.Bits);
                                JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), x ^ y);
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto ArithOutOfLine;
                        }
                        case Op.Shl:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber)
                            {
                                int x = Operations.NumberBitsToInt32(a.Bits), y = Operations.NumberBitsToInt32(b.Bits);
                                JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), x << (y & 31));
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto ArithOutOfLine;
                        }
                        case Op.Shr:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber)
                            {
                                int x = Operations.NumberBitsToInt32(a.Bits), y = Operations.NumberBitsToInt32(b.Bits);
                                JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), x >> (y & 31));
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto ArithOutOfLine;
                        }
                        case Op.UShr:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber)
                            {
                                int x = Operations.NumberBitsToInt32(a.Bits), y = Operations.NumberBitsToInt32(b.Bits);
                                JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), (double)((uint)x >> (y & 31)));
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto ArithOutOfLine;
                        }
                        case Op.StrictEq:
                        case Op.StrictNe:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            bool eq = StrictEqualsInline(a, b);
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromBool(eq == ((Op)ip == Op.StrictEq));
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }
                        case Op.Lt:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromBool(a.Num < b.Num); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto CompareOutOfLine;
                        }
                        case Op.Le:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromBool(a.Num <= b.Num); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto CompareOutOfLine;
                        }
                        case Op.Gt:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromBool(a.Num > b.Num); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto CompareOutOfLine;
                        }
                        case Op.Ge:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3));
                            if (a.IsNumber && b.IsNumber) { Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromBool(a.Num >= b.Num); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto CompareOutOfLine;
                        }
                        case Op.Neg:
                        {
                            ref readonly var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber) JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), -JsValue.LoadNumber(a));
                            else { f.Pc = PcAt(ref code0, ref ip) + 1; Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = BigIntOps.Negate(Realm, a); }
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        }
                        case Op.Plus:
                        case Op.ToNumeric:
                        {
                            ref readonly var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber) Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = a;
                            else
                            {
                                f.Pc = PcAt(ref code0, ref ip) + 1;
                                Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = (Op)ip == Op.ToNumeric ? Operations.ToNumericValue(Realm, a) : new JsValue(ToNumberSlow(a));
                            }
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        }
                        case Op.Inc:
                        {
                            ref readonly var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber) JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a) + 1);
                            else { f.Pc = PcAt(ref code0, ref ip) + 1; Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = BigIntOps.Increment(Realm, a, 1); }
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        }
                        case Op.Dec:
                        {
                            ref readonly var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber) JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a) - 1);
                            else { f.Pc = PcAt(ref code0, ref ip) + 1; Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = BigIntOps.Increment(Realm, a, -1); }
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        }
                        case Op.Not:
                        {
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromBool(!TruthyInline(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2))));
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        }
                        case Op.Typeof:
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = TypeofValue(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2)));
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        case Op.ToPropertyKey:
                        {
                            // strings, symbols and array-index numbers are already keys (see Operations.ToPropertyKeyValue)
                            ref readonly var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            // an index is copied through a floating-point register, as the GetElem or SetElem after it reads it (JsValue.LoadNumber)
                            if (a.IsNumber && Operations.IsArrayIndexNumber(JsValue.LoadNumber(a))) JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a));
                            else if (a.IsString || a.IsSymbol) Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = a;
                            else { f.Pc = PcAt(ref code0, ref ip) + 1; Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = Operations.ToPropertyKeyValue(Realm, a); }
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        }

                        // ---------------------------------------------------------- fused compare-and-branch
                        case Op.AddI:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber) { JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a) + Unsafe.Add(ref ip, 3)); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto ArithImmOutOfLine;
                        }
                        case Op.SubI:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber) { JsValue.StoreNumber(ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), JsValue.LoadNumber(a) - Unsafe.Add(ref ip, 3)); ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto ArithImmOutOfLine;
                        }
                        case Op.LtIJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            if (a.IsNumber)
                            {
                                if (a.Num < Unsafe.Add(ref ip, 2)) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                                goto JumpOperand3;
                            }
                            goto CompareImmJmpOutOfLine;
                        }
                        case Op.LeIJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            if (a.IsNumber)
                            {
                                if (a.Num <= Unsafe.Add(ref ip, 2)) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                                goto JumpOperand3;
                            }
                            goto CompareImmJmpOutOfLine;
                        }
                        case Op.GtIJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            if (a.IsNumber)
                            {
                                if (a.Num > Unsafe.Add(ref ip, 2)) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                                goto JumpOperand3;
                            }
                            goto CompareImmJmpOutOfLine;
                        }
                        case Op.GeIJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            if (a.IsNumber)
                            {
                                if (a.Num >= Unsafe.Add(ref ip, 2)) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                                goto JumpOperand3;
                            }
                            goto CompareImmJmpOutOfLine;
                        }
                        case Op.LtJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber && b.IsNumber)
                            {
                                if (a.Num < b.Num) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                                goto JumpOperand3;
                            }
                            goto CompareJmpOutOfLine;
                        }
                        case Op.LeJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber && b.IsNumber)
                            {
                                if (a.Num <= b.Num) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                                goto JumpOperand3;
                            }
                            goto CompareJmpOutOfLine;
                        }
                        case Op.GtJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber && b.IsNumber)
                            {
                                if (a.Num > b.Num) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                                goto JumpOperand3;
                            }
                            goto CompareJmpOutOfLine;
                        }
                        case Op.GeJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber && b.IsNumber)
                            {
                                if (a.Num >= b.Num) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                                goto JumpOperand3;
                            }
                            goto CompareJmpOutOfLine;
                        }
                        case Op.LtIJmpTrue:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            if (a.IsNumber)
                            {
                                if (a.Num < Unsafe.Add(ref ip, 2)) goto JumpOperand3;
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto CompareImmJmpOutOfLine;
                        }
                        case Op.LeIJmpTrue:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            if (a.IsNumber)
                            {
                                if (a.Num <= Unsafe.Add(ref ip, 2)) goto JumpOperand3;
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto CompareImmJmpOutOfLine;
                        }
                        case Op.GtIJmpTrue:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            if (a.IsNumber)
                            {
                                if (a.Num > Unsafe.Add(ref ip, 2)) goto JumpOperand3;
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto CompareImmJmpOutOfLine;
                        }
                        case Op.GeIJmpTrue:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            if (a.IsNumber)
                            {
                                if (a.Num >= Unsafe.Add(ref ip, 2)) goto JumpOperand3;
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto CompareImmJmpOutOfLine;
                        }
                        case Op.LtJmpTrue:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber && b.IsNumber)
                            {
                                if (a.Num < b.Num) goto JumpOperand3;
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto CompareJmpOutOfLine;
                        }
                        case Op.LeJmpTrue:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber && b.IsNumber)
                            {
                                if (a.Num <= b.Num) goto JumpOperand3;
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto CompareJmpOutOfLine;
                        }
                        case Op.GtJmpTrue:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber && b.IsNumber)
                            {
                                if (a.Num > b.Num) goto JumpOperand3;
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto CompareJmpOutOfLine;
                        }
                        case Op.GeJmpTrue:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (a.IsNumber && b.IsNumber)
                            {
                                if (a.Num >= b.Num) goto JumpOperand3;
                                ip = ref Unsafe.Add(ref ip, 4);
                                continue;
                            }
                            goto CompareJmpOutOfLine;
                        }
                        case Op.StrictEqJmpFalse:
                        case Op.StrictNeJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            if (StrictEqualsInline(a, b) == ((Op)ip == Op.StrictEqJmpFalse)) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto JumpOperand3;
                        }
                        case Op.StrictEqKJmpFalse:
                        case Op.StrictNeKJmpFalse:
                        {
                            ref var a = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            ref var b = ref f.K[Unsafe.Add(ref ip, 2)];
                            if (StrictEqualsInline(a, b) == ((Op)ip == Op.StrictEqKJmpFalse)) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                            goto JumpOperand3;
                        }

                        // ---------------------------------------------------------- jumps
                        case Op.Jmp:
                        {
                            int target = Unsafe.Add(ref ip, 1);
                            ref int tip = ref Unsafe.Add(ref code0, target);
                            if (!Unsafe.IsAddressGreaterThan(ref tip, ref ip) && --_ticks <= 0) { f.Pc = PcAt(ref code0, ref ip) + 1; Checkpoint(); }
                            ip = ref tip;
                            continue;
                        }
                        case Op.JmpTrue:
                        case Op.JmpFalse:
                        {
                            bool truthy = TruthyInline(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)));
                            if (truthy == ((Op)ip == Op.JmpTrue))
                            {
                                int target = Unsafe.Add(ref ip, 2);
                                ref int tip = ref Unsafe.Add(ref code0, target);
                                if (!Unsafe.IsAddressGreaterThan(ref tip, ref ip) && --_ticks <= 0) { f.Pc = PcAt(ref code0, ref ip) + 1; Checkpoint(); }
                                ip = ref tip;
                            }
                            else ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        }
                        case Op.JmpNullish:
                            if (Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)).IsNullOrUndefined) ip = ref Unsafe.Add(ref code0, Unsafe.Add(ref ip, 2));
                            else ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        case Op.JmpNotNullish:
                            if (Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)).IsNotNullish) ip = ref Unsafe.Add(ref code0, Unsafe.Add(ref ip, 2));
                            else ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        case Op.JmpUndef:
                            if (Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)).IsUndefined) ip = ref Unsafe.Add(ref code0, Unsafe.Add(ref ip, 2));
                            else ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        case Op.JmpNotUndef:
                            if (Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)).IsNotUndefined) ip = ref Unsafe.Add(ref code0, Unsafe.Add(ref ip, 2));
                            else ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        case Op.SwitchInt:
                        {
                            int v = (int)Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)).Num;
                            int n = Unsafe.Add(ref ip, 2);
                            if ((uint)v < (uint)n) ip = ref Unsafe.Add(ref code0, Unsafe.Add(ref ip, 3 + v));
                            else ip = ref Unsafe.Add(ref ip, 3 + n);
                            continue;
                        }

                        // ---------------------------------------------------------- returns and throws
                        case Op.Ret:
                            result = Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            goto Return;
                        case Op.RetUndef:
                            result = default;
                            goto Return;
                        case Op.Throw:
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            thrown = Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            goto Unwind;

                        // ---------------------------------------------------------- calls
                        case Op.TailCall:
                        case Op.TailCallMethod:
                        case Op.TailCallSpread:
                        case Op.TailCallEval:
                        {
                            var nf = TailCallOp(f, PcAt(ref code0, ref ip));
                            if (nf != null) f = nf;
                            goto Reload;
                        }
                        case Op.Call:
                        {
                            ref readonly var fv = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            f.Pc = PcAt(ref code0, ref ip) + 6;
                            if (fv.Ref is JsClosure c && (c.Template.Flags & InlineCallMask) == 0)
                            {
                                callee = c;
                                int tr = Unsafe.Add(ref ip, 3);
                                callThis = tr;
                                callArgs = Unsafe.Add(ref ip, 4);
                                callArgc = Unsafe.Add(ref ip, 5);
                                goto EnterClosure;
                            }
                            var rf = CallOutOfLoop(f, PcAt(ref code0, ref ip), fv);
                            if (rf != null) { f = rf; goto Reload; }
                            ip = ref Unsafe.Add(ref ip, 6);
                            continue;
                        }
                        case Op.CallMethod:
                        {
                            ref readonly var ov = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            ref var ic = ref f.Feedback.Ics[Unsafe.Add(ref ip, 4)];
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
                                f.Pc = PcAt(ref code0, ref ip) + 1;
                                if (ov.IsNullOrUndefined) { thrown = NullishReadError(ov, f.K[Unsafe.Add(ref ip, 3)]); goto Unwind; }
                                fv = GetPropSlow(ov, f.K[Unsafe.Add(ref ip, 3)], ref ic);
                            }
                            f.Pc = PcAt(ref code0, ref ip) + 7;
                            if (fv.Ref is JsClosure c && (c.Template.Flags & InlineCallMask) == 0)
                            {
                                callee = c;
                                callThis = Unsafe.Add(ref ip, 2);
                                callArgs = Unsafe.Add(ref ip, 5);
                                callArgc = Unsafe.Add(ref ip, 6);
                                goto EnterClosure;
                            }
                            // arr.push(v) on a plain array: append without building NativeArgs
                            if (Unsafe.Add(ref ip, 6) == 1 && fv.Ref is JsBuiltin pb && ReferenceEquals(pb.Fn, ArrayBuiltins.PushFn) && ov.Ref is JsArray pa
                                && pa.Dense && pa.LengthWritable && pa.Extensible && pa.Length < uint.MaxValue - 1 && !JsArray.ProtoChainMayHaveIndices(pa.Proto))
                            {
                                pa.Push(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 5)));
                                Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = new JsValue((double)pa.Length);
                                ip = ref Unsafe.Add(ref ip, 7);
                                continue;
                            }
                            var rf = CallOutOfLoop(f, PcAt(ref code0, ref ip), fv);
                            if (rf != null) { f = rf; goto Reload; }
                            ip = ref Unsafe.Add(ref ip, 7);
                            continue;
                        }
                        case Op.New:
                        {
                            f.Pc = PcAt(ref code0, ref ip) + 5;
                            var nf = NewOp(f, PcAt(ref code0, ref ip));
                            if (nf == null) { ip = ref Unsafe.Add(ref ip, 5); continue; }
                            f = nf;
                            goto Reload;
                        }
                        case Op.SuperCall:
                        {
                            f.Pc = PcAt(ref code0, ref ip) + 4;
                            var nf = SuperCallOp(f, PcAt(ref code0, ref ip));
                            if (nf != null) f = nf;
                            goto Reload;
                        }
                        case Op.Closure:
                        {
                            int fi = Unsafe.Add(ref ip, 2);
                            Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromObject(CreateClosure(f.T.Functions[fi], f.Env, f.Feedback.Child(fi), f));
                            ip = ref Unsafe.Add(ref ip, 3);
                            continue;
                        }

                        // ---------------------------------------------------------- private names (#x): a field read or write
                        // on an object that has it is the common case; everything else (methods, accessors, errors) is out of line
                        case Op.PrivateGet:
                        {
                            ref readonly var ov = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2));
                            var name = Unsafe.As<JsPrivateName>(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)).Ref!);
                            if (!PrivateGetFast(ov, name, ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1))))
                            {
                                f.Pc = PcAt(ref code0, ref ip) + 1;
                                Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = PrivateGet(ov, name);
                            }
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }
                        case Op.PrivateSet:
                        {
                            ref readonly var ov = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            var name = Unsafe.As<JsPrivateName>(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2)).Ref!);
                            if (!PrivateSetFast(ov, name, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3))))
                            {
                                f.Pc = PcAt(ref code0, ref ip) + 1;
                                PrivateSet(ov, name, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
                            }
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }
                        case Op.PrivateDefine:
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            PrivateDefine(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), Unsafe.As<JsPrivateName>(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2)).Ref!), Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;

                        // ---------------------------------------------------------- loops over iterators
                        case Op.IterNext:
                        {
                            int pc = PcAt(ref code0, ref ip);
                            f.Pc = pc + 1;
                            ip = ref Unsafe.Add(ref code0, IterNextOp(f, pc));
                            continue;
                        }
                        case Op.ForInNext:
                        {
                            int pc = PcAt(ref code0, ref ip);
                            f.Pc = pc + 1;
                            ip = ref Unsafe.Add(ref code0, ForInNextOp(f, pc));
                            continue;
                        }
                        case Op.DestructArray:
                        {
                            ref readonly var sv = ref Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1));
                            int dst = Unsafe.Add(ref ip, 2), n = Unsafe.Add(ref ip, 3);
                            if (sv.IsObject && sv.Ref is JsArray da && TryDestructDense(da, ref Unsafe.Add(ref r0, dst), n)) { ip = ref Unsafe.Add(ref ip, 4); continue; }
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            DestructArraySlow(sv, f.Regs, f.Base + dst, n);
                            ip = ref Unsafe.Add(ref ip, 4);
                            continue;
                        }

                        // ---------------------------------------------------------- coroutines
                        case Op.Yield:
                        case Op.YieldRaw:
                        case Op.Await:
                            return SuspendOp(f, PcAt(ref code0, ref ip));
                        case Op.InitialYield:
                            return InitialYieldOp(f, PcAt(ref code0, ref ip));

                        // classes with fields: out of the way of the hotter cases above
                        case Op.SuperBind:
                        {
                            f.Pc = PcAt(ref code0, ref ip) + 2;
                            var nf = SuperBindOp(f, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)));
                            if (nf != null) { f = nf; goto Reload; }
                            ip = ref Unsafe.Add(ref ip, 2);
                            continue;
                        }
                        case Op.DefineFieldK:
                            f.Pc = PcAt(ref code0, ref ip) + 1;
                            DefineFieldProp(Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), f.K[Unsafe.Add(ref ip, 2)], Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)), ref f.Feedback.Ics[Unsafe.Add(ref ip, 4)]);
                            ip = ref Unsafe.Add(ref ip, 5);
                            continue;
                        case Op.RetFieldInit:
                            if ((f.Flags & FrameFlags.NoResult) == 0) { result = default; goto Return; }
                            f = PopFieldInitFrame(f);
                            goto Reload;

                        default:
                        {
                            int pc = PcAt(ref code0, ref ip);
                            f.Pc = pc + 1;
                            ip = ref Unsafe.Add(ref code0, RareOp(f, pc));
                            continue;
                        }
                    }
                }

    ArithOutOfLine:
        f.Pc = PcAt(ref code0, ref ip) + 1;
        Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = ArithSlow((Op)ip, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2)), Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3)));
        ip = ref Unsafe.Add(ref ip, 4);
        goto Dispatch;

    ArithImmOutOfLine:
        f.Pc = PcAt(ref code0, ref ip) + 1;
        Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = ArithImmSlow((Op)ip, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2)), Unsafe.Add(ref ip, 3));
        ip = ref Unsafe.Add(ref ip, 4);
        goto Dispatch;

    CompareImmJmpOutOfLine:
        f.Pc = PcAt(ref code0, ref ip) + 1;
        if (CompareImmSlow((Op)ip, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), Unsafe.Add(ref ip, 2)) != (Op)ip >= Op.LtJmpTrue)
        {
            ip = ref Unsafe.Add(ref ip, 4);
            goto Dispatch;
        }
        goto JumpOperand3;

    CompareOutOfLine:
        f.Pc = PcAt(ref code0, ref ip) + 1;
        Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)) = JsValue.FromBool(CompareSlow((Op)ip, Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2)), Unsafe.Add(ref r0, Unsafe.Add(ref ip, 3))));
        ip = ref Unsafe.Add(ref ip, 4);
        goto Dispatch;

    CompareJmpOutOfLine:
        f.Pc = PcAt(ref code0, ref ip) + 1;
        {
            if (CompareSlow(CompareOf((Op)ip), Unsafe.Add(ref r0, Unsafe.Add(ref ip, 1)), Unsafe.Add(ref r0, Unsafe.Add(ref ip, 2))) != (Op)ip >= Op.LtJmpTrue)
            {
                ip = ref Unsafe.Add(ref ip, 4);
                goto Dispatch;
            }
        }
    JumpOperand3:
        {
            ref int tip = ref Unsafe.Add(ref code0, Unsafe.Add(ref ip, 3));
            if (!Unsafe.IsAddressGreaterThan(ref tip, ref ip) && --_ticks <= 0) { f.Pc = PcAt(ref code0, ref ip) + 1; Checkpoint(); }
            ip = ref tip;
            goto Dispatch;
        }

    EnterClosure:
        {
            // A call is about a third of the instructions of call-heavy code, so this is PushCallFrame with everything rare moved
            // out of the way, and such a frame keeps FrameFlags clear for Return's single test. See Vm.BindCallWindow for
            // the overlapping register window. A callee with an arguments array, or a caller whose registers are not the top of the
            // shared stack (a coroutine, or the chunk is full), takes the general path.
            if (--_ticks <= 0) Checkpoint();
            var ct = callee.Template;
            int bp = f.Base;
            int dst = bp + Unsafe.Add(ref ip, 1);
            JsValue[] regs = f.Regs;
            int argStart = bp + callArgs;
            int end = argStart + ct.RegisterCount;
            var tflags = ct.Flags;
            if ((tflags & FunctionFlags.NeedsArgsArray) != 0 || !ReferenceEquals(regs, _chunk) || end > regs.Length)
            {
                f = PushCallFrameOutOfLine(callee, regs, callThis < 0 ? -1 : bp + callThis, argStart, callArgc);
                f.RetIndex = dst;
                goto Reload;
            }
            int d = Sp;
            if (d >= _spLimit) StackOverflowFrame();
            var nf = _frames[d] ?? RentFrameSlow();
            nf.SavedTop = _top;
            nf.Flags = FrameFlags.None;
            _top = end;
            if (end > _hwm) CallWindowHighWater(nf, ct, regs, argStart, end);
            nf.Base = argStart;
            if (!ReferenceEquals(nf.Regs, regs)) nf.Regs = regs;
            // the bindings and missing parameters start as undefined, every other register is written before it is read
            int n = callArgc < ct.ParamCount ? callArgc : ct.ParamCount;
            int initEnd = ct.InitUndefEnd;
            while (n < initEnd) { regs[argStart + n] = default; n++; }
            if (!ReferenceEquals(nf.Callee, callee))
            {
                nf.Callee = callee;
                var fb = callee.Feedback;
                if (!ReferenceEquals(nf.Feedback, fb)) nf.Feedback = fb;
            }
            nf.Pc = 0;
            var env = callee.Env;
            if (!ReferenceEquals(nf.Env, env)) nf.Env = env;
            nf.EnvDepth = 0;
            nf.Suspend = SuspendKind.None;
            var calleeRealm = callee.Realm;
            if (!ReferenceEquals(Realm, calleeRealm)) SwitchCallRealm(nf, calleeRealm);
            if ((tflags & FunctionFlags.NoThisBinding) == 0)
            {
                if (callThis < 0 || (tflags & FunctionFlags.Arrow) != 0 || callee.HasRareParts || nf.ThisBox != null)
                    BindThisSlow(nf, callee, regs, callThis < 0 ? -1 : bp + callThis);
                else
                {
                    ref readonly var tv = ref regs[bp + callThis];
                    if ((tflags & FunctionFlags.Strict) != 0 || tv.IsObject)
                    {
                        if (!ReferenceEquals(nf.This.Ref, tv.Ref) || nf.This.Bits != tv.Bits) nf.This = tv;
                    }
                    else nf.This = CoerceSloppyThis(calleeRealm, tv);
                }
            }
            Sp = d + 1;
            nf.RetIndex = dst;
            f = nf;
            code0 = ref MemoryMarshal.GetArrayDataReference(ct.Code);
            ip = ref code0;
            r0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(regs), argStart);
            goto Dispatch;
        }

    Return:
        {
            var flags = f.Flags;
            int ret = f.RetIndex;
            if (flags == FrameFlags.None)
            {
                _top = f.SavedTop;
                int d = Sp - 1;
                Sp = d;
                f = _frames[d - 1]!;
            }
            else
            {
                if ((flags & FrameFlags.Construct) != 0 && result.IsNotObject)
                {
                    result = ConstructReturnValue(f, result);
                    if (result.IsEmpty) { f = ConstructReturnThrow(f); goto Reload; }
                }
                if ((flags & FrameFlags.Coroutine) != 0 && (flags & FrameFlags.Entry) != 0)
                {
                    FinishCoroutine(f);
                    return result;
                }
                PopFrame(f);
                if ((flags & FrameFlags.Entry) != 0) return result;
                f = FrameAt(Sp - 1);
            }
            ref JsValue regs0 = ref MemoryMarshal.GetArrayDataReference(f.Regs);
            JsValue.Store(ref Unsafe.Add(ref regs0, ret), result);
            r0 = ref Unsafe.Add(ref regs0, f.Base);
            code0 = ref MemoryMarshal.GetArrayDataReference(f.Code);
            ip = ref Unsafe.Add(ref code0, f.Pc);
            goto Dispatch;
        }

    Unwind:
        f = UnwindToHandler(f, thrown);
    Reload:
        if (ReferenceEquals(f, _resultThrown)) return TakePending();
        code0 = ref MemoryMarshal.GetArrayDataReference(f.Code);
        ip = ref Unsafe.Add(ref code0, f.Pc);
        r0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(f.Regs), f.Base);
        goto Dispatch;
    }

    /// <summary>
    /// IsStrictlyEqual without a call unless both are strings or BigInts with different references: values with different
    /// <see cref="JsValue.Bits"/> that are not two numbers have different types (or are different booleans), and for equal
    /// Bits (type, or true/false/undefined/null itself) the same reference is the same value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StrictEqualsInline(in JsValue a, in JsValue b)
    {
        if (a.IsNumber && b.IsNumber) return a.Num == b.Num;
        if (a.Bits != b.Bits) return false;
        return ReferenceEquals(a.Ref, b.Ref) || StrictEqualsSlow(a, b);
    }

    /// <summary>ToBoolean without a call for booleans, undefined, null and numbers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TruthyInline(in JsValue v)
    {
        ulong bits = v.Bits;
        // A number other than ±0 and NaN, tested on its bits in an integer register: without the sign bit, zero is 0, the
        // infinities are 0xFFE0... and a NaN is above. Every non-number tag decodes to a NaN, so it fails this test too.
        if (((bits ^ JsValue.NumberXor) << 1) - 1 < 0xFFE0_0000_0000_0000UL) return true;
        if (bits == JsValue.TrueBits) return true;
        if (bits <= JsValue.FalseBits || bits > 15) return false;   // undefined, null, false; ±0 and NaN
        return ToBooleanSlow(v);
    }

    /// <summary>The index of the instruction at <paramref name="ip"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PcAt(ref int code0, ref int ip) => (int)(Unsafe.ByteOffset(ref code0, ref ip) >> 2);

    /// <summary>
    /// Runs the loop with a handler for exceptions thrown by the helpers and builtins it calls: JS <c>throw</c> inside the
    /// loop and <see cref="Raise"/> reach a handler table without a CLR exception, everything else lands here and resumes
    /// the loop at the innermost frame, which recorded its pc. The try region is out of <see cref="RunLoop"/> because every
    /// local that is live into or through a handler is kept on the stack: <c>this</c> would be loaded from memory on each use.
    /// </summary>
    private JsValue RunGuarded(Frame entry)
    {
        entry.Flags |= FrameFlags.Entry;
        Frame f = entry;
        bool recovering = false;
        while (true)
        {
            try
            {
                return RunLoop(f, recovering);
            }
            catch (JsThrowException ex) when (CatchesEscape())
            {
                Pending = ex.Value;
            }
            // every frame above the faulting one has already been popped by the nested loops that owned them, so the
            // innermost frame is the one executing the instruction at f.Pc - 1
            f = FrameAt(Sp - 1);
            recovering = true;
        }
    }

    /// <summary>Set while a value unwound past a loop's entry frame is on its way out to that loop's caller.</summary>
    private bool _escaping;

    /// <summary>The exception that leaves the current <see cref="RunGuarded"/>: its filter lets exactly this one through.</summary>
    private JsThrowException EscapeLoop(JsValue thrown)
    {
        _escaping = true;
        return new JsThrowException(thrown);
    }

    private bool CatchesEscape()
    {
        if (!_escaping) return true;
        _escaping = false;
        return false;
    }

    /// <summary>
    /// Finds the handler for <paramref name="thrown"/>, raised by the instruction at <c>f.Pc - 1</c>: pops the frames without one,
    /// stores the value in the exception register and returns the frame, with <c>Pc</c> at the handler. Past the loop's entry
    /// frame the value continues as a CLR exception, or as the loop's result for a <see cref="FrameFlags.ThrowAsResult"/> frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame UnwindToHandler(Frame f, JsValue thrown)
    {
        while (true)
        {
            int at = f.Pc - 1;
            var handlers = f.T.Handlers;
            for (int i = 0; i < handlers.Length; i++)
            {
                if (at >= handlers[i].Start && at < handlers[i].End)
                {
                    ref readonly var h = ref handlers[i];
                    while (f.EnvDepth > h.EnvDepth) { f.Env = f.Env!.Parent; f.EnvDepth--; }
                    f.Regs[f.Base + h.ExceptionReg] = thrown;
                    f.Pc = h.Target;
                    return f;
                }
            }
            if ((f.Flags & FrameFlags.Entry) != 0)
            {
                if ((f.Flags & FrameFlags.Coroutine) != 0)
                {
                    FinishCoroutine(f);
                    if ((f.Flags & FrameFlags.ThrowAsResult) != 0)
                    {
                        f.Suspend = SuspendKind.Thrown;
                        Pending = thrown;
                        return _resultThrown;
                    }
                }
                else PopFrame(f);
                throw EscapeLoop(thrown);
            }
            PopFrame(f);
            f = FrameAt(Sp - 1);
        }
    }

    /// <summary>
    /// What <see cref="UnwindToHandler"/> returns when a <see cref="FrameFlags.ThrowAsResult"/> entry frame finished by throwing: the
    /// loop then returns the value in <see cref="Pending"/>, whichever path unwound. It is a marker, never run.
    /// </summary>
    private readonly Frame _resultThrown = new();

    /// <summary>A coroutine's entry frame ran to completion or threw: it detaches for good.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FinishCoroutine(Frame f)
    {
        f.Suspend = SuspendKind.None;
        LastSuspend = SuspendKind.None;
        DetachFrame(f);
    }

    /// <summary>[[Construct]] return semantics for a non-object result; <see cref="JsValue.Empty"/> with <see cref="Pending"/> set when they throw.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ConstructReturnValue(Frame f, JsValue value)
    {
        // the usual end of a constructor: nothing returned, this bound in the frame
        ref readonly var self = ref f.This;
        if (self.IsObject && f.ThisBox == null && (value.IsUndefined || !f.Callee.IsDerived))
        {
            LearnInstanceSlots(f, self);
            return self;
        }
        if (!TryConstructResult(f, ref value, out var error)) return Raise(error);
        return value;
    }

    /// <summary>
    /// A call in tail position. Proper tail call: the callee replaces this frame (constant stack depth) and is returned;
    /// otherwise an ordinary call whose result the following Ret returns: the result is stored, <c>f.Pc</c> is set to the next
    /// instruction and the result is null.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame? TailCallOp(Frame f, int pc)
    {
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        JsValue[] k = f.K;
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
                targs = sarr.ContiguousElements();
            }
            else targs = new ReadOnlySpan<JsValue>(regs, bp + code[pc + 4], code[pc + 5]);
            if (top == Op.TailCallEval && fv.IsObject && ReferenceEquals(fv.Ref, Realm.EvalFunction))
            {
                // a direct eval is never a tail call
                int flags = code[pc + 6];
                var ev = targs.Length == 0 ? JsValue.Undefined : PerformEval(targs[0], f, (flags & 1) != 0, (flags & 2) != 0, flags);
                regs[bp + code[pc + 1]] = ev;
                f.Pc = pc + len;
                return null;
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
            return nf;
        }
        var tres = CallValue(fv, thisV, targs);
        regs[dst] = tres;
        f.Pc = pc + len;
        return null;
    }

    /// <summary>
    /// Op.Call / Op.CallMethod of anything but a closure the loop can enter itself; <c>f.Pc</c> is at the next instruction.
    /// Null when the call completed and its result is stored. Otherwise the frame to continue in, at its <c>Pc</c>: the pushed
    /// frame of a redirected call (see <see cref="TryPushRedirectedCall"/>) or the frame that handles the error of a failed one.
    /// A builtin is called directly so an error it raises (<see cref="Raise"/>) unwinds without a CLR throw.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame? CallOutOfLoop(Frame f, int pc, JsValue fv)
    {
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        int thisIndex, argStart, argc;
        bool method = (Op)code[pc] == Op.CallMethod;
        if (method)
        {
            thisIndex = bp + code[pc + 2];
            argStart = bp + code[pc + 5];
            argc = code[pc + 6];
        }
        else
        {
            int tr = code[pc + 3];
            thisIndex = tr < 0 ? -1 : bp + tr;
            argStart = bp + code[pc + 4];
            argc = code[pc + 5];
        }
        int dst = bp + code[pc + 1];
        JsValue thrown;
        if (fv.IsObject && fv.Ref is JsFunctionObject fo)
        {
            if (fo is not JsClosure)
            {
                // f.call / f.apply / a bound closure: push the target's frame here instead of re-entering the loop
                var rf = TryPushRedirectedCall(fo, thisIndex, regs, argStart, argc);
                if (rf != null) { rf.RetIndex = dst; return rf; }
            }
            var r = fo is JsBuiltin b
                ? b.Fn(new NativeArgs(b.Realm, thisIndex < 0 ? default : regs[thisIndex], new ReadOnlySpan<JsValue>(regs, argStart, argc), JsValue.Undefined, b))
                : fo.Call(thisIndex < 0 ? default : regs[thisIndex], new ReadOnlySpan<JsValue>(regs, argStart, argc));
            if (!r.IsEmpty) { regs[dst] = r; return null; }
            thrown = TakePending();
        }
        else thrown = method ? NotAFunctionError(regs[thisIndex], f.K[code[pc + 3]]) : NotCallableError(fv);
        f.Pc = pc + 1;
        return UnwindToHandler(f, thrown);
    }

    /// <summary>
    /// Op.GetProp on a cached accessor: the pushed frame of a getter the loop can enter (its result goes to the instruction's
    /// destination when it returns, and the loop continues after the instruction), or null when the result is stored.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame? GetterOp(Frame f, int pc, Accessor accessor, in JsValue receiver)
    {
        int dst = f.Base + f.Code[pc + 1];
        if (accessor.Getter is JsClosure c && (c.Template.Flags & InlineCallMask) == 0)
        {
            f.Pc = pc + 5;
            if (--_ticks <= 0) Checkpoint();
            var nf = PushFrame(c, receiver, ReadOnlySpan<JsValue>.Empty, default, FrameFlags.None);
            nf.RetIndex = dst;
            return nf;
        }
        f.Pc = pc + 1;
        var v = CallCachedGetter(accessor, receiver);
        f.Regs[dst] = v;
        return null;
    }

    /// <summary>Op.New (<c>f.Pc</c> is at the next instruction): the pushed frame of a closure the loop can enter, or null when the construction completed and its result is stored.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame? NewOp(Frame f, int pc)
    {
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        ref readonly var fv = ref regs[bp + code[pc + 2]];
        int argStart = bp + code[pc + 3];
        int argc = code[pc + 4];
        int dst = bp + code[pc + 1];
        if (fv.IsObject && fv.Ref is JsClosure c && (c.Template.Flags & InlineNewMask) == 0 && c.Template.IsConstructor)
        {
            if (--_ticks <= 0) Checkpoint();
            if (c.IsDerived || c.FieldInit != null) return PushClassNewFrame(c, f, argStart, argc, fv, dst);
            var nf = PushNewFrame(c, regs, argStart, argc, NewInstance(c, ConstructPrototype(c)), fv);
            nf.RetIndex = dst;
            return nf;
        }
        regs[dst] = Construct(fv, new ReadOnlySpan<JsValue>(regs, argStart, argc));
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame? SuperCallOp(Frame f, int pc)
    {
        int[] code = f.Code;
        int bp = f.Base;
        int argStart = bp + code[pc + 2];
        int argc = code[pc + 3];
        int dst = bp + code[pc + 1];
        if (--_ticks <= 0) Checkpoint();
        var nf = TryPushSuperFrame(f, argStart, argc, dst);
        if (nf != null) return nf;
        var result = SuperConstructFrom(f, argStart, argc);
        f.Regs[dst] = result;
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int IterNextOp(Frame f, int pc)
    {
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        var rec = Unsafe.As<IteratorRecord>(regs[bp + code[pc + 2]].Ref!);
        if (!rec.Step(out var v)) return code[pc + 3];
        regs[bp + code[pc + 1]] = v;
        return pc + 4;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ForInNextOp(Frame f, int pc)
    {
        int[] code = f.Code;
        JsValue[] regs = f.Regs;
        int bp = f.Base;
        var it = Unsafe.As<ForInIterator>(regs[bp + code[pc + 2]].Ref!);
        if (!it.Next(out var key)) return code[pc + 3];
        regs[bp + code[pc + 1]] = new JsValue(key);
        return pc + 4;
    }

    /// <summary>Op.Yield, Op.YieldRaw, Op.Await: detaches the coroutine frame and returns the value it hands out of the loop.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue SuspendOp(Frame f, int pc)
    {
        if ((f.Flags & FrameFlags.Entry) == 0) throw new InvalidOperationException("Jaunt internal error: suspension outside coroutine entry frame.");
        int[] code = f.Code;
        var op = (Op)code[pc];
        var result = f.Regs[f.Base + code[pc + 1]];
        f.ResumeSentReg = code[pc + 2];
        f.ResumeModeReg = code[pc + 3];
        f.Pc = pc + 4;
        f.Suspend = op == Op.Await ? SuspendKind.Await : op == Op.Yield ? SuspendKind.Yield : SuspendKind.YieldRaw;
        LastSuspend = f.Suspend;
        DetachFrame(f);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue InitialYieldOp(Frame f, int pc)
    {
        int[] code = f.Code;
        f.ResumeSentReg = code[pc + 1];
        f.ResumeModeReg = code[pc + 2];
        f.Pc = pc + 3;
        f.Suspend = SuspendKind.Initial;
        LastSuspend = SuspendKind.Initial;
        DetachFrame(f);
        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue NewArrayValue(int capacity) => JsValue.FromObject(new JsArray(Realm, capacity));

    /// <summary>
    /// Op.DestructArray on an array whose iteration is unobservable (pristine %ArrayIteratorPrototype%.next and
    /// @@iterator, dense contiguous storage): the first <paramref name="n"/> elements straight from the store, undefined past
    /// the end. False for anything else, a hole included (it reads through the prototype chain): the general path then
    /// starts over, nothing observable has happened yet.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryDestructDense(JsArray a, ref JsValue dst, int n)
    {
        if (!a.Dense || !Realm.IsArrayIterationPristine(a)) return false;
        var items = a.Items;
        uint len = a.Length;
        for (int i = 0; i < n; i++)
        {
            if ((uint)i < len)
            {
                if ((uint)i >= (uint)items.Length) return false;
                var v = items[i];
                if (v.IsEmpty) return false;
                JsValue.Store(ref Unsafe.Add(ref dst, i), v);
            }
            else Unsafe.Add(ref dst, i) = default;
        }
        return true;
    }

    /// <summary>Op.DestructArray by the iterator protocol: GetIterator, <paramref name="n"/> steps (an abrupt step closes nothing), IteratorClose.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DestructArraySlow(in JsValue src, JsValue[] regs, int dst, int n)
    {
        var rec = IteratorRecord.Get(Realm, src);
        for (int i = 0; i < n; i++) regs[dst + i] = rec.Step(out var v) ? v : default;
        rec.Close();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TypedGetFast(JsTypedArray ta, double key, ref JsValue dst)
    {
        return ta.TryGetElementFast(key, ref dst);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TypedSetFast(JsTypedArray ta, double key, in JsValue v) => ta.TrySetElementFast(key, v);

    /// <summary>
    /// A constructor's return failed its [[Construct]] checks (the error is in <see cref="Pending"/>): it is raised in the caller's
    /// context, after the callee frame is removed. Returns the frame that handles it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame ConstructReturnThrow(Frame done)
    {
        var thrown = TakePending();
        bool entry = (done.Flags & FrameFlags.Entry) != 0;
        PopFrame(done);
        if (entry) throw EscapeLoop(thrown);
        return UnwindToHandler(FrameAt(Sp - 1), thrown);
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
    private static bool StrictEqualsSlow(in JsValue a, in JsValue b)
    {
        // two different flat strings of one type tag, which usually differ early (a tag such as e.kind against a case label)
        if (a.Ref is string x && b.Ref is string y)
        {
            if (x.Length != y.Length) return false;
            return x.Length == 0 || (x[0] == y[0] && string.Equals(x, y));
        }
        return Operations.StrictEquals(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsObject ConstructPrototype(JsClosure c)
    {
        var fast = c.PrototypeFast();
        if (fast != null) return fast;
        var proto = c.Get("prototype");
        return proto.IsObject ? proto.AsObjectUnchecked : Operations.GetPrototypeFromConstructor(Realm, c, Realm.ObjectPrototype);
    }

    /// <summary>The frame to run for a class constructor that <c>new</c> enters in this loop: derived (this unbound) or with instance fields to initialize first, in a frame of their own on top of the constructor's.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame PushClassNewFrame(JsClosure c, Frame caller, int argStart, int argc, JsValue newTarget, int dst)
    {
        var thisValue = ConstructThis(c, newTarget);
        var ctorFrame = PushConstructFrame(c, caller.Regs, argStart, argc, thisValue, newTarget);
        ctorFrame.RetIndex = dst;
        var init = c.IsDerived ? null : c.FieldInit;
        return init != null ? PushFieldInitFrame(init, ctorFrame.Regs, thisValue) : ctorFrame;
    }

    /// <summary>The frame a field initializer pushed by <see cref="PushFieldInitFrame"/> resumes when it ends; nothing is stored in it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame PopFieldInitFrame(Frame f)
    {
        PopFrame(f);
        return FrameAt(Sp - 1);
    }

    /// <summary>
    /// The second half of super(...) in this loop: binds this, then the frame of the field initializer to run, or null.
    /// A throw from it is seen by this frame's handlers at the super() call, as the spec has it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame? SuperBindOp(Frame f, JsValue result)
    {
        var init = SuperBind(f, result);
        return init != null ? PushFieldInitFrame(init, f.Regs, result) : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue SuperConstructFrom(Frame f, int argStart, int argc) =>
        SuperConstruct(f, argc < 0 ? new ReadOnlySpan<JsValue>(f.Args) : new ReadOnlySpan<JsValue>(f.Regs, argStart, argc));

    private JsValue ConstructThis(JsClosure c, JsValue newTarget)
    {
        if (c.IsDerived) return JsValue.Empty;
        var nt = Unsafe.As<JsObject>(newTarget.Ref!);   // a new.target is an object, checked where it came from
        return JsValue.FromObject(NewInstance(nt, Operations.GetPrototypeFromConstructor(Realm, nt, Realm.ObjectPrototype)));
    }

    /// <summary>
    /// super(...) in a derived constructor whose parent is a closure: its frame, to run in this loop. Null for the other cases,
    /// which <see cref="SuperConstruct"/> takes. A negative <paramref name="argc"/> forwards the frame's arguments (default constructor).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame? TryPushSuperFrame(Frame f, int argStart, int argc, int dst)
    {
        var ctor = f.Callee;
        if (!ctor.IsDerived || (f.Flags & FrameFlags.Construct) == 0) return null;
        if (ctor.GetPrototypeOf() is not JsClosure parent || (parent.Template.Flags & InlineNewMask) != 0 || !parent.Template.IsConstructor) return null;
        var thisValue = ConstructThis(parent, f.NewTarget);
        var ctorFrame = argc < 0
            ? PushFrame(parent, thisValue, f.Args, f.NewTarget, FrameFlags.Construct)
            : PushConstructFrame(parent, f.Regs, argStart, argc, thisValue, f.NewTarget);
        ctorFrame.RetIndex = dst;
        var init = parent.IsDerived ? null : parent.FieldInit;
        return init != null ? PushFieldInitFrame(init, ctorFrame.Regs, thisValue) : ctorFrame;
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

    /// <summary>AddI and SubI on a left operand that is not a number: the literal as a value, then the instructions' usual slow paths.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ArithImmSlow(Op op, in JsValue a, int imm)
    {
        var b = new JsValue((double)imm);
        return op == Op.AddI ? AddSlow(a, b) : ArithSlow(Op.Sub, a, b);
    }

    /// <summary>The compare-and-jump instructions with a literal on a left operand that is not a number.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool CompareImmSlow(Op op, in JsValue a, int imm)
    {
        var b = new JsValue((double)imm);
        return CompareSlow(CompareOf(op), a, b);
    }

    /// <summary>The comparison a compare-and-jump instruction makes: Lt, Le, Gt or Ge.</summary>
    private static Op CompareOf(Op op) => op switch
    {
        Op.LtJmpFalse or Op.LtIJmpFalse or Op.LtJmpTrue or Op.LtIJmpTrue => Op.Lt,
        Op.LeJmpFalse or Op.LeIJmpFalse or Op.LeJmpTrue or Op.LeIJmpTrue => Op.Le,
        Op.GtJmpFalse or Op.GtIJmpFalse or Op.GtJmpTrue or Op.GtIJmpTrue => Op.Gt,
        _ => Op.Ge,
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
