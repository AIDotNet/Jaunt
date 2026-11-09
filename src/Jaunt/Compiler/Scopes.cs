using System;
using System.Collections.Generic;
using Acornima.Ast;

namespace Jaunt.Compiler;

internal enum BindingKind
{
    Var,
    Let,
    Const,
    Class,
    Function,
    Param,
    CatchParam,
    Arguments,
    FuncName,
    Import,
    ClassInner,
    PrivateName,
    Synthetic,
}

internal sealed class Binding
{
    public required string Name;
    public required BindingKind Kind;
    public required Scope Scope;
    public bool Captured;
    public bool NeedsTdz;
    public int DeclEnd;
    public int Register = -1;
    public int EnvSlot = -1;
    public ImportEntryInfo? Import;
    public int ImportIndex = -1;
    /// <summary>For Annex B block functions in sloppy mode: the var binding to copy into.</summary>
    public Binding? AnnexBVar;
    public int ParamIndex = -1;

    /// <summary>Parameter of a function with a non-simple parameter list: in TDZ until its initializer ran.</summary>
    public bool ParamTdz;
    /// <summary>The arguments binding of a function with <see cref="FunctionInfo.ArgumentsElided"/>.</summary>
    public bool ArgumentsElided;
    /// <summary>Top-level var/function of sloppy eval code: lives in the caller's variable environment (name lookup).</summary>
    public bool IsEvalDynamic;
    public bool HasTdz => IsLexical || ParamTdz;
    public bool IsLexical => Kind is BindingKind.Let or BindingKind.Const or BindingKind.Class or BindingKind.ClassInner;
    public bool IsConst => Kind is BindingKind.Const or BindingKind.ClassInner or BindingKind.Import or BindingKind.PrivateName;
    public bool IsVarLike => Kind is BindingKind.Var or BindingKind.Function or BindingKind.Param or BindingKind.Arguments;
}

internal enum ScopeKind
{
    Script,
    Module,
    Function,
    Block,
    Catch,
    For,
    Switch,
    Class,
    /// <summary>Object environment of a <c>with</c> statement (no bindings).</summary>
    With,
}

internal sealed class Scope
{
    public required ScopeKind Kind;
    public Scope? Parent;
    public required FunctionInfo Function;
    // collections are created on first declaration: most block scopes are tiny or empty
    private Dictionary<string, Binding>? _map;
    private List<Binding>? _ordered;
    private List<FunctionDeclaration>? _hoisted;
    private static readonly List<Binding> s_noBindings = new();
    public List<Binding> Ordered => _ordered ?? s_noBindings;
    public bool HasEnv;
    /// <summary>Slot names are emitted so direct eval / with code can find bindings by name.</summary>
    public bool Named;
    /// <summary>Names not found in this scope must be looked up dynamically (with object, eval-created vars).</summary>
    public bool Barrier;
    /// <summary>An environment is created even without captured bindings (sloppy direct-eval var target).</summary>
    public bool ForceEnv;
    public int EnvSize;
    public int LexStart;
    /// <summary>Function declarations to instantiate at scope entry (hoisted).</summary>
    public IReadOnlyList<FunctionDeclaration> HoistedFunctions => (IReadOnlyList<FunctionDeclaration>?)_hoisted ?? Array.Empty<FunctionDeclaration>();
    public void AddHoisted(FunctionDeclaration fd) => (_hoisted ??= new List<FunctionDeclaration>()).Add(fd);
    public void Remove(Binding b) { _map?.Remove(b.Name); _ordered?.Remove(b); }

    /// <summary>Removes every binding matching <paramref name="match"/> in one pass (Remove per binding is O(n) each).</summary>
    public void RemoveAll(Predicate<Binding> match)
    {
        if (_ordered == null) return;
        if (_map != null) foreach (var b in _ordered) if (match(b)) _map.Remove(b.Name);
        _ordered.RemoveAll(match);
    }

    public bool IsGlobalScript => Kind == ScopeKind.Script;

    public Binding? Lookup(string name)
    {
        var o = _ordered;
        if (o == null) return null;
        if (_map == null)
        {
            // small scopes: linear search beats hashing
            for (int i = o.Count - 1; i >= 0; i--) if (string.Equals(o[i].Name, name, StringComparison.Ordinal)) return o[i];
            return null;
        }
        return _map.TryGetValue(name, out var b) ? b : null;
    }

    public Binding Declare(string name, BindingKind kind, int declEnd)
    {
        var b = new Binding { Name = name, Kind = kind, Scope = this, DeclEnd = declEnd };
        var o = _ordered ??= new List<Binding>(4);
        if (_map != null) _map[name] = b;
        else
        {
            // lookups search from the end, so a redeclared name resolves to the latest binding (as with the map)
            if (o.Count >= 8)
            {
                _map = new Dictionary<string, Binding>(StringComparer.Ordinal);
                foreach (var x in o) _map[x.Name] = x;
                _map[name] = b;
            }
        }
        o.Add(b);
        return b;
    }
}

internal sealed class FunctionInfo
{
    public Node? Node;
    public FunctionInfo? Parent;
    public Scope Scope = null!;
    /// <summary>
    /// Variable scope of the body. Equals <see cref="Scope"/> unless the function has parameter expressions, in which
    /// case body declarations live in a separate environment (10.2.11 step 28).
    /// </summary>
    public Scope? BodyScope;
    public Scope VarScope => BodyScope ?? Scope;
    public bool IsArrow;
    public bool Strict;
    public bool IsScriptTop;
    public bool IsModuleTop;
    public bool IsSynthetic;
    public Binding? ArgumentsBinding;
    public Binding? FuncNameBinding;
    public bool UsesThis;
    public bool UsesArguments;
    public bool HasSuperCall;
    public bool ContainsAwait;
    public bool HasDirectEval;
    public bool IsFieldInit;
    /// <summary>Sloppy function with a simple parameter list whose arguments object aliases the parameters.</summary>
    public bool MappedArguments;
    /// <summary>
    /// A reference to the arguments binding other than a plain read of <c>arguments.length</c> or <c>arguments[key]</c>, a
    /// <c>var arguments</c>, or a direct eval that could name it: the object has to exist from the start.
    /// </summary>
    public bool ArgumentsEscapes;
    /// <summary>Some read needs the argument values, not only their count.</summary>
    public bool ArgumentsIndexed;
    /// <summary>
    /// No arguments object is created on entry: its only uses are answered from the frame (Op.ArgCount, Op.ArgLength, Op.ArgElem).
    /// Parameters stay in registers. When ArgumentsIndexed is set the binding's register starts as undefined and holds the object
    /// if a read that is not a plain element of the argument list (a key on Object.prototype, say) needed it; user code can only
    /// reach it from there through an accessor's receiver, and such an object could not alias registers, so a function with
    /// aliased parameters (mapped, with parameters) is only elided when it reads nothing but the length.
    /// </summary>
    public bool ArgumentsElided;
    public bool IsEvalTop;
    /// <summary>Sloppy eval code: names of top-level var / function declarations (declared in the caller's var env).</summary>
    public List<string>? EvalVarNames;
    public List<string>? EvalFunctionNames;
}

internal struct ResolvedRef
{
    public Binding? Binding;   // null → global (or dynamic)
    public bool NeedsTdzCheck;
    /// <summary>Resolved at run time by name (inside with, after sloppy direct eval, or in eval code).</summary>
    public bool Dynamic;

