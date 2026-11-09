using System;
using System.Collections.Generic;
using System.Text;

namespace Jaunt.Compiler;

/// <summary>
/// Register-machine opcodes. Every instruction is one opcode word followed by a fixed number of int operands
/// (except <see cref="Op.SwitchInt"/>, which carries a jump table). Operand kinds are declared in
/// <see cref="OpInfo"/> and checked by <see cref="BytecodeVerifier"/>.
/// </summary>
internal enum Op : int
{
    Nop,
    Mov,
    LoadUndef, LoadNull, LoadTrue, LoadFalse, LoadEmpty,
    LoadInt,
    LoadConst,
    LoadThis,
    LoadNewTarget,
    LoadCallee,
    CheckTdz,
    ThrowConstAssign,
    LdEnv, LdEnvChk, StEnv, StEnvChk,
    PushEnv, PopEnv, CopyEnv,
    GetGlobal, GetGlobalTypeof, SetGlobal, InitGlobalLex,
    GetProp, SetProp, GetElem, SetElem,
    DelProp, DelElem,
    GetSuper, SetSuper, LoadSuperBase,
    DefineProp, DefinePropK, DefineAccessor, DefineMethod, SetProto,
    CopyDataProps, ObjectRest,
    NewObject, NewArray, ArrayPush, ArrayPushHole, ArraySpread,
    Add, Sub, Mul, Div, Mod, Exp, BitAnd, BitOr, BitXor, Shl, Shr, UShr,
    Eq, Ne, StrictEq, StrictNe, Lt, Le, Gt, Ge, InstanceOf, In,
    Neg, Plus, Not, BitNot, Typeof, ToNumeric, Inc, Dec, ToPropertyKey, ToStringOp,
    AddConst, LtJmpFalse, LeJmpFalse, GtJmpFalse, GeJmpFalse, StrictEqJmpFalse, StrictNeJmpFalse,
    /// <summary>Add and Sub whose right operand is a literal int32 in the instruction: dst, a, imm.</summary>
    AddI, SubI,
    /// <summary>The compare-and-jump instructions with a literal int32 as the right operand: a, imm, target.</summary>
    LtIJmpFalse, LeIJmpFalse, GtIJmpFalse, GeIJmpFalse,
    /// <summary>StrictEqJmpFalse and StrictNeJmpFalse with a constant as the right operand: a, constant, target.</summary>
    StrictEqKJmpFalse, StrictNeKJmpFalse,
    Jmp, JmpTrue, JmpFalse, JmpNullish, JmpNotNullish, JmpUndef, JmpNotUndef,
    SwitchInt,
    Ret, RetUndef,
    Throw, ThrowTypeError, ThrowRefError,
    Call, CallMethod, CallSpread, New, NewSpread, SuperCall, SuperCallSpread, SuperBind,
    /// <summary>Calls in tail position (strict code): replace the current frame when the callee is a plain closure.</summary>
    TailCall, TailCallMethod, TailCallSpread, TailCallEval,
    Closure, ClassCreate, SetHomeObject, InitFields, SetFieldInit,
    NewPrivateName, PrivateMethod, PrivateGet, PrivateSet, PrivateDefine, PrivateIn,
    GetIterator, GetIteratorObj, IterNext, IterNextOrUndef, IterRest, IterClose, IterCloseQuiet,
    /// <summary>
    /// Array pattern of plain local targets: src, dst, count — the first count iteration values of src into the registers
    /// from dst on (undefined past the end), then IteratorClose. Equivalent to GetIterator, count IterNextOrUndef, IterClose,
    /// which it replaces when nothing observable can run between the steps (the targets are registers or environment slots).
    /// </summary>
    DestructArray,
    ForInPrepare, ForInNext,
    Yield, YieldRaw, Await, InitialYield, GetAsyncIterator, ThrowIfNotObject,
    PushEnvNamed, PushWith, LdName, LdNameCall, StName, DelName, CallEval, CallEvalSpread, EvalDeclare, EvalInitVar, ResolveRef, LdRef, StRef, CreateMappedArguments, ParseJsonConst, NewDisposeStack, AddDisposable, DisposeSync, DisposeNext, DisposeMerge,
    GetTemplateObject, NewRegExp,
    CreateArguments, CreateRestArray,
    LdImport, ImportMeta, DynamicImport,
    Debugger,
    /// <summary>Calls a rarely used runtime helper (<see cref="Jaunt.Runtime.ClassIntrinsic"/>): dst, id, args start, count.</summary>
    Intrinsic,
    /// <summary>A class field with a constant name: this, name, value, inline cache.</summary>
    DefineFieldK,
    /// <summary>The end of an instance field initializer, which may run on top of its constructor's frame without a result to store (hot returns need no check for that).</summary>
    RetFieldInit,
    Count_,
}

