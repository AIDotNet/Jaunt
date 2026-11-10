using System;
using System.Collections.Generic;

namespace Jaunt.Compiler;

/// <summary>
/// Structural verification of a compiled function: every opcode is known, operands are in range, jump targets
/// land on instruction boundaries and handler ranges are valid. Run once per template at compile time; the VM
/// relies on these invariants and does not re-check them in the hot loop.
/// </summary>
internal static class BytecodeVerifier
{
    [ThreadStatic] private static bool[]? s_starts;
    [ThreadStatic] private static List<int>? s_jumps;

    /// <summary>
    /// Sets <see cref="FunctionFlags.NoThisBinding"/> on verified code without an instruction that reads the frame's this binding,
    /// new.target or ThisBox. Listed by what can: the instructions themselves, and those that give the binding to code they create or run.
    /// </summary>
    public static void MarkThisBinding(FunctionTemplate t)
    {
        var code = t.Code;
        for (int pc = 0; pc < code.Length; pc += OpInfo.Length(code, pc))
        {
            switch ((Op)code[pc])
            {
                case Op.LoadThis:
                case Op.LoadNewTarget:
                case Op.Closure:
                case Op.ClassCreate:
                case Op.SuperCall:
                case Op.SuperCallSpread:
                case Op.SuperBind:
                case Op.CallEval:
                case Op.CallEvalSpread:
                case Op.TailCallEval:
                case Op.InitFields:
                    return;
            }
        }
        t.Flags |= FunctionFlags.NoThisBinding;
    }

    public static void Verify(FunctionTemplate t)
    {
        var code = t.Code;
        // scratch buffers are reused per thread (verification runs for every compiled function)
        int limit = code.Length + 1;
        var starts = s_starts;
        if (starts == null || starts.Length < limit)
        {
            starts = new bool[Math.Max(limit, 256)];
            if (limit <= 1 << 16) s_starts = starts;   // don't pin huge buffers per thread
        }
        else Array.Clear(starts, 0, limit);
        var jumps = s_jumps ??= new List<int>();
        jumps.Clear();
        int pc = 0, last = 0;
        while (pc < code.Length)
        {
            last = pc;
            starts[pc] = true;
            int opv = code[pc];
            if (opv < 0 || opv >= (int)Op.Count_) Fail(t, pc, "unknown opcode");
            var op = (Op)opv;
            var kinds = OpInfo.Operands(op);
            if (pc + kinds.Length >= code.Length) Fail(t, pc, "truncated instruction");
            for (int i = 0; i < kinds.Length; i++)
            {
                int v = code[pc + 1 + i];
                switch (kinds[i])
                {
                    case OperandKind.Reg:
                        if (v < 0 || v > t.RegisterCount || (v == t.RegisterCount && !(i + 1 < kinds.Length && kinds[i + 1] == OperandKind.Count)))
                            Fail(t, pc, "register out of range");
                        break;
                    case OperandKind.OptReg: if (v < -1 || v >= t.RegisterCount) Fail(t, pc, "register out of range"); break;
                    case OperandKind.Const: if (v < 0 || v >= t.Constants.Length) Fail(t, pc, "constant out of range"); break;
                    case OperandKind.Object: if (v < 0 || v >= t.Objects.Length) Fail(t, pc, "object out of range"); break;
                    case OperandKind.Func: if (v < 0 || v >= t.Functions.Length) Fail(t, pc, "function out of range"); break;
                    case OperandKind.Ic: if (v < 0 || v >= t.IcCount) Fail(t, pc, "ic out of range"); break;
                    case OperandKind.Jump: jumps.Add(v); break;
                    case OperandKind.Count:
                        if (v < 0) Fail(t, pc, "negative count");
                        if (v > 0)
                        {
                            int start = code[pc + i];
                            if (start < 0 || start + v > t.RegisterCount) Fail(t, pc, "argument window out of range");
                        }
                        break;
                }
            }
            if (op == Op.SwitchInt)
            {
                int n = code[pc + 2];
                if (n < 0 || pc + 3 + n > code.Length) Fail(t, pc, "bad switch");
                for (int i = 0; i < n; i++) jumps.Add(code[pc + 3 + i]);
            }
            pc += OpInfo.Length(code, pc);
        }
        if (pc != code.Length) Fail(t, pc, "instruction overruns code");
        foreach (var j in jumps) if ((uint)j >= (uint)limit || !starts[j]) Fail(t, j, "jump target is not an instruction boundary");
        foreach (var h in t.Handlers)
        {
            if (h.Start < 0 || h.End < h.Start || h.End > code.Length || (uint)h.Target >= (uint)limit || !starts[h.Target]) Fail(t, h.Target, "bad handler");
            if (h.ExceptionReg < 0 || h.ExceptionReg >= t.RegisterCount) Fail(t, h.Target, "bad handler register");
        }
        if (code.Length == 0 || !IsTerminator((Op)code[last])) Fail(t, code.Length, "function does not end with a terminator");
    }

    private static bool IsTerminator(Op op) => op is Op.Ret or Op.RetUndef or Op.RetFieldInit or Op.Throw or Op.Jmp or Op.ThrowTypeError or Op.ThrowRefError or Op.SwitchInt or Op.ThrowConstAssign;

    private static void Fail(FunctionTemplate t, int pc, string message) =>
        throw new InvalidOperationException($"Jaunt bytecode verification failed in '{t.Name}' at {pc}: {message}.");
}