    // Every identifier of a compilation is resolved once and read at least once: the result rides on the node instead of in a
    // table. The usual result, a plain binding, is the Binding itself and allocates nothing, as is an unresolved global.
    private sealed class Box { public ResolvedRef Value; }
    private static readonly Box s_global = new();

    public static void Set(Identifier id, in ResolvedRef r)
    {
        if (r.NeedsTdzCheck || r.Dynamic) id.UserData = new Box { Value = r };
        else id.UserData = (object?)r.Binding ?? s_global;
    }

    public static bool TryGet(Identifier id, out ResolvedRef r)
    {
        var u = id.UserData;
        if (u is Binding b) { r = new ResolvedRef { Binding = b }; return true; }
        if (u is Box x) { r = x.Value; return true; }
        r = default;
        return false;
    }

    public static ResolvedRef Get(Identifier id) =>
        TryGet(id, out var r) ? r : throw new InvalidOperationException($"Jaunt internal error: identifier '{id.Name}' was not analyzed.");
}

/// <summary>
/// What the analysis attaches to a syntax node that owns a scope or a function, carried on the node itself: the AST lives for
/// one compilation and is never shared. A function node's scope is its <see cref="FunctionInfo.Scope"/>.
/// </summary>
internal static class NodeTable
{
    public static void SetFunction(Node node, FunctionInfo function) => node.UserData = function;

    public static void SetScope(Node node, Scope scope)
    {
        if (node.UserData is FunctionInfo f)
        {
            if (!ReferenceEquals(f.Scope, scope)) throw new InvalidOperationException("Jaunt internal error: a function node with a scope of its own.");
            return;
        }
        node.UserData = scope;
    }

    public static bool TryGetFunction(Node node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FunctionInfo? function)
    {
        function = node.UserData as FunctionInfo;
        return function != null;
    }

    public static bool TryGetScope(Node node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Scope? scope)
    {
        var u = node.UserData;
        scope = u as Scope ?? (u as FunctionInfo)?.Scope;
        return scope != null;
    }

    public static FunctionInfo Function(Node node) =>
        node.UserData as FunctionInfo ?? throw new InvalidOperationException("Jaunt internal error: a function node was not analyzed.");

    public static Scope Scope(Node node) =>
        TryGetScope(node, out var s) ? s : throw new InvalidOperationException("Jaunt internal error: a scope node was not analyzed.");
}

/// <summary>Thrown for unsupported features and early errors detected by Jaunt itself.</summary>
internal sealed class CompileError : Exception
{
    public string Code { get; }
    public int Line { get; }
    public int Column { get; }
    public CompileError(string code, string message, Node? node) : base(message)
    {
        Code = code;
        if (node != null)
        {
            Line = node.Location.Start.Line;
            Column = node.Location.Start.Column + 1;
        }
    }
}

/// <summary>
/// First compiler pass: builds the scope tree, hoists declarations, resolves identifier references and
/// decides which bindings are captured by closures (and therefore live in heap environments instead of registers).
/// </summary>
internal sealed class ScopeAnalyzer
{
    private Scope _scope = null!;
    private FunctionInfo _func = null!;
    private readonly ModuleRecordInfo? _module;
    private int _depth;
    private readonly int _maxDepth;
    private readonly Action? _checkpoint;