internal enum OperandKind : byte
{
    Reg,       // register index (>= 0)
    OptReg,    // register index or -1
    Const,     // index into Constants
    Object,    // index into Objects
    Func,      // index into Functions
    Imm,       // immediate int
    Jump,      // absolute code offset
    Ic,        // inline cache slot
    Count,     // argument count (paired with preceding Reg start)
}

internal static class OpInfo
{
    private static readonly OperandKind[][] Table = Build();

    public static ReadOnlySpan<OperandKind> Operands(Op op) => Table[(int)op];

    private static OperandKind[][] Build()
    {
        const OperandKind R = OperandKind.Reg, O = OperandKind.OptReg, K = OperandKind.Const, B = OperandKind.Object,
            F = OperandKind.Func, I = OperandKind.Imm, J = OperandKind.Jump, C = OperandKind.Ic, N = OperandKind.Count;
        var t = new OperandKind[(int)Op.Count_][];
        void S(Op op, params OperandKind[] k) => t[(int)op] = k;
        S(Op.Nop);
        S(Op.Mov, R, R);
        S(Op.LoadUndef, R); S(Op.LoadNull, R); S(Op.LoadTrue, R); S(Op.LoadFalse, R); S(Op.LoadEmpty, R);
        S(Op.LoadInt, R, I);
        S(Op.LoadConst, R, K);
        S(Op.LoadThis, R);
        S(Op.LoadNewTarget, R);
        S(Op.LoadCallee, R);
        S(Op.CheckTdz, R, K);
        S(Op.ThrowConstAssign, K);
        S(Op.LdEnv, R, I, I); S(Op.LdEnvChk, R, I, I, K); S(Op.StEnv, I, I, R); S(Op.StEnvChk, I, I, R, K);
        S(Op.PushEnv, I); S(Op.PopEnv); S(Op.CopyEnv);
        S(Op.GetGlobal, R, K, C); S(Op.GetGlobalTypeof, R, K, C); S(Op.SetGlobal, K, R, C); S(Op.InitGlobalLex, K, R);
        S(Op.GetProp, R, R, K, C); S(Op.SetProp, R, K, R, C); S(Op.GetElem, R, R, R, C); S(Op.SetElem, R, R, R, C);
        S(Op.DelProp, R, R, K); S(Op.DelElem, R, R, R);
        S(Op.GetSuper, R, R, R, R); S(Op.SetSuper, R, R, R, R); S(Op.LoadSuperBase, R);
        S(Op.DefineProp, R, R, R); S(Op.DefinePropK, R, K, R, C); S(Op.DefineFieldK, R, K, R, C); S(Op.DefineAccessor, R, R, R, I); S(Op.DefineMethod, R, R, R); S(Op.SetProto, R, R);
        S(Op.CopyDataProps, R, R); S(Op.ObjectRest, R, R, O, N);
        S(Op.NewObject, R, I); S(Op.NewArray, R, I); S(Op.ArrayPush, R, R); S(Op.ArrayPushHole, R); S(Op.ArraySpread, R, R, I);
        foreach (var op in new[] { Op.Add, Op.Sub, Op.Mul, Op.Div, Op.Mod, Op.Exp, Op.BitAnd, Op.BitOr, Op.BitXor, Op.Shl, Op.Shr, Op.UShr,
                     Op.Eq, Op.Ne, Op.StrictEq, Op.StrictNe, Op.Lt, Op.Le, Op.Gt, Op.Ge, Op.InstanceOf, Op.In })
            S(op, R, R, R);
        foreach (var op in new[] { Op.Neg, Op.Plus, Op.Not, Op.BitNot, Op.Typeof, Op.ToNumeric, Op.Inc, Op.Dec, Op.ToPropertyKey, Op.ToStringOp })
            S(op, R, R);
        S(Op.AddConst, R, R, K);
        foreach (var op in new[] { Op.LtJmpFalse, Op.LeJmpFalse, Op.GtJmpFalse, Op.GeJmpFalse, Op.StrictEqJmpFalse, Op.StrictNeJmpFalse }) S(op, R, R, J);
        S(Op.AddI, R, R, I); S(Op.SubI, R, R, I);
        foreach (var op in new[] { Op.LtIJmpFalse, Op.LeIJmpFalse, Op.GtIJmpFalse, Op.GeIJmpFalse }) S(op, R, I, J);
        S(Op.StrictEqKJmpFalse, R, K, J); S(Op.StrictNeKJmpFalse, R, K, J);
        S(Op.Jmp, J); S(Op.JmpTrue, R, J); S(Op.JmpFalse, R, J); S(Op.JmpNullish, R, J); S(Op.JmpNotNullish, R, J);
        S(Op.JmpUndef, R, J); S(Op.JmpNotUndef, R, J);
        S(Op.SwitchInt, R, I); // followed by I jump targets (variable)
        S(Op.Ret, R); S(Op.RetUndef); S(Op.RetFieldInit);
        S(Op.Throw, R); S(Op.ThrowTypeError, K); S(Op.ThrowRefError, K);
        S(Op.Call, R, R, O, R, N); S(Op.TailCall, R, R, O, R, N);
        S(Op.CallMethod, R, R, K, C, R, N); S(Op.TailCallMethod, R, R, K, C, R, N);
        S(Op.CallSpread, R, R, O, R); S(Op.TailCallSpread, R, R, O, R);
        S(Op.New, R, R, R, N);
        S(Op.NewSpread, R, R, R);
        S(Op.SuperCall, R, R, N);
        S(Op.SuperCallSpread, R, R);
        S(Op.SuperBind, R);
        S(Op.Closure, R, F);
        S(Op.ClassCreate, R, R, F, O, I);
        S(Op.SetHomeObject, R, R);
        S(Op.InitFields);
        S(Op.SetFieldInit, R, R);
        S(Op.NewPrivateName, R, K, I);
        S(Op.PrivateMethod, R, R, I);
        S(Op.PrivateGet, R, R, R); S(Op.PrivateSet, R, R, R); S(Op.PrivateDefine, R, R, R); S(Op.PrivateIn, R, R, R);
        S(Op.GetIterator, R, R);
        S(Op.GetIteratorObj, R, R);
        S(Op.IterNext, R, R, J);
        S(Op.IterNextOrUndef, R, R);
        S(Op.IterRest, R, R);
        S(Op.IterClose, R); S(Op.IterCloseQuiet, R);
        S(Op.DestructArray, R, R, N);   // the count is checked as a register window from the second operand
        S(Op.ForInPrepare, R, R); S(Op.ForInNext, R, R, J);
        S(Op.Yield, R, R, R); S(Op.YieldRaw, R, R, R); S(Op.Await, R, R, R); S(Op.InitialYield, R, R);
        S(Op.GetAsyncIterator, R, R); S(Op.ThrowIfNotObject, R, K);
        S(Op.PushEnvNamed, I, B); S(Op.PushWith, R);
        S(Op.LdName, R, K, I); S(Op.LdNameCall, R, R, K); S(Op.StName, K, R, I); S(Op.DelName, R, K);
        S(Op.CallEval, R, R, O, R, N, I); S(Op.TailCallEval, R, R, O, R, N, I); S(Op.CallEvalSpread, R, R, O, R, I); S(Op.EvalDeclare, B); S(Op.EvalInitVar, K, R, I);
        S(Op.CreateMappedArguments, R, B); S(Op.ParseJsonConst, R, K);
        S(Op.NewDisposeStack, R); S(Op.AddDisposable, R, R, I); S(Op.DisposeSync, R, R, R); S(Op.DisposeNext, R, R, R, J); S(Op.DisposeMerge, R, R, R); S(Op.ResolveRef, R, K); S(Op.LdRef, R, R); S(Op.StRef, R, R, I);
        S(Op.GetTemplateObject, R, B, I);
        S(Op.NewRegExp, R, K, K, C);
        S(Op.CreateArguments, R);
        S(Op.CreateRestArray, R, I);
        S(Op.LdImport, R, I, K);
        S(Op.ImportMeta, R);
        S(Op.DynamicImport, R, R, O, I);
        S(Op.Debugger);
        S(Op.Intrinsic, R, I, R, N);
        for (int i = 0; i < t.Length; i++) t[i] ??= Array.Empty<OperandKind>();
        return t;
    }

