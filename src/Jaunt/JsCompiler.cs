using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Acornima;
using Acornima.Ast;
using Jaunt.Compiler;

namespace Jaunt;

/// <summary>Compilation configuration and compile-time budget.</summary>
public sealed class JsCompileOptions
{
    /// <summary>Default options.</summary>
    public static JsCompileOptions Default { get; } = new();

    /// <summary>Name used in diagnostics and stack traces.</summary>
    public string SourceName { get; init; } = "<anonymous>";
    /// <summary>Treat the script as strict mode code.</summary>
    public bool Strict { get; init; }
    /// <summary>Maximum source length in UTF-16 code units.</summary>
    public int MaxSourceLength { get; init; } = 16 * 1024 * 1024;
    /// <summary>Maximum syntactic nesting depth accepted by the compiler.</summary>
    public int MaxNestingDepth { get; init; } = 1000;
    /// <summary>Maximum number of AST nodes.</summary>
    public int MaxNodes { get; init; } = 4_000_000;
    /// <summary>Maximum wall-clock compile time (cooperative check during parsing and code generation).</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Keep the source text for <c>Function.prototype.toString</c>. Default true.</summary>
    public bool RetainSource { get; init; } = true;
}

/// <summary>Raised for syntax errors, early errors and unsupported language features.</summary>
public sealed class JsCompileException : Exception
{
    /// <summary>Diagnostic code (e.g. <c>SyntaxError</c>, <c>UnsupportedWith</c>, <c>NestingTooDeep</c>).</summary>
    public string Code { get; }
    /// <summary>Source name.</summary>
    public string SourceName { get; }
    /// <summary>1-based line (0 when unknown).</summary>
    public int Line { get; }
    /// <summary>1-based column (0 when unknown).</summary>
    public int Column { get; }
    /// <summary>The message without location.</summary>
    public string Description { get; }

    internal JsCompileException(string code, string description, string sourceName, int line, int column, Exception? inner = null)
        : base(line > 0 ? $"{description} ({sourceName}:{line}:{column})" : $"{description} ({sourceName})", inner)
    {
        Code = code;
        Description = description;
        SourceName = sourceName;
        Line = line;
        Column = column;
    }
}

/// <summary>
/// Immutable compiled Script. Contains no realm state and can be evaluated concurrently by any number of
/// <see cref="JsEngine"/> instances.
/// </summary>
public sealed class JsScript
{
    internal readonly ScriptTemplate Template;
    internal JsScript(ScriptTemplate template) => Template = template;
    /// <summary>Source name given at compile time.</summary>
    public string SourceName => Template.SourceName;
    /// <summary>Human readable bytecode listing (diagnostics only; format is not stable).</summary>
    public string Disassemble() => Template.Body.Disassemble();
}

/// <summary>
/// Immutable compiled ES module template. Loading it into an engine (<see cref="JsEngine.LoadModule"/>)
/// creates a private module instance; the template itself never holds module state.
/// </summary>
public sealed class JsModule
{
    internal readonly ScriptTemplate Template;
    internal JsModule(ScriptTemplate template) => Template = template;
    /// <summary>Source name given at compile time.</summary>
    public string SourceName => Template.SourceName;
    /// <summary>Module specifiers requested by static imports/re-exports, in source order.</summary>
    public IReadOnlyList<string> RequestedModules
    {
        get
        {
            var list = new List<string>();
            foreach (var k in Template.Module!.RequestedModules)
            {
                // a specifier requested in several phases (e.g. `import defer` and `import`) is listed once
                var spec = JsCompiler.SplitRequestKey(k).Specifier;
                if (!list.Contains(spec)) list.Add(spec);
            }
            return list;
        }
    }
    /// <summary>True for JSON modules created by <see cref="JsCompiler.CompileJsonModule"/>.</summary>
    public bool IsJsonModule => Template.Module!.IsJson;
    /// <summary>True for text modules created by <see cref="JsCompiler.CreateTextModule"/>.</summary>
    public bool IsTextModule => Template.Module!.IsText;
    /// <summary>True for bytes modules created by <see cref="JsCompiler.CreateBytesModule"/>.</summary>
    public bool IsBytesModule => Template.Module!.IsBytes;
    /// <summary>
    /// The module-source kind of host modules created by <see cref="JsCompiler.CreateSourceModule"/> (the
    /// <c>Symbol.toStringTag</c> of their Module Source object); null for modules without a source representation
    /// (source text, JSON and text modules: <c>import source</c> of them throws a SyntaxError).
    /// </summary>
    public string? ModuleSourceKind => Template.Module!.SourceKind;
    /// <summary>True when the module body uses top-level await (requires asynchronous loading).</summary>
    public bool HasTopLevelAwait => Template.Module!.HasTopLevelAwait;
    /// <summary>Human readable bytecode listing (diagnostics only; format is not stable).</summary>
    public string Disassemble() => Template.Body.Disassemble();
}