    public ScopeAnalyzer(ModuleRecordInfo? module, int maxDepth, Action? checkpoint)
    {
        _module = module;
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

    // ------------------------------------------------------------------ entry points

    private HashSet<Node>? _evalFunctions;
    public bool EvalContainsArguments;
    public HashSet<string>? EvalPrivateNames;
    private bool _evalCode;

    /// <summary>
    /// Finds the functions (or the program) whose own code contains a direct <c>eval(...)</c> call. Only these and
    /// code under <c>with</c> take the conservative named-environment path; everything else is compiled as usual.
    /// </summary>
    public bool MayContainEval = true;

    private void Prepass(Node root)
    {
        if (!MayContainEval) return;
        var stack = new Stack<(Node node, Node fn)>();
        stack.Push((root, root));
        while (stack.Count > 0)
        {
            var (n, fn) = stack.Pop();
            if (n is CallExpression { Callee: Identifier { Name: "eval" }, Optional: false })
                (_evalFunctions ??= new HashSet<Node>()).Add(fn);
            var inner = n is IFunction && !ReferenceEquals(n, root) ? n : fn;
            foreach (var c in n.ChildNodes)
                if (c != null) stack.Push((c, inner));
        }
    }

    private void MarkEvalFunction(Node node, Scope scope, FunctionInfo fi)
    {
        if (_evalFunctions == null || !_evalFunctions.Contains(node)) return;
        fi.HasDirectEval = true;
        scope.Barrier = true;
        scope.Named = true;
        scope.ForceEnv = true;
    }

    /// <summary>A direct eval can read every binding visible at the call site by name.</summary>
    private void MarkEvalSite()
    {
        var fn = _func;
        while (fn.IsArrow && !fn.IsEvalTop && fn.Parent != null) fn = fn.Parent;
        fn.ArgumentsEscapes = true;
        if (!fn.IsScriptTop && !fn.IsModuleTop && !fn.IsSynthetic && !fn.IsArrow && !fn.IsEvalTop && fn.Node is not null
            && fn.Scope.Lookup("arguments") == null)
        {
            fn.ArgumentsBinding = fn.Scope.Declare("arguments", BindingKind.Arguments, 0);
            fn.UsesArguments = true;
        }
        for (var s = _scope; s != null; s = s.Parent)
        {
            s.Named = true;
            foreach (var b in s.Ordered)
            {
                if (b.Kind == BindingKind.Import) continue;
                b.Captured = true;
                if (b.HasTdz) b.NeedsTdz = true;
            }
        }
    }

    /// <summary>Analyzes eval code. Unresolved names and (sloppy) top-level vars are looked up by name at run time.</summary>
    public FunctionInfo AnalyzeEval(Program program, bool strict)
    {
        strict |= program.Strict;
        _evalCode = true;
        Prepass(program);
        var fi = new FunctionInfo { Node = program, IsEvalTop = true, IsArrow = true, Strict = strict };
        var scope = new Scope { Kind = ScopeKind.Function, Function = fi, Named = true, ForceEnv = true, Barrier = true };
        fi.Scope = scope;
        NodeTable.SetFunction(program, fi);
        NodeTable.SetScope(program, scope);
        _func = fi;
        _scope = scope;
        HoistVarDeclarations(program.Body, scope, fi);
        DeclareLexical(program.Body, scope, topLevelFunctionsAreVar: true);
        if (!strict)
        {
            fi.EvalVarNames = new List<string>();
            fi.EvalFunctionNames = new List<string>();
            foreach (var b in scope.Ordered)
            {
                if (!b.IsVarLike) continue;
                b.IsEvalDynamic = true;
                _evalRemovedVars[b.Name] = b;
                (b.Kind == BindingKind.Function ? fi.EvalFunctionNames : fi.EvalVarNames).Add(b.Name);
            }
            scope.RemoveAll(static b => b.IsVarLike);   // one pass: removing one by one was quadratic in the var count
        }
        foreach (var st in program.Body) VisitStatement(st);
        return fi;
    }

    public FunctionInfo AnalyzeProgram(Program program, bool isModule)
    {
        Prepass(program);
        var fi = new FunctionInfo { Node = program, IsScriptTop = !isModule, IsModuleTop = isModule, Strict = isModule || program.Strict };
        var scope = new Scope { Kind = isModule ? ScopeKind.Module : ScopeKind.Script, Function = fi };
        fi.Scope = scope;
        NodeTable.SetFunction(program, fi);
        NodeTable.SetScope(program, scope);
        _func = fi;
        _scope = scope;
        MarkEvalFunction(program, scope, fi);
        HoistVarDeclarations(program.Body, scope, fi);
        DeclareLexical(program.Body, scope, topLevelFunctionsAreVar: true);
        if (isModule) DeclareImports(program);
        foreach (var st in program.Body) VisitStatement(st);
        if (isModule)
        {
            // every module top-level binding lives in the module environment
            foreach (var b in scope.Ordered) b.Captured = true;
        }
        return fi;
    }

    private void DeclareImports(Program program)
    {
        foreach (var st in program.Body)
        {
            if (st is not ImportDeclaration imp) continue;
            foreach (var spec in imp.Specifiers)
            {
                var local = spec.Local.Name;
                var info = new ImportEntryInfo
                {
                    ModuleRequest = JsCompiler.StaticRequestKey(imp.Source, imp.Attributes),
                    LocalName = local,
                    Phase = imp.Phase switch { ImportPhase.Defer => ModulePhase.Defer, ImportPhase.Source => ModulePhase.Source, _ => ModulePhase.Evaluation },
                    ImportName = imp.Phase != ImportPhase.None ? null : spec switch
                    {
                        ImportDefaultSpecifier => "default",
                        ImportNamespaceSpecifier => null,
                        ImportSpecifier s => ModuleExportName(s.Imported),
                        _ => throw new CompileError("Unsupported", "Unsupported import specifier.", spec),
                    },
                };
                var b = _scope.Declare(local, BindingKind.Import, spec.End);
                b.Import = info;
                b.ImportIndex = _module!.Imports.Count;
                _module.Imports.Add(info);
            }
        }
    }

    internal static string ModuleExportName(Node n) => n switch
    {
        Identifier id => id.Name,
        StringLiteral s => s.Value,
        Literal l => l.Value?.ToString() ?? "",
        _ => throw new CompileError("Unsupported", "Unsupported module export name.", n),
    };

    // ------------------------------------------------------------------ hoisting

    /// <summary>Collects var-scoped declarations of a function body (not entering nested functions).</summary>
    // Annex B.3.3: block-level functions (sloppy) that also get a var binding in the enclosing function
    private readonly HashSet<FunctionDeclaration> _annexBFunctions = new();
    /// <summary>Enclosing lexical declarations for the Annex B.3.3 check; names are computed only when needed.</summary>
    private sealed class LexFrame
    {
        public IEnumerable<Node>? Body;
        public bool IncludeFunctions;
        private HashSet<string>? _names;
        public HashSet<string> Names => _names ??= Body == null ? new HashSet<string>(StringComparer.Ordinal) : LexNames(Body, IncludeFunctions);
        public LexFrame(IEnumerable<Node>? body, bool includeFunctions, HashSet<string>? names = null) { Body = body; IncludeFunctions = includeFunctions; _names = names; }
    }

    private List<LexFrame> _lexStack = new();
    private bool _annexB; // sloppy code: Annex B.3.3 bookkeeping is needed
    private readonly Dictionary<string, Binding> _evalRemovedVars = new(StringComparer.Ordinal);

    private void HoistVarDeclarations(in NodeList<Statement> body, Scope target, FunctionInfo fi)
    {
        var saved = (_lexStack, _annexB);
        _annexB = !fi.Strict;
        _lexStack = _annexB ? new List<LexFrame> { new LexFrame(body, false) } : _lexStack;
        foreach (var st in body) HoistVar(st, target, fi, topLevel: true);
        (_lexStack, _annexB) = saved;
    }

    private readonly Dictionary<Node, BlockStatement> _clauseBlocks = new();

    /// <summary>B.3.4: a FunctionDeclaration as an if clause is treated as if it were inside a block.</summary>
    internal Statement? ClauseBlock(Statement? clause)
    {
        if (clause is not FunctionDeclaration fd) return clause;
        if (!_clauseBlocks.TryGetValue(fd, out var b))
        {
            b = new NestedBlockStatement(NodeList.From<Statement>(new Statement[] { fd })) { Range = fd.Range, Location = fd.Location };
            _clauseBlocks[fd] = b;
        }
        return b;
    }

    private static HashSet<string> LexNames(IEnumerable<Node> body, bool includeFunctions)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in body)
        {
            var n = st is LabeledStatement { Body: FunctionDeclaration lf } ? lf : st;
            switch (n)
            {
                case VariableDeclaration vd when vd.Kind != VariableDeclarationKind.Var:
                    foreach (var d in vd.Declarations) foreach (var id in PatternIdentifiers(d.Id)) set.Add(id.Name);
                    break;
                case ClassDeclaration { Id: { } cid }: set.Add(cid.Name); break;
                case FunctionDeclaration { Id: { } fid } when includeFunctions: set.Add(fid.Name); break;
                case ExportNamedDeclaration { Declaration: { } ed }:
                    foreach (var x in LexNames(new[] { ed }, includeFunctions)) set.Add(x);
                    break;
            }
        }
        return set;
    }

    private void HoistInBlock(IEnumerable<Node> lexicalBody, Node? inner, Scope target, FunctionInfo fi)
    {
        if (!_annexB) { if (inner != null) HoistVar(inner, target, fi, false); return; }
        _lexStack.Add(new LexFrame(lexicalBody, true));
        if (inner != null) HoistVar(inner, target, fi, false);
        _lexStack.RemoveAt(_lexStack.Count - 1);
    }

    private void HoistVar(Node? node, Scope target, FunctionInfo fi, bool topLevel)
    {
        if (node == null) return;
        switch (node)
        {
            case VariableDeclaration vd when vd.Kind == VariableDeclarationKind.Var:
                foreach (var d in vd.Declarations)
                    foreach (var id in PatternIdentifiers(d.Id)) DeclareVar(target, id.Name, BindingKind.Var, id.End);
                break;
            case VariableDeclaration:
                break;
            case FunctionDeclaration fd when topLevel:
                // top-level function declarations are var-scoped
                if (fd.Id != null) DeclareVar(target, fd.Id.Name, BindingKind.Function, fd.End);
                break;
            case FunctionDeclaration fd:
            {
                // Annex B.3.3: a sloppy block-level function also gets a var binding unless that would be an early
                // error (an enclosing lexical declaration of the same name) or the name is a parameter
                if (fi.Strict || fd.Id == null || fd.Async || fd.Generator) break;
                string name = fd.Id.Name;
                for (int i = 0; i < _lexStack.Count - 1; i++)
                    if (_lexStack[i].Names.Contains(name)) return;
                var existing = target.Lookup(name);
                if (existing != null && (existing.Kind is BindingKind.Param || existing.IsLexical)) break;
                // parameter names (in the separate parameter scope when parameters have expressions) and
                // "arguments" never receive an Annex B var binding
                if (fi.BodyScope != null && ReferenceEquals(target, fi.BodyScope) && fi.Scope.Lookup(name) is { Kind: BindingKind.Param }) break;
                if (name == "arguments" && !fi.IsEvalTop && fi.Node != null) break;
                if (existing == null) DeclareVar(target, name, BindingKind.Var, fd.End);
                _annexBFunctions.Add(fd);
                break;
            }
            case BlockStatement b:
                if (_annexB) _lexStack.Add(new LexFrame(b.Body, true));
                foreach (var s in b.Body) HoistVar(s, target, fi, false);
                if (_annexB) _lexStack.RemoveAt(_lexStack.Count - 1);
                break;
            case IfStatement s:
                // B.3.4: `if (x) function f() {}` behaves as if the declaration were in a block
                HoistVar(ClauseBlock(s.Consequent), target, fi, false);
                HoistVar(ClauseBlock(s.Alternate), target, fi, false);
                break;
            case ForStatement s:
                HoistVar(s.Init, target, fi, false);
                HoistInBlock(s.Init is VariableDeclaration fv ? new Node[] { fv } : Array.Empty<Node>(), s.Body, target, fi);
                break;
            case ForInStatement s:
                HoistVar(s.Left, target, fi, false);
                HoistInBlock(s.Left is VariableDeclaration fiv ? new Node[] { fiv } : Array.Empty<Node>(), s.Body, target, fi);
                break;
            case ForOfStatement s:
                HoistVar(s.Left, target, fi, false);
                HoistInBlock(s.Left is VariableDeclaration fov ? new Node[] { fov } : Array.Empty<Node>(), s.Body, target, fi);
                break;
            case WhileStatement s: HoistVar(s.Body, target, fi, false); break;
            case DoWhileStatement s: HoistVar(s.Body, target, fi, false); break;
            case LabeledStatement s: HoistVar(s.Body, target, fi, topLevel && s.Body is not FunctionDeclaration ? false : false); break;
            case TryStatement s:
                HoistVar(s.Block, target, fi, false);
                if (s.Handler != null)
                {
                    // B.3.5: a simple catch parameter may be redeclared by var; a destructuring one may not
                    var catchSet = new HashSet<string>(StringComparer.Ordinal);
                    if (s.Handler.Param is not null and not Identifier)
                        foreach (var id in PatternIdentifiers(s.Handler.Param)) catchSet.Add(id.Name);
                    if (_annexB) _lexStack.Add(new LexFrame(null, false, catchSet));
                    HoistVar(s.Handler.Body, target, fi, false);
                    if (_annexB) _lexStack.RemoveAt(_lexStack.Count - 1);
                }
                HoistVar(s.Finalizer, target, fi, false);
                break;
            case SwitchStatement s:
            {
                var all = new List<Node>();
                foreach (var c in s.Cases) all.AddRange(c.Consequent);
                if (_annexB) _lexStack.Add(new LexFrame(all, true));
                foreach (var c in s.Cases) foreach (var cs in c.Consequent) HoistVar(cs, target, fi, false);
                if (_annexB) _lexStack.RemoveAt(_lexStack.Count - 1);
                break;
            }
            case ExportNamedDeclaration e:
                HoistVar(e.Declaration, target, fi, topLevel);
                break;
            case ExportDefaultDeclaration e when e.Declaration is FunctionDeclaration fdd:
                if (fdd.Id != null) DeclareVar(target, fdd.Id.Name, BindingKind.Function, fdd.End);
                break;
            case WithStatement w:
                HoistVar(w.Body, target, fi, false);
                break;
        }
    }

    private static void DeclareVar(Scope target, string name, BindingKind kind, int end)
    {
        var existing = target.Lookup(name);
        if (existing != null)
        {
            if (existing.IsLexical)
                throw new CompileError("Redeclaration", $"Identifier '{name}' has already been declared", null);
            if (kind == BindingKind.Function && existing.Kind == BindingKind.Var) existing.Kind = BindingKind.Function;
            return;
        }
        target.Declare(name, kind, end);
    }

    /// <summary>Declares let/const/class (and block-level function) bindings of a statement list in <paramref name="scope"/>.</summary>
    private void DeclareLexical(in NodeList<Statement> body, Scope scope, bool topLevelFunctionsAreVar)
    {
        foreach (var st in body) DeclareLexicalStatement(st, scope, topLevelFunctionsAreVar);
    }

    private void DeclareLexicalStatement(Statement st, Scope scope, bool topLevelFunctionsAreVar)
    {
        switch (st)
        {
            case VariableDeclaration vd when vd.Kind is not VariableDeclarationKind.Var:
                // let / const / using / await using (using bindings are const)
                foreach (var d in vd.Declarations)
                    foreach (var id in PatternIdentifiers(d.Id))
                        DeclareLex(scope, id.Name, vd.Kind == VariableDeclarationKind.Let ? BindingKind.Let : BindingKind.Const, d.End, id);
                break;
            case ClassDeclaration cd when cd.Id != null:
                DeclareLex(scope, cd.Id.Name, BindingKind.Class, cd.End, cd.Id);
                break;
            case FunctionDeclaration fd:
                if (topLevelFunctionsAreVar) scope.AddHoisted(fd);
                else
                {
                    if (fd.Id != null)
                    {
                        var existing = scope.Lookup(fd.Id.Name);
                        Binding b;
                        if (existing != null && existing.Kind == BindingKind.Function && !_func.Strict) b = existing;
                        else b = DeclareLex(scope, fd.Id.Name, BindingKind.Function, fd.Start, fd.Id);
                        if (_annexBFunctions.Contains(fd))
                        {
                            var v = _func.VarScope.Lookup(fd.Id.Name);
                            if (v == null && _func.IsEvalTop) _evalRemovedVars.TryGetValue(fd.Id.Name, out v);
                            if (v != null && v.IsVarLike && v.Kind != BindingKind.Param) b.AnnexBVar = v;
                        }
                    }
                    scope.AddHoisted(fd);
                }
                break;
            case ExportNamedDeclaration e when e.Declaration != null:
                DeclareLexicalStatement(e.Declaration, scope, topLevelFunctionsAreVar);
                break;
            case ExportDefaultDeclaration e:
                if (e.Declaration is ClassDeclaration cdd)
                    DeclareLex(scope, cdd.Id?.Name ?? "*default*", BindingKind.Class, cdd.End, cdd);
                else if (e.Declaration is FunctionDeclaration fdd)
                {
                    if (fdd.Id == null) DeclareVar(scope, "*default*", BindingKind.Function, fdd.End);
                    scope.AddHoisted(fdd);
                }
                else DeclareLex(scope, "*default*", BindingKind.Const, e.End, e);
                break;
            case LabeledStatement ls when ls.Body is FunctionDeclaration lfd:
                DeclareLexicalStatement(lfd, scope, topLevelFunctionsAreVar);
                break;
        }
    }

    private Binding DeclareLex(Scope scope, string name, BindingKind kind, int declEnd, Node? node)
    {
        var existing = scope.Lookup(name);
        if (existing != null)
        {
            throw new CompileError("Redeclaration", $"Identifier '{name}' has already been declared", node);
        }
        return scope.Declare(name, kind, declEnd);
    }

    /// <summary>Bound identifiers of a pattern; a plain identifier (the common case) allocates nothing.</summary>
    internal static PatternIds PatternIdentifiers(Node? pattern) => new(pattern);

    internal readonly struct PatternIds(Node? pattern)
    {
        public Enumerator GetEnumerator() => new(pattern);

        public struct Enumerator(Node? pattern)
        {
            private IEnumerator<Identifier>? _slow;
            private int _state;   // 0 = not started, 1 = single yielded, 2 = slow path, 3 = done
            public Identifier Current { get; private set; } = null!;

            public bool MoveNext()
            {
                switch (_state)
                {
                    case 0:
                        if (pattern is Identifier id) { Current = id; _state = 1; return true; }
                        if (pattern == null) { _state = 3; return false; }
                        _slow = PatternIdentifiersSlow(pattern).GetEnumerator();
                        _state = 2;
                        goto case 2;
                    case 2:
                        if (_slow!.MoveNext()) { Current = _slow.Current; return true; }
                        _state = 3;
                        return false;
                    default:
                        _state = 3;
                        return false;
                }
            }
        }
    }

    private static IEnumerable<Identifier> PatternIdentifiersSlow(Node? pattern)
    {
        if (pattern == null) yield break;
        switch (pattern)
        {
            case Identifier id: yield return id; break;
            case ArrayPattern ap:
                foreach (var e in ap.Elements) foreach (var x in PatternIdentifiers(e)) yield return x;
                break;
            case ObjectPattern op:
                foreach (var p in op.Properties)
                {
                    if (p is Property prop) foreach (var x in PatternIdentifiers(prop.Value)) yield return x;
                    else if (p is RestElement re) foreach (var x in PatternIdentifiers(re.Argument)) yield return x;
                }
                break;
            case AssignmentPattern asp: foreach (var x in PatternIdentifiers(asp.Left)) yield return x; break;
            case RestElement r: foreach (var x in PatternIdentifiers(r.Argument)) yield return x; break;
            case VariableDeclaration vd:
                foreach (var d in vd.Declarations) foreach (var x in PatternIdentifiers(d.Id)) yield return x;
                break;
        }
    }

    // ------------------------------------------------------------------ scopes

    private Scope PushScope(ScopeKind kind, Node owner)
    {
        var s = new Scope { Kind = kind, Parent = _scope, Function = _func };
        NodeTable.SetScope(owner, s);
        _scope = s;
        return s;
    }

    private void PopScope() => _scope = _scope.Parent!;

    // ------------------------------------------------------------------ resolution

    private void Resolve(Identifier id, bool isDeclaration = false)
    {
        string name = id.Name;
        if (_evalCode && name == "arguments" && !isDeclaration)
        {
            var ef = _func;
            while (ef.IsArrow && !ef.IsEvalTop && ef.Parent != null) ef = ef.Parent;
            if (ef.IsEvalTop) EvalContainsArguments = true;
        }
        var r = default(ResolvedRef);   // a struct: stored on the identifier on every exit below
        bool crossed = false;
        bool dynamic = false;
        for (var s = _scope; s != null; s = s.Parent)
        {
            if (s.Function != _func && !crossed) crossed = true;
            var b = s.Lookup(name);
            if (b == null && name == "arguments" && s.Kind == ScopeKind.Function && !s.Function.IsArrow && !s.Function.IsSynthetic && !s.Function.IsEvalTop && s.Function.Node is not null)
            {
                b = s.Function.ArgumentsBinding ??= s.Declare("arguments", BindingKind.Arguments, 0);
                s.Function.UsesArguments = true;
            }
            if (b == null)
            {
                if (s.Barrier) dynamic = true;
                if (s.Kind == ScopeKind.Script) break; // script top-level declarations are globals
                continue;
            }
            if (s.Kind == ScopeKind.Script) break;
            if (dynamic && !isDeclaration)
            {
                // found behind a with / eval barrier: looked up by name at run time
                b.Captured = true;
                if (b.HasTdz) b.NeedsTdz = true;
                s.Named = true;
                r.Dynamic = true;
                ResolvedRef.Set(id, r);
                return;
            }
            if (s.Function != _func) crossed = true;
            if (crossed || s.Function != _func) b.Captured = true;
            if (b.Kind == BindingKind.Arguments && !_argumentsMemberRead) s.Function.ArgumentsEscapes = true;
            r.Binding = b;
            if ((b.HasTdz && !isDeclaration) || b.Kind == BindingKind.Import)
            {
                // TDZ: references textually before the declaration end, from closures, or in switch scopes need checks
                if (b.Kind == BindingKind.Import || crossed || id.Start < b.DeclEnd || b.Scope.Kind == ScopeKind.Switch)
                {
                    r.NeedsTdzCheck = true;
                    b.NeedsTdz = true;
                }
            }
            ResolvedRef.Set(id, r);
            return;
        }
        r.Binding = null;
        r.Dynamic = dynamic || _evalCode;
        ResolvedRef.Set(id, r);
    }

    // ------------------------------------------------------------------ functions

    private void VisitFunction(Node node, Identifier? id, in NodeList<Node> parameters, StatementOrExpression body, bool isArrow, bool isExpressionName, bool strictBody, bool isMethod = false)
    {
        Enter(node);
        var parentFunc = _func;
        var parentScope = _scope;
        var fi = new FunctionInfo { Node = node, Parent = parentFunc, IsArrow = isArrow, Strict = parentFunc.Strict || strictBody };
        NodeTable.SetFunction(node, fi);
        var scope = new Scope { Kind = ScopeKind.Function, Parent = parentScope, Function = fi };
        fi.Scope = scope;
        NodeTable.SetScope(node, scope);
        _func = fi;
        _scope = scope;
        MarkEvalFunction(node, scope, fi);

        // parameters
        int pi = 0;
        bool simpleParams = true;
        foreach (var p in parameters) if (p is not Identifier) simpleParams = false;
        foreach (var p in parameters)
        {
            foreach (var pid in PatternIdentifiers(p))
            {
                var existing = scope.Lookup(pid.Name);
                if (existing == null)
                {
                    var b = scope.Declare(pid.Name, BindingKind.Param, 0);
                    if (p is Identifier) b.ParamIndex = pi;
                    if (!simpleParams) { b.ParamTdz = true; b.DeclEnd = p.End; }
                }
                else if (p is Identifier) existing.ParamIndex = pi;
            }
            pi++;
        }
        if (body is BlockStatement block)
        {
            var target = scope;
            if (!simpleParams)
            {
                // parameter expressions: closures in the parameter list must not see body declarations
                target = new Scope { Kind = ScopeKind.Block, Parent = scope, Function = fi };
                if (scope.Barrier) { target.Barrier = true; target.Named = true; target.ForceEnv = true; }
                NodeTable.SetScope(block, target);
                fi.BodyScope = target;
            }
            HoistVarDeclarations(block.Body, target, fi);
            DeclareLexical(block.Body, target, topLevelFunctionsAreVar: true);
            // `var arguments` does not suppress the arguments object (FunctionDeclarationInstantiation step 15/18):
            // the var shares (or, with parameter expressions, starts from) the arguments binding
            if (!isArrow && target.Lookup("arguments") is { Kind: BindingKind.Var } argVar && scope.Lookup("arguments") is not { Kind: BindingKind.Param })
            {
                if (ReferenceEquals(target, scope))
                {
                    argVar.Kind = BindingKind.Arguments;
                    fi.ArgumentsBinding = argVar;
                }
                else fi.ArgumentsBinding ??= scope.Declare("arguments", BindingKind.Arguments, 0);
                fi.UsesArguments = true;
                fi.ArgumentsEscapes = true;
            }
        }
        if (isExpressionName && id != null && scope.Lookup(id.Name) == null)
        {
            fi.FuncNameBinding = scope.Declare(id.Name, BindingKind.FuncName, 0);
        }

        foreach (var p in parameters) VisitPattern(p, isDeclaration: true);
        if (fi.BodyScope != null) _scope = fi.BodyScope;
        if (body is BlockStatement b2)
        {
            foreach (var st in b2.Body) VisitStatement(st);
        }
        else VisitExpression((Expression)body);
        bool mapped = !fi.Strict && simpleParams && !isArrow;
        if (fi.UsesArguments && fi.ArgumentsBinding != null && CanElideArguments(fi, node, mapped && parameters.Count > 0))
        {
            fi.ArgumentsElided = fi.ArgumentsBinding.ArgumentsElided = true;
            fi.MappedArguments = mapped;
            if (!simpleParams) foreach (var p in parameters) if (p is RestElement) fi.ArgumentsIndexed = true;   // the frame keeps the whole list anyway
        }
        else if (fi.UsesArguments && fi.ArgumentsBinding != null && mapped)
        {
            // mapped arguments object (10.4.4): parameters live in the environment so the object can alias them
            fi.MappedArguments = true;
            foreach (var b in scope.Ordered) if (b.Kind == BindingKind.Param) b.Captured = true;
        }

        _func = parentFunc;
        _scope = parentScope;
        Leave();
    }

    private static bool CanElideArguments(FunctionInfo fi, Node node, bool aliasesParameters)
    {
        if (fi.ArgumentsEscapes || fi.ArgumentsBinding!.Captured || fi.HasDirectEval) return false;
        if (fi.Scope.Named || fi.Scope.Barrier || fi.BodyScope is { Named: true }) return false;   // bindings may live in a named environment
        if (node is IFunction { Generator: true } or IFunction { Async: true }) return false;
        return !(fi.ArgumentsIndexed && aliasesParameters);
    }

    private FunctionInfo BeginSyntheticFunction(Node owner)
    {
        var fi = new FunctionInfo { Node = owner, Parent = _func, Strict = true, IsSynthetic = true };
        var scope = new Scope { Kind = ScopeKind.Function, Parent = _scope, Function = fi };
        fi.Scope = scope;
        _func = fi;
        _scope = scope;
        return fi;
    }

    // ------------------------------------------------------------------ statements

    private void VisitStatements(in NodeList<Statement> list)
    {
        foreach (var s in list) VisitStatement(s);
    }

    private void VisitStatement(Node? node)
    {
        if (node == null) return;
        Enter(node);
        _checkpoint?.Invoke();
        switch (node)
        {
            case ExpressionStatement es: VisitExpression(es.Expression); break;
            case VariableDeclaration vd:
                if (vd.Kind == VariableDeclarationKind.AwaitUsing) _func.ContainsAwait = true;
                foreach (var d in vd.Declarations)
                {
                    // `var x = v` is a PutValue through ResolveBinding (visible to with/eval scopes); lexical
                    // declarations initialize their binding directly
                    VisitPattern(d.Id, isDeclaration: vd.Kind != VariableDeclarationKind.Var);
                    if (d.Init != null) VisitExpression(d.Init);
                }
                break;
            case FunctionDeclaration fd:
                if (fd.Id != null) Resolve(fd.Id, true);
                VisitFunction(fd, fd.Id, fd.Params, fd.Body, false, false, fd.Body.Strict);
                break;
            case ClassDeclaration cd:
                if (cd.Id != null) Resolve(cd.Id, true);
                VisitClass(cd, cd.Id, cd.SuperClass, cd.Body);
                break;
            case ReturnStatement rs: if (rs.Argument != null) VisitExpression(rs.Argument); break;
            case IfStatement s:
                VisitExpression(s.Test);
                VisitStatement(ClauseBlock(s.Consequent));
                VisitStatement(ClauseBlock(s.Alternate));
                break;
            case BlockStatement b: VisitBlock(b); break;
            case ForStatement f:
            {
                bool lexical = f.Init is VariableDeclaration { Kind: not VariableDeclarationKind.Var };
                if (lexical)
                {
                    var s = PushScope(ScopeKind.For, f);
                    var vd = (VariableDeclaration)f.Init!;
                    foreach (var d in vd.Declarations)
                        foreach (var id in PatternIdentifiers(d.Id))
                            DeclareLex(s, id.Name, vd.Kind == VariableDeclarationKind.Let ? BindingKind.Let : BindingKind.Const, d.End, id);
                }
                if (f.Init is VariableDeclaration ivd) VisitStatement(ivd);
                else if (f.Init is Expression ie) VisitExpression(ie);
                if (f.Test != null) VisitExpression(f.Test);
                if (f.Update != null) VisitExpression(f.Update);
                VisitStatement(f.Body);
                if (lexical) PopScope();
                break;
            }
            case ForInStatement fin:
                VisitForInOf(fin, fin.Left, fin.Right, fin.Body);
                break;
            case ForOfStatement fof:
                if (fof.Await || fof.Left is VariableDeclaration { Kind: VariableDeclarationKind.AwaitUsing }) _func.ContainsAwait = true;
                VisitForInOf(fof, fof.Left, fof.Right, fof.Body);
                break;
            case WhileStatement w: VisitExpression(w.Test); VisitStatement(w.Body); break;
            case DoWhileStatement dw: VisitStatement(dw.Body); VisitExpression(dw.Test); break;
            case BreakStatement or ContinueStatement or EmptyStatement or DebuggerStatement: break;
            case ThrowStatement t: VisitExpression(t.Argument); break;
            case TryStatement t:
                VisitBlock(t.Block);
                if (t.Handler != null)
                {
                    var cs = PushScope(ScopeKind.Catch, t.Handler);
                    if (t.Handler.Param != null)
                        foreach (var id in PatternIdentifiers(t.Handler.Param))
                            cs.Declare(id.Name, BindingKind.CatchParam, 0);
                    if (t.Handler.Param is null or Identifier)
                    {
                        DeclareLexical(t.Handler.Body.Body, cs, topLevelFunctionsAreVar: false);
                        if (t.Handler.Param != null) VisitPattern(t.Handler.Param, isDeclaration: true);
                        VisitStatements(t.Handler.Body.Body);
                    }
                    else
                    {
                        // a destructuring parameter may contain closures: they must not see the block's declarations,
                        // so the block gets its own environment (CatchClauseEvaluation step 8)
                        VisitPattern(t.Handler.Param, isDeclaration: true);
                        VisitBlock(t.Handler.Body);
                    }
                    PopScope();
                }
                if (t.Finalizer != null) VisitBlock(t.Finalizer);
                break;
            case SwitchStatement sw:
            {
                VisitExpression(sw.Discriminant);
                var s = PushScope(ScopeKind.Switch, sw);
                foreach (var c in sw.Cases) DeclareLexical(c.Consequent, s, topLevelFunctionsAreVar: false);
                foreach (var c in sw.Cases)
                {
                    if (c.Test != null) VisitExpression(c.Test);
                    VisitStatements(c.Consequent);
                }
                PopScope();
                break;
            }
            case LabeledStatement ls: VisitStatement(ls.Body); break;
            case WithStatement w:
            {
                VisitExpression(w.Object);
                var ws = PushScope(ScopeKind.With, w);
                ws.Barrier = true;
                VisitStatement(w.Body);
                PopScope();
                break;
            }
            case ImportDeclaration: break;
            case ExportNamedDeclaration e:
                if (e.Declaration != null) VisitStatement(e.Declaration);
                else if (e.Source == null)
                {
                    foreach (var spec in e.Specifiers)
                        if (spec.Local is Identifier lid) Resolve(lid);
                }
                break;
            case ExportDefaultDeclaration e:
                if (e.Declaration is FunctionDeclaration fdd)
                {
                    if (fdd.Id != null) Resolve(fdd.Id);
                    VisitFunction(fdd, fdd.Id, fdd.Params, fdd.Body, false, false, fdd.Body.Strict);
                }
                else if (e.Declaration is ClassDeclaration cdd)
                {
                    if (cdd.Id != null) Resolve(cdd.Id);
                    VisitClass(cdd, cdd.Id, cdd.SuperClass, cdd.Body);
                }
                else VisitExpression((Expression)e.Declaration);
                break;
            case ExportAllDeclaration: break;
            default:
                throw new CompileError("Unsupported", $"Unsupported statement '{node.Type}'.", node);
        }
        Leave();
    }

    private void VisitForInOf(Statement node, Node left, Expression right, Statement body)
    {
        bool lexical = left is VariableDeclaration { Kind: not VariableDeclarationKind.Var };
        if (lexical)
        {
            // TDZ scope for the right-hand side evaluation + per-iteration binding scope
            var s = PushScope(ScopeKind.For, node);
            var vd = (VariableDeclaration)left;
            foreach (var d in vd.Declarations)
                foreach (var id in PatternIdentifiers(d.Id))
                    DeclareLex(s, id.Name, vd.Kind == VariableDeclarationKind.Let ? BindingKind.Let : BindingKind.Const, int.MaxValue, id);
            VisitExpression(right);
            // bindings are initialized before each body execution
            foreach (var b in s.Ordered) b.DeclEnd = 0;
            foreach (var d in vd.Declarations) VisitPattern(d.Id, isDeclaration: true);
            VisitStatement(body);
            PopScope();
            return;
        }
        // Annex B.3.5: for (var x = init in obj) evaluates the initializer first
        if (left is VariableDeclaration { Kind: VariableDeclarationKind.Var } ivd && ivd.Declarations[0].Init is { } forInInit)
            VisitExpression(forInInit);
        VisitExpression(right);
        if (left is VariableDeclaration vvd)
        {
            foreach (var d in vvd.Declarations) VisitPattern(d.Id, isDeclaration: vvd.Kind != VariableDeclarationKind.Var);
        }
        else VisitPattern(left, isDeclaration: false);
        VisitStatement(body);
    }

    private void VisitBlock(BlockStatement b)
    {
        bool needsScope = false;
        foreach (var st in b.Body)
        {
            if (st is VariableDeclaration { Kind: not VariableDeclarationKind.Var } or ClassDeclaration or FunctionDeclaration ||
                st is LabeledStatement { Body: FunctionDeclaration })
            { needsScope = true; break; }
        }
        if (needsScope)
        {
            var s = PushScope(ScopeKind.Block, b);
            DeclareLexical(b.Body, s, topLevelFunctionsAreVar: false);
            VisitStatements(b.Body);
            PopScope();
        }
        else VisitStatements(b.Body);
    }

    private void VisitClass(Node node, Identifier? id, Expression? superClass, ClassBody body)
    {
        Enter(node);
        // class decorators are evaluated in the enclosing scope, before the class scope exists
        foreach (var dec in CodeGen.ClassDecorators(node)) { MarkNotRead(dec.Expression); VisitExpression(dec.Expression); }
        var cs = PushScope(ScopeKind.Class, node);
        if (id != null) cs.Declare(id.Name, BindingKind.ClassInner, node.End);
        // private names
        foreach (var el in body.Body)
        {
            if (el is ClassProperty cp && cp.Key is PrivateIdentifier pid)
            {
                string pname = "#" + pid.Name;
                if (cs.Lookup(pname) == null) cs.Declare(pname, BindingKind.PrivateName, 0).Captured = true;
            }
        }
        foreach (var el in body.Body)
            if (el is ClassProperty kp && kp.Key is PrivateIdentifier kpid) ResolvePrivate(kpid);
        if (superClass != null) VisitExpression(superClass);
        var outerFunc = _func;
        var outerScope = _scope;
        // instance field initializer pseudo-function
        FunctionInfo? fieldInit = null;
        FunctionInfo? staticInit = null;
        foreach (var el in body.Body)
        {
            // element decorators are evaluated in the class scope, interleaved with the computed keys
            foreach (var dec in CodeGen.ElementDecorators(el)) { MarkNotRead(dec.Expression); VisitExpression(dec.Expression); }
            switch (el)
            {
                case MethodDefinition md:
                    if (md.Computed) VisitExpression(md.Key);
                    VisitFunction(md.Value, null, md.Value.Params, md.Value.Body, false, false, true, isMethod: true);
                    if (md.Kind == PropertyKind.Constructor) NodeTable.Function(md.Value).HasSuperCall = superClass != null;
                    break;
                case PropertyDefinition or AccessorProperty:
                    var pd = (ClassProperty)el;
                    if (pd.Computed) VisitExpression(pd.Key);
                    if (pd.Value != null)
                    {
                        if (pd.Static)
                        {
                            if (staticInit == null) { staticInit = BeginSyntheticFunction(body); staticInit.IsFieldInit = true; NodeTable.SetScope(pd, staticInit.Scope); _func = outerFunc; _scope = outerScope; }
                            _func = staticInit; _scope = staticInit.Scope;
                        }
                        else
                        {
                            if (fieldInit == null) { fieldInit = BeginSyntheticFunction(body); fieldInit.IsFieldInit = true; _func = outerFunc; _scope = outerScope; }
                            _func = fieldInit; _scope = fieldInit.Scope;
                        }
                        VisitExpression(pd.Value);
                        _func = outerFunc; _scope = outerScope;
                    }
                    break;
                case StaticBlock sb:
                {
                    var fi = BeginSyntheticFunction(sb);
                    NodeTable.SetFunction(sb, fi);
                    NodeTable.SetScope(sb, fi.Scope);
                    HoistVarDeclarations(sb.Body, fi.Scope, fi);
                    DeclareLexical(sb.Body, fi.Scope, topLevelFunctionsAreVar: true);
                    VisitStatements(sb.Body);
                    _func = outerFunc; _scope = outerScope;
                    break;
                }
            }
        }
        if (fieldInit != null) NodeTable.SetFunction(GetFieldInitKey(body, false), fieldInit);
        if (staticInit != null) NodeTable.SetFunction(GetFieldInitKey(body, true), staticInit);
        PopScope();
        Leave();
    }

    // Synthetic keys used to look up the pseudo-functions of a class body.
    private readonly Dictionary<(ClassBody, bool), Node> _initKeys = new();
    internal Node GetFieldInitKey(ClassBody body, bool isStatic)
    {
        if (!_initKeys.TryGetValue((body, isStatic), out var n))
        {
            n = new EmptyStatement();
            _initKeys[(body, isStatic)] = n;
        }
        return n;
    }

    // ------------------------------------------------------------------ patterns

    private void VisitPattern(Node? node, bool isDeclaration)
    {
        if (node == null) return;
        switch (node)
        {
            case Identifier id: Resolve(id, isDeclaration); break;
            case ArrayPattern ap:
                foreach (var e in ap.Elements) VisitPattern(e, isDeclaration);
                break;
            case ObjectPattern op:
                foreach (var p in op.Properties)
                {
                    if (p is Property prop)
                    {
                        if (prop.Computed) VisitExpression(prop.Key);
                        VisitPattern(prop.Value, isDeclaration);
                    }
                    else if (p is RestElement re) VisitPattern(re.Argument, isDeclaration);
                }
                break;
            case AssignmentPattern asp:
                VisitPattern(asp.Left, isDeclaration);
                VisitExpression(asp.Right);
                break;
            case RestElement r: VisitPattern(r.Argument, isDeclaration); break;
            case MemberExpression me when !isDeclaration: MarkNotRead(me); VisitExpression(me); break;
            case Expression e when !isDeclaration: MarkNotRead(e); VisitExpression(e); break;
            default: throw new CompileError("InvalidPattern", "Invalid destructuring target.", node);
        }
    }

    // ------------------------------------------------------------------ expressions

    private void VisitExpression(Expression? node)
    {
        if (node == null) return;
        Enter(node);
        switch (node)
        {
            case Identifier id: Resolve(id); break;
            case Literal: break;
            case ThisExpression: _func.UsesThis = true; break;
            case Super: break;
            case TemplateLiteral tl: foreach (var e in tl.Expressions) VisitExpression(e); break;
            case TaggedTemplateExpression tt: MarkNotRead(tt.Tag); VisitExpression(tt.Tag); foreach (var e in tt.Quasi.Expressions) VisitExpression(e); break;
            case ArrayExpression ae: foreach (var e in ae.Elements) VisitExpression(e); break;
            case ObjectExpression oe:
                foreach (var p in oe.Properties)
                {
                    if (p is SpreadElement se) VisitExpression(se.Argument);
                    else if (p is Property prop)
                    {
                        if (prop.Computed) VisitExpression(prop.Key);
                        if (prop.Value is FunctionExpression fe && (prop.Method || prop.Kind is PropertyKind.Get or PropertyKind.Set))
                            VisitFunction(fe, null, fe.Params, fe.Body, false, false, fe.Body.Strict, isMethod: true);
                        else if (prop.Shorthand && prop.Value is Identifier sid) Resolve(sid);
                        else VisitExpression((Expression)prop.Value);
                    }
                }
                break;
            case FunctionExpression fe: VisitFunction(fe, fe.Id, fe.Params, fe.Body, false, true, fe.Body.Strict); break;
            case ArrowFunctionExpression af: VisitFunction(af, null, af.Params, af.Body, true, false, af.Body is FunctionBody fb && fb.Strict); break;
            case ClassExpression ce: VisitClass(ce, ce.Id, ce.SuperClass, ce.Body); break;
            case UnaryExpression ue:
                if (ue is UpdateExpression || ue.Operator == Acornima.Operator.Delete) MarkNotRead(ue.Argument);
                VisitExpression(ue.Argument);
                break;
            case BinaryExpression be: VisitExpression(be.Left); VisitExpression(be.Right); break;
            case AssignmentExpression ae:
                if (ae.Left is Expression le && ae.Left is not ArrayPattern && ae.Left is not ObjectPattern) { MarkNotRead(le); VisitExpression(le); }
                else VisitPattern(ae.Left, isDeclaration: false);
                VisitExpression(ae.Right);
                break;
            case ConditionalExpression c: VisitExpression(c.Test); VisitExpression(c.Consequent); VisitExpression(c.Alternate); break;
            case CallExpression call:
                if (call.Callee is Identifier { Name: "eval" } && !call.Optional) MarkEvalSite();
                MarkNotRead(call.Callee);
                VisitExpression(call.Callee);
                foreach (var a in call.Arguments) VisitExpression(a);
                break;
            case NewExpression ne:
                VisitExpression(ne.Callee);
                foreach (var a in ne.Arguments) VisitExpression(a);
                break;
            case MemberExpression me:
                if (me.Object is Identifier { Name: "arguments" } argsId && !me.Optional && !ReferenceEquals(me, _notRead) && (me.Computed || me.Property is Identifier { Name: "length" }))
                {
                    _argumentsMemberRead = true;
                    Resolve(argsId);
                    _argumentsMemberRead = false;
                    if (me.Computed && ResolvedRef.Get(argsId).Binding is { Kind: BindingKind.Arguments } ab) ab.Scope.Function.ArgumentsIndexed = true;
                }
                else VisitExpression(me.Object);
                if (me.Computed) VisitExpression(me.Property);
                else if (me.Property is PrivateIdentifier pid) ResolvePrivate(pid);
                break;
            case ChainExpression ch: VisitExpression(ch.Expression); break;
            case SequenceExpression se: foreach (var e in se.Expressions) VisitExpression(e); break;
            case SpreadElement sp: VisitExpression(sp.Argument); break;
            case YieldExpression y: if (y.Argument != null) VisitExpression(y.Argument); break;
            case AwaitExpression aw: _func.ContainsAwait = true; VisitExpression(aw.Argument); break;
            case MetaProperty: break;
            case ParenthesizedExpression pe: VisitExpression(pe.Expression); break;
            case ImportExpression ie: VisitExpression(ie.Source); if (ie.Options != null) VisitExpression(ie.Options); break;
            case PrivateIdentifier pid: ResolvePrivate(pid); break;
            default:
                throw new CompileError("Unsupported", $"Unsupported expression '{node.Type}'.", node);
        }
        Leave();
    }

    /// <summary>Set while resolving the object of a plain <c>arguments.length</c> or <c>arguments[key]</c> read.</summary>
    private bool _argumentsMemberRead;
    /// <summary>The last expression seen in a position where a member expression is more than a read of its value.</summary>
    private Node? _notRead;

    /// <summary>Assignment target, delete, ++/--, callee or tag (whose receiver is the object), destructuring target, decorator.</summary>
    private void MarkNotRead(Node? e)
    {
        while (e is ParenthesizedExpression p) e = p.Expression;
        _notRead = e;
    }

    private void ResolvePrivate(PrivateIdentifier pid)
    {
        if (!(_privateNames ??= new Dictionary<string, string>(StringComparer.Ordinal)).TryGetValue(pid.Name, out var hashName))
            _privateNames[pid.Name] = hashName = "#" + pid.Name;
        var fake = new Identifier(hashName);
        _privateRefs[pid] = fake;
        Resolve(fake);
        var r = ResolvedRef.Get(fake);
        r.NeedsTdzCheck = false;
        ResolvedRef.Set(fake, r);
        if (r.Binding == null)
        {
            // eval code may use private names of the classes enclosing the eval call (checked when the eval runs)
            if (_evalCode && r.Dynamic) { (EvalPrivateNames ??= new HashSet<string>()).Add(fake.Name); return; }
            throw new CompileError("UndeclaredPrivate", $"Private field '#{pid.Name}' must be declared in an enclosing class", pid);
        }
    }

    private readonly Dictionary<PrivateIdentifier, Identifier> _privateRefs = new();
    private Dictionary<string, string>? _privateNames;
    internal Identifier PrivateRef(PrivateIdentifier pid) => _privateRefs[pid];
}