    public static int Length(int[] code, int pc)
    {
        var op = (Op)code[pc];
        int n = 1 + Table[(int)op].Length;
        if (op == Op.SwitchInt) n += code[pc + 2];
        return n;
    }
}

[Flags]
internal enum FunctionFlags
{
    None = 0,
    Strict = 1,
    Arrow = 2,
    Method = 4,
    ClassConstructor = 8,
    Derived = 16,
    Generator = 32,
    Async = 64,
    UsesThis = 128,
    UsesArguments = 256,
    HasRest = 512,
    IsGetter = 1024,
    IsSetter = 2048,
    NoPrototype = 4096,
    ScriptBody = 8192,
    ModuleBody = 16384,
    FieldInitializer = 32768,
    NeedsArgsArray = 65536,
    ClassStaticInit = 131072,
}

internal struct HandlerEntry
{
    public int Start;
    public int End;
    public int Target;
    public int ExceptionReg;
    public int EnvDepth;
}

internal struct PositionEntry
{
    public int Pc;
    public int Line;
    public int Column;
}

/// <summary>Realm-independent template object description for tagged template sites.</summary>
internal sealed class TemplateSite
{
    public required string?[] Cooked;
    public required string[] Raw;
}

/// <summary>
/// Immutable compiled function. Contains only realm-independent data (code, primitive constants, nested
/// templates, source mapping). Safe to share between engines and threads.
/// </summary>
internal sealed class FunctionTemplate
{
    public string Name = "";
    public int Length;            // formal parameter count for .length
    public int ParamCount;        // register slots used by incoming arguments
    /// <summary>Registers [args, InitUndefEnd) start as undefined on entry (function-scope bindings such as hoisted vars
    /// and missing parameters); every register at or above it is written before it is read. -1: initialize all.</summary>
    public int InitUndefEnd = -1;
    public int RegisterCount;
    public int[] Code = Array.Empty<int>();
    public JsValue[] Constants = Array.Empty<JsValue>();
    public object[] Objects = Array.Empty<object>();
    public FunctionTemplate[] Functions = Array.Empty<FunctionTemplate>();
    public HandlerEntry[] Handlers = Array.Empty<HandlerEntry>();
    public PositionEntry[] Positions = Array.Empty<PositionEntry>();
    public FunctionFlags Flags;
    public int IcCount;
    public int SourceStart;
    public int SourceEnd;
    public ScriptTemplate Script = null!;
    /// <summary>Field initializer index into <see cref="Functions"/> for classes, or -1.</summary>
    public int RestIndex = -1;