/// <summary>Stateless compiler entry point. Thread-safe.</summary>
public static class JsCompiler
{
    /// <summary>Compiles a classic script.</summary>
    public static JsScript CompileScript(string source, JsCompileOptions? options = null, CancellationToken cancellationToken = default)
        => new(Compile(source, options ?? JsCompileOptions.Default, isModule: false, cancellationToken));

    /// <summary>Compiles an ES module template.</summary>
    public static JsModule CompileModule(string source, JsCompileOptions? options = null, CancellationToken cancellationToken = default)
        => new(Compile(source, options ?? JsCompileOptions.Default, isModule: true, cancellationToken));

    /// <summary>Compiles eval code (19.2.1.1 PerformEval steps 4-11); scope analysis runs in eval mode.</summary>
    internal static ScriptTemplate CompileEval(string source, bool strictCaller, bool allowNewTarget, bool allowSuper, CancellationToken ct, bool inFieldInit = false, bool allowSuperCall = false)
        => Compile(source, s_evalOptions, isModule: false, ct, forceStrict: strictCaller, eval: (allowNewTarget, allowSuper, inFieldInit, allowSuperCall));

    private static readonly JsCompileOptions s_evalOptions = new() { SourceName = "eval" };

    /// <summary>
    /// Compiles a JSON module (the target of <c>import x from "..." with { type: "json" }</c>): a synthetic module
    /// whose only export is <c>default</c>, the parsed JSON value. The text is validated here (SyntaxError at load).
    /// </summary>
    public static JsModule CompileJsonModule(string json, JsCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        options ??= JsCompileOptions.Default;
        if (json.Length > options.MaxSourceLength)
            throw new JsCompileException("SourceTooLarge", $"Source length {json.Length} exceeds the limit of {options.MaxSourceLength}.", options.SourceName, 0, 0);
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json),
                new System.Text.Json.JsonReaderOptions { MaxDepth = 100_000 });
            while (reader.Read()) { }
        }
        catch (System.Text.Json.JsonException je)
        {
            throw new JsCompileException("SyntaxError", "Invalid JSON module: " + je.Message, options.SourceName, 0, 0);
        }
        var info = new ModuleRecordInfo { IsJson = true, EnvSize = 1 };
        info.EnvSlots["*default*"] = 0;
        info.LocalExports.Add(new ExportEntryInfo { ExportName = "default", LocalName = "*default*", LocalEnvIndex = 0 });
        var script = new ScriptTemplate { Body = null!, SourceName = options.SourceName, IsModule = true, Strict = true, Module = info };
        var fb = new FunctionBuilder();
        int r = fb.AllocReg();
        fb.Emit(Op.ParseJsonConst, r, fb.Const(json));
        fb.Emit(Op.StEnv, 0, 0, r);
        fb.Emit(Op.RetUndef);
        var t = new FunctionTemplate { Name = "<module>", Script = script, Flags = FunctionFlags.Strict | FunctionFlags.ModuleBody };
        fb.Finish(t);
        BytecodeVerifier.Verify(t);
        script.Body = t;
        return new JsModule(script);
    }

    /// <summary>
    /// Creates a text module (the target of <c>import x from "..." with { type: "text" }</c>): a synthetic module whose
    /// only export is <c>default</c>, the given text.
    /// </summary>
    public static JsModule CreateTextModule(string text, JsCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= JsCompileOptions.Default;
        if (text.Length > options.MaxSourceLength)
            throw new JsCompileException("SourceTooLarge", $"Source length {text.Length} exceeds the limit of {options.MaxSourceLength}.", options.SourceName, 0, 0);
        return SyntheticModule(new ModuleRecordInfo { IsText = true }, options.SourceName, (fb, r) => fb.Emit(Op.LoadConst, r, fb.Const(text)));
    }

    /// <summary>
    /// Creates a bytes module (the target of <c>import x from "..." with { type: "bytes" }</c>): its <c>default</c>
    /// export is a <c>Uint8Array</c> over an immutable <c>ArrayBuffer</c> holding a copy of <paramref name="bytes"/>,
    /// created in the realm that evaluates the module.
    /// </summary>
    public static JsModule CreateBytesModule(ReadOnlySpan<byte> bytes, JsCompileOptions? options = null)
    {
        options ??= JsCompileOptions.Default;
        if (bytes.Length > options.MaxSourceLength)
            throw new JsCompileException("SourceTooLarge", $"Module length {bytes.Length} exceeds the limit of {options.MaxSourceLength}.", options.SourceName, 0, 0);
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
        var latin1 = new string(chars);
        return SyntheticModule(new ModuleRecordInfo { IsBytes = true }, options.SourceName, (fb, r) =>
        {
            fb.Emit(Op.LoadConst, r, fb.Const(latin1));
            fb.Emit(Op.Intrinsic, r, (int)Jaunt.Runtime.ClassIntrinsic.BytesModule, r, 1);
        });
    }

    /// <summary>
    /// Creates a host-defined module that has a source representation (source phase imports): <c>import source x from
    /// "..."</c> and <c>import.source("...")</c> of it produce a Module Source object of the engine's realm whose
    /// prototype chain is <c>(per-kind prototype) → %AbstractModuleSource%.prototype</c> and whose
    /// <c>Symbol.toStringTag</c> is <paramref name="sourceKind"/>. Imported normally it is an empty module.
    /// </summary>
    public static JsModule CreateSourceModule(string sourceKind, JsCompileOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceKind);
        options ??= JsCompileOptions.Default;
        var info = new ModuleRecordInfo { SourceKind = sourceKind };
        return SyntheticModule(info, options.SourceName, null);
    }

    /// <summary>Builds a synthetic module whose body stores one value (if any) into its sole export, <c>default</c>.</summary>
    private static JsModule SyntheticModule(ModuleRecordInfo info, string sourceName, Action<FunctionBuilder, int>? emitDefault)
    {
        var script = new ScriptTemplate { Body = null!, SourceName = sourceName, IsModule = true, Strict = true, Module = info };
        var fb = new FunctionBuilder();
        if (emitDefault != null)
        {
            info.EnvSize = 1;
            info.EnvSlots["*default*"] = 0;
            info.LocalExports.Add(new ExportEntryInfo { ExportName = "default", LocalName = "*default*", LocalEnvIndex = 0 });
            int r = fb.AllocReg();
            emitDefault(fb, r);
            fb.Emit(Op.StEnv, 0, 0, r);
        }
        fb.Emit(Op.RetUndef);
        var t = new FunctionTemplate { Name = "<module>", Script = script, Flags = FunctionFlags.Strict | FunctionFlags.ModuleBody };
        fb.Finish(t);
        BytecodeVerifier.Verify(t);
        script.Body = t;
        return new JsModule(script);
    }

    /// <summary>Module request key: the specifier plus its import attributes ("spec\0key=value...", keys sorted).</summary>
    internal static string RequestKey(string specifier, IReadOnlyList<KeyValuePair<string, string>>? attributes)
    {
        if (attributes == null || attributes.Count == 0) return specifier;
        var sorted = new List<KeyValuePair<string, string>>(attributes);
        sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var sb = new System.Text.StringBuilder(specifier);
        foreach (var kv in sorted) sb.Append('\0').Append(kv.Key).Append('=').Append(kv.Value);
        return sb.ToString();
    }

    internal static (string Specifier, Dictionary<string, string> Attributes) SplitRequestKey(string key)
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        int z = key.IndexOf('\0');
        if (z < 0) return (key, attrs);
        foreach (var part in key[(z + 1)..].Split('\0'))
        {
            int eq = part.IndexOf('=');
            attrs[part[..eq]] = part[(eq + 1)..];
        }
        return (key[..z], attrs);
    }

    internal static string StaticRequestKey(Literal source, in NodeList<ImportAttribute> attributes)
    {
        string spec = (string)source.Value!;
        if (attributes.Count == 0) return spec;
        var list = new List<KeyValuePair<string, string>>();
        foreach (var a in attributes)
        {
            string key = a.Key switch { Identifier id => id.Name, Literal l => (string)l.Value!, _ => "" };
            if (key != "type")
                throw new CompileError("SyntaxError", $"Unsupported import attribute '{key}'", a);
            list.Add(new(key, (string)((Literal)a.Value).Value!));
        }
        return RequestKey(spec, list);
    }

    /// <summary>
    /// A reusable Acornima parser plus the per-compile counters its callbacks update. Parsers keep their string pool
    /// and scope buffers between parses, so one is cached per thread and option combination.
    /// </summary>
    private sealed class ParseSession
    {
        [ThreadStatic] private static ParseSession?[]? s_cache;

        public readonly Parser Parser;
        private readonly int _slot;
        private bool _inUse;
        public int Tokens, Nodes, Idents, Funcs, ScopeNodes;
        private string _name = "";
        private int _maxNodes;
        private long _deadline;
        private CancellationToken _ct;

        private ParseSession(int slot, (bool NewTarget, bool Super, bool SuperCall, bool CheckPrivate) o)
        {
            _slot = slot;
            Parser = new Parser(new ParserOptions
            {
                EcmaVersion = EcmaVersion.Latest,
                // stage-3 module proposals: `import source x from "m"` / `import.source()`, `import defer * as ns` / `import.defer()`
                ExperimentalESFeatures = ExperimentalESFeatures.SourcePhaseImports | ExperimentalESFeatures.DeferImportEvaluation | ExperimentalESFeatures.Decorators,
                AllowNewTargetOutsideFunction = o.NewTarget,
                AllowSuperOutsideMethod = o.Super,
                AllowSuperCallOutsideConstructor = o.SuperCall,
                CheckPrivateFields = o.CheckPrivate,
                RegExpParseMode = RegExpParseMode.Validate,
                // Acornima guards its own recursion (StackGuard → InsufficientExecutionStackException)
                OnToken = (in Token _) => { if ((++Tokens & 1023) == 0) CheckBudget(_name, _deadline, _ct); },
                OnNode = (Node n, in OnNodeContext _) =>
                {
                    // counts used to pre-size the analysis maps (avoids repeated dictionary growth)
                    switch (n.Type)
                    {
                        case NodeType.Identifier: Idents++; break;
                        case NodeType.FunctionDeclaration or NodeType.FunctionExpression or NodeType.ArrowFunctionExpression
                            or NodeType.StaticBlock or NodeType.ClassDeclaration or NodeType.ClassExpression:
                            Funcs++; ScopeNodes++; break;
                        case NodeType.BlockStatement or NodeType.ForStatement or NodeType.ForInStatement or NodeType.ForOfStatement
                            or NodeType.SwitchStatement or NodeType.CatchClause:
                            ScopeNodes++; break;
                    }
                    if (++Nodes > _maxNodes)
                        throw new JsCompileException("TooManyNodes", $"Source exceeds the AST node budget of {_maxNodes}.", _name, 0, 0);
                },
            });
        }

        public static ParseSession Rent((bool NewTarget, bool Super, bool SuperCall, bool CheckPrivate) o)
        {
            int slot = (o.NewTarget ? 1 : 0) | (o.Super ? 2 : 0) | (o.SuperCall ? 4 : 0) | (o.CheckPrivate ? 8 : 0);
            var cache = s_cache ??= new ParseSession?[16];
            var s = cache[slot];
            if (s == null || s._inUse) s = new ParseSession(slot, o);   // re-entrant compile: use a private parser
            else cache[slot] = null;
            s._inUse = true;
            return s;
        }

        public void Begin(string name, int maxNodes, long deadline, CancellationToken ct)
        {
            _name = name; _maxNodes = maxNodes; _deadline = deadline; _ct = ct;
            Tokens = Nodes = Idents = Funcs = ScopeNodes = 0;
        }

        public void Return()
        {
            _inUse = false;
            _ct = default;
            _name = "";
            // a parser that just handled a huge source keeps a large string pool: let it go instead of caching it
            if (Nodes > 100_000) return;
            var cache = s_cache ??= new ParseSession?[16];
            cache[_slot] ??= this;
        }

        public static void CheckBudget(string name, long deadline, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline != long.MaxValue && Stopwatch.GetTimestamp() > deadline)
                throw new JsCompileException("CompileTimeout", "Compilation exceeded its time budget.", name, 0, 0);
        }
    }

    internal static ScriptTemplate Compile(string source, JsCompileOptions options, bool isModule, CancellationToken ct, bool forceStrict = false,
        (bool AllowNewTarget, bool AllowSuper, bool InFieldInit, bool AllowSuperCall)? eval = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        string name = options.SourceName;
        if (source.Length > options.MaxSourceLength)
            throw new JsCompileException("SourceTooLarge", $"Source length {source.Length} exceeds the limit of {options.MaxSourceLength}.", name, 0, 0);

        long deadline = options.Timeout is { } to ? Stopwatch.GetTimestamp() + (long)(to.TotalSeconds * Stopwatch.Frequency) : long.MaxValue;
        var session = ParseSession.Rent(eval is { } e0 ? (e0.AllowNewTarget, e0.AllowSuper, e0.AllowSuperCall, false) : (false, false, false, true));
        session.Begin(name, options.MaxNodes, deadline, ct);
        var parser = session.Parser;
        Program program;
        try
        {
            program = isModule
                ? parser.ParseModule(source, name)
                : parser.ParseScript(source, name, strict: options.Strict || forceStrict);
        }
        catch (ParseErrorException pe)
        {
            throw new JsCompileException("SyntaxError", pe.Description ?? pe.Message, name, pe.LineNumber, pe.Column, pe);
        }
        catch (InsufficientExecutionStackException ise)
        {
            throw new JsCompileException("NestingTooDeep", "Source nesting is too deep to compile safely.", name, 0, 0, ise);
        }
        finally
        {
            session.Return();
        }
        int idents = session.Idents, funcs = session.Funcs, scopeNodes = session.ScopeNodes;

        var script = new ScriptTemplate
        {
            Body = null!,
            SourceName = name,
            Source = options.RetainSource ? source : null,
            IsModule = isModule,
            Strict = isModule || program.Strict || options.Strict || forceStrict,
        };
        ModuleRecordInfo? module = null;
        if (isModule)
        {
            try { module = BuildModuleRecord(program); }
            catch (CompileError ce) { throw new JsCompileException(ce.Code, ce.Message, name, ce.Line, ce.Column, ce); }
            script.Module = module;
        }
        var scopes = new Dictionary<Node, Jaunt.Compiler.Scope>(scopeNodes + 1);
        var functions = new Dictionary<Node, FunctionInfo>(funcs + 1);
        var refs = new Dictionary<Identifier, ResolvedRef>(idents);
        int counter = 0;
        Action checkpoint = () => { if ((++counter & 255) == 0) ParseSession.CheckBudget(name, deadline, ct); };
        try
        {
            var analyzer = new ScopeAnalyzer(scopes, functions, refs, module, options.MaxNestingDepth, checkpoint)
            {
                // a direct eval needs the identifier "eval" (possibly written with \u escapes) in the source
                MayContainEval = source.Contains("eval", StringComparison.Ordinal) || source.Contains("\\u", StringComparison.Ordinal),
            };
            if (eval is { } ev)
            {
                analyzer.AnalyzeEval(program, script.Strict);
                var egen = new CodeGen(script, source, scopes, functions, refs, analyzer, options.MaxNestingDepth, checkpoint);
                script.Body = egen.CompileEval(program, ev.AllowNewTarget, ev.InFieldInit);
                script.EvalContainsArguments = analyzer.EvalContainsArguments;
                if (analyzer.EvalPrivateNames != null) script.EvalPrivateNames = new List<string>(analyzer.EvalPrivateNames).ToArray();
                return script;
            }
            var fi = analyzer.AnalyzeProgram(program, isModule);
            if (options.Strict || forceStrict) fi.Strict = true;
            var gen = new CodeGen(script, source, scopes, functions, refs, analyzer, options.MaxNestingDepth, checkpoint);
            script.Body = gen.CompileProgram(program, isModule);
            if (isModule) ResolveExportSlots(module!);
        }
        catch (CompileError ce)
        {
            throw new JsCompileException(ce.Code, ce.Message, name, ce.Line, ce.Column, ce);
        }
        catch (InsufficientExecutionStackException ise)
        {
            throw new JsCompileException("NestingTooDeep", "Source nesting is too deep to compile safely.", name, 0, 0, ise);
        }
        return script;
    }

    private static ModuleRecordInfo BuildModuleRecord(Program program)
    {
        var m = new ModuleRecordInfo();
        void Request(string s, ModulePhase phase = ModulePhase.Evaluation)
        {
            for (int i = 0; i < m.RequestedModules.Count; i++)
                if (m.RequestedPhases[i] == phase && m.RequestedModules[i] == s) return;
            m.RequestedModules.Add(s);
            m.RequestedPhases.Add(phase);
        }
        foreach (var st in program.Body)
        {
            switch (st)
            {
                case ImportDeclaration imp:
                    Request(StaticRequestKey(imp.Source, imp.Attributes),
                        imp.Phase switch { ImportPhase.Defer => ModulePhase.Defer, ImportPhase.Source => ModulePhase.Source, _ => ModulePhase.Evaluation });
                    break;
                case ExportNamedDeclaration en:
                    if (en.Source != null)
                    {
                        var key = StaticRequestKey(en.Source, en.Attributes);
                        Request(key);
                        foreach (var spec in en.Specifiers)
                            m.IndirectExports.Add(new ExportEntryInfo
                            {
                                ExportName = ScopeAnalyzer.ModuleExportName(spec.Exported),
                                ModuleRequest = key,
                                ImportName = ScopeAnalyzer.ModuleExportName(spec.Local),
                            });
                    }
                    else if (en.Declaration != null)
                    {
                        foreach (var id in DeclaredNames(en.Declaration))
                            m.LocalExports.Add(new ExportEntryInfo { ExportName = id, LocalName = id });
                    }
                    else
                    {
                        foreach (var spec in en.Specifiers)
                            m.LocalExports.Add(new ExportEntryInfo
                            {
                                ExportName = ScopeAnalyzer.ModuleExportName(spec.Exported),
                                LocalName = ScopeAnalyzer.ModuleExportName(spec.Local),
                            });
                    }
                    break;
                case ExportDefaultDeclaration ed:
                {
                    string local = ed.Declaration switch
                    {
                        FunctionDeclaration { Id: { } fid } => fid.Name,
                        ClassDeclaration { Id: { } cid } => cid.Name,
                        _ => "*default*",
                    };
                    m.LocalExports.Add(new ExportEntryInfo { ExportName = "default", LocalName = local });
                    break;
                }
                case ExportAllDeclaration ea:
                {
                    var key = StaticRequestKey(ea.Source, ea.Attributes);
                    Request(key);
                    if (ea.Exported == null) m.StarExports.Add(new ExportEntryInfo { ModuleRequest = key });
                    else m.IndirectExports.Add(new ExportEntryInfo { ExportName = ScopeAnalyzer.ModuleExportName(ea.Exported), ModuleRequest = key, ImportName = null });
                    break;
                }
            }
        }
        return m;
    }

    private static IEnumerable<string> DeclaredNames(Declaration d)
    {
        switch (d)
        {
            case VariableDeclaration vd:
                foreach (var id in ScopeAnalyzer.PatternIdentifiers(vd)) yield return id.Name;
                break;
            case FunctionDeclaration fd when fd.Id != null: yield return fd.Id.Name; break;
            case ClassDeclaration cd when cd.Id != null: yield return cd.Id.Name; break;
        }
    }

    private static void ResolveExportSlots(ModuleRecordInfo m)
    {
        var converted = new List<ExportEntryInfo>();
        foreach (var e in m.LocalExports)
        {
            // `export { x }` where x is an import binding becomes an indirect export
            var imp = m.Imports.Find(i => i.LocalName == e.LocalName);
            if (imp != null)
            {
                converted.Add(e);
                m.IndirectExports.Add(new ExportEntryInfo { ExportName = e.ExportName, ModuleRequest = imp.ModuleRequest, ImportName = imp.ImportName, Phase = imp.Phase });
                continue;
            }
            if (!m.EnvSlots.TryGetValue(e.LocalName!, out int slot))
                throw new CompileError("UnresolvedExport", $"Export '{e.ExportName}' is not defined in module", null);
            e.LocalEnvIndex = slot;
        }
        foreach (var c in converted) m.LocalExports.Remove(c);
    }
}
