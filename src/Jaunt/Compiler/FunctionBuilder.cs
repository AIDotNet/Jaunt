using System;
using System.Collections.Generic;
using Acornima.Ast;

namespace Jaunt.Compiler;

/// <summary>Accumulates bytecode for a single function during code generation.</summary>
internal sealed class FunctionBuilder
{
    public readonly List<int> Code = new(64);
    private readonly List<JsValue> _constants = new();
    private readonly Dictionary<string, int> _stringConsts = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _numberConsts = new();
    public readonly List<object> Objects = new();
    public readonly List<FunctionTemplate> Functions = new();
    public readonly List<HandlerEntry> Handlers = new();
    public readonly List<PositionEntry> Positions = new();
    private readonly List<int> _labelPos = new();
    private readonly List<(int at, int label)> _patches = new();
    public int NextReg;
    public int MaxReg;
    public int IcCount;
    private int _lastLine = -1, _lastCol = -1;

    public int Pc => Code.Count;

    // Builders are pooled per thread: compiling allocates the output arrays only (Finish copies them out).
    [ThreadStatic] private static Stack<FunctionBuilder>? t_pool;

    public static FunctionBuilder Rent()
    {
        var pool = t_pool;
        if (pool != null && pool.Count > 0) return pool.Pop();
        return new FunctionBuilder();
    }

    /// <summary>Clears the builder and returns it to the pool. The builder must not be used afterwards.</summary>
    public void Return()
    {
        if (Code.Capacity > 1 << 16) return; // do not keep unusually large buffers alive
        Code.Clear();
        _constants.Clear();
        _stringConsts.Clear();
        _numberConsts.Clear();
        Objects.Clear();
        Functions.Clear();
        Handlers.Clear();
        Positions.Clear();
        _labelPos.Clear();
        _patches.Clear();
        NextReg = MaxReg = IcCount = 0;
        _lastLine = _lastCol = -1;
        var pool = t_pool ??= new Stack<FunctionBuilder>();
        if (pool.Count < 64) pool.Push(this);
    }

    public int AllocReg()
    {
        int r = NextReg++;
        if (NextReg > MaxReg) MaxReg = NextReg;
        if (NextReg > 65535) throw new CompileError("TooManyRegisters", "Function requires too many registers.", null);
        return r;
    }

    public int NewIc() => IcCount++;

    public int Const(string s)
    {
        if (_stringConsts.TryGetValue(s, out int i)) return i;
        i = _constants.Count;
        _constants.Add(new JsValue(s));
        _stringConsts[s] = i;
        return i;
    }

    public int ConstValue(JsValue v)
    {
        _constants.Add(v);
        return _constants.Count - 1;
    }

    public int Const(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        if (_numberConsts.TryGetValue(bits, out int i)) return i;
        i = _constants.Count;
        _constants.Add(new JsValue(d));
        _numberConsts[bits] = i;
        return i;
    }

    public int AddObject(object o)
    {
        Objects.Add(o);
        return Objects.Count - 1;
    }

    public int AddFunction(FunctionTemplate t)
    {
        Functions.Add(t);
        return Functions.Count - 1;
    }

    public int NewLabel()
    {
        _labelPos.Add(-1);
        return _labelPos.Count - 1;
    }

    public void Mark(int label) => _labelPos[label] = Code.Count;

    public bool IsMarked(int label) => _labelPos[label] >= 0;

    public void Emit(Op op) => Code.Add((int)op);
    public void Emit(Op op, int a) { Code.Add((int)op); Code.Add(a); }
    public void Emit(Op op, int a, int b) { Code.Add((int)op); Code.Add(a); Code.Add(b); }
    public void Emit(Op op, int a, int b, int c) { Code.Add((int)op); Code.Add(a); Code.Add(b); Code.Add(c); }
    public void Emit(Op op, int a, int b, int c, int d) { Code.Add((int)op); Code.Add(a); Code.Add(b); Code.Add(c); Code.Add(d); }
    public void Emit(Op op, int a, int b, int c, int d, int e) { Code.Add((int)op); Code.Add(a); Code.Add(b); Code.Add(c); Code.Add(d); Code.Add(e); }
    public void Emit(Op op, int a, int b, int c, int d, int e, int f) { Code.Add((int)op); Code.Add(a); Code.Add(b); Code.Add(c); Code.Add(d); Code.Add(e); Code.Add(f); }

    /// <summary>Emits a label reference operand (patched at finish).</summary>
    public void LabelRef(int label)
    {
        _patches.Add((Code.Count, label));
        Code.Add(-1);
    }

    public void Jump(int label) { Emit(Op.Jmp); LabelRef(label); }
    public void JumpIf(Op op, int reg, int label) { Code.Add((int)op); Code.Add(reg); LabelRef(label); }
    public void JumpCmp(Op op, int a, int b, int label) { Code.Add((int)op); Code.Add(a); Code.Add(b); LabelRef(label); }

    public void Position(Node node)
    {
        var loc = node.Location.Start;
        if (loc.Line == _lastLine && loc.Column == _lastCol) return;
        _lastLine = loc.Line;
        _lastCol = loc.Column;
        Positions.Add(new PositionEntry { Pc = Code.Count, Line = loc.Line, Column = loc.Column + 1 });
    }

    public int LabelPosition(int label) => _labelPos[label];

    public FunctionTemplate Finish(FunctionTemplate t)
    {
        foreach (var (at, label) in _patches)
        {
            int pos = _labelPos[label];
            if (pos < 0) throw new InvalidOperationException("Unmarked label in bytecode.");
            Code[at] = pos;
        }
        t.Code = Code.ToArray();
        t.Constants = _constants.ToArray();
        t.Objects = Objects.ToArray();
        t.Functions = Functions.ToArray();
        t.Handlers = Handlers.ToArray();
        t.Positions = Positions.ToArray();
        t.RegisterCount = Math.Max(MaxReg, 1);
        t.IcCount = IcCount;
        return t;
    }
}