    public bool IsStrict => (Flags & FunctionFlags.Strict) != 0;
    public bool IsArrow => (Flags & FunctionFlags.Arrow) != 0;
    public bool IsGenerator => (Flags & FunctionFlags.Generator) != 0;
    public bool IsAsync => (Flags & FunctionFlags.Async) != 0;
    public bool IsClassConstructor => (Flags & FunctionFlags.ClassConstructor) != 0;
    public bool IsDerived => (Flags & FunctionFlags.Derived) != 0;
    public bool IsConstructor => (Flags & (FunctionFlags.Arrow | FunctionFlags.Method | FunctionFlags.Generator | FunctionFlags.Async | FunctionFlags.IsGetter | FunctionFlags.IsSetter)) == 0 || IsClassConstructor;

    public (int line, int column) GetPosition(int pc)
    {
        var p = Positions;
        int lo = 0, hi = p.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (p[mid].Pc <= pc) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (best < 0) return p.Length > 0 ? (p[0].Line, p[0].Column) : (0, 0);
        return (p[best].Line, p[best].Column);
    }

    public string Disassemble()
    {
        var sb = new StringBuilder();
        sb.Append("function ").Append(Name).Append(" regs=").Append(RegisterCount).Append(" params=").Append(ParamCount).AppendLine();
        int pc = 0;
        while (pc < Code.Length)
        {
            var op = (Op)Code[pc];
            sb.Append(pc.ToString().PadLeft(5)).Append(": ").Append(op);
            var kinds = OpInfo.Operands(op);
            for (int i = 0; i < kinds.Length; i++)
            {
                int v = Code[pc + 1 + i];
                sb.Append(' ');
                switch (kinds[i])
                {
                    case OperandKind.Reg: sb.Append('r').Append(v); break;
                    case OperandKind.OptReg: sb.Append(v < 0 ? "_" : "r" + v); break;
                    case OperandKind.Const: sb.Append("k").Append(v).Append('(').Append(Constants[v].ToString()).Append(')'); break;
                    case OperandKind.Jump: sb.Append("->").Append(v); break;
                    default: sb.Append(v); break;
                }
            }
            if (op == Op.SwitchInt)
            {
                int n = Code[pc + 2];
                for (int i = 0; i < n; i++) sb.Append(" ->").Append(Code[pc + 3 + i]);
            }
            sb.AppendLine();
            pc += OpInfo.Length(Code, pc);
        }
        foreach (var h in Handlers) sb.Append("  try [").Append(h.Start).Append(',').Append(h.End).Append(") -> ").Append(h.Target).Append(" exc=r").Append(h.ExceptionReg).AppendLine();
        foreach (var f in Functions) sb.AppendLine().Append(f.Disassemble());
        return sb.ToString();
    }
}

/// <summary>Top-level compiled unit (script or module body).</summary>
internal sealed class ScriptTemplate
{
    public required FunctionTemplate Body;
    public required string SourceName;
    public string? Source;
    public bool IsModule;
    public bool Strict;
    /// <summary>Eval code: sloppy top-level var/function named "arguments".</summary>
    public bool EvalDeclaresArguments;
    /// <summary>Eval code: references "arguments" outside nested non-arrow functions.</summary>
    public bool EvalContainsArguments;
    /// <summary>Eval code: private names ("#x") it references that must be visible at the eval call site.</summary>
    public string[]? EvalPrivateNames;
    // Script global declaration instantiation
    public string[] VarNames = Array.Empty<string>();
    public string[] FunctionNames = Array.Empty<string>();
    public string[] LexNames = Array.Empty<string>();
    public bool[] LexIsConst = Array.Empty<bool>();
    // Module records
    public ModuleRecordInfo? Module;
    public int FunctionCount;
    public int IcTotal;
}

/// <summary>Import phase of a module request (source / defer phase import proposals).</summary>
internal enum ModulePhase : byte { Evaluation, Defer, Source }

internal sealed class ImportEntryInfo
{
    public required string ModuleRequest;
    /// <summary>
    /// Imported name, or null for a whole-module import: the namespace (<c>import * as x</c>), the deferred namespace
    /// (<c>import defer * as x</c>) or the module source object (<c>import source x</c>), per <see cref="Phase"/>.
    /// </summary>
    public string? ImportName;
    public required string LocalName;
    public ModulePhase Phase;
}

internal sealed class ExportEntryInfo
{
    public string? ExportName;   // null for export * from
    public string? LocalName;    // local binding name (when local export)
    public string? ModuleRequest;
    public string? ImportName;   // for re-exports; null = namespace (export * as ns), deferred namespace or module source (per Phase)
    public ModulePhase Phase;    // phase of the re-exported whole-module import (ImportName == null)
    public int LocalEnvIndex = -1;
}

internal sealed class ModuleRecordInfo
{
    /// <summary>Module request keys in source order; a key appears once per distinct phase (see <see cref="RequestedPhases"/>).</summary>
    public List<string> RequestedModules = new();
    /// <summary>Import phase of each <see cref="RequestedModules"/> entry (parallel list).</summary>
    public List<ModulePhase> RequestedPhases = new();
    /// <summary>Slot names of the module environment (only when direct eval needs them).</summary>
    public Jaunt.Runtime.ScopeLayout? Layout;
    public List<ImportEntryInfo> Imports = new();
    public List<ExportEntryInfo> LocalExports = new();
    public List<ExportEntryInfo> IndirectExports = new();
    public List<ExportEntryInfo> StarExports = new();
    public bool HasTopLevelAwait;
    /// <summary>Synthetic JSON module (import ... with { type: "json" }).</summary>
    public bool IsJson;
    /// <summary>Synthetic text module (import ... with { type: "text" }).</summary>
    public bool IsText;
    public bool IsBytes;
    /// <summary>Host module with a Module Source object (source phase imports): its GetModuleSourceKind() name.</summary>
    public string? SourceKind;
    public int EnvSize;
    /// <summary>Module env slot for each local binding name.</summary>
    public Dictionary<string, int> EnvSlots = new();
    /// <summary>Hoisted function declarations: name → (functionIndex, envSlot) to instantiate at link time.</summary>
    public List<(int FunctionIndex, int Slot)> HoistedFunctions = new();
    /// <summary>Env slots for lexical (TDZ) bindings.</summary>
    public List<int> LexicalSlots = new();
}
