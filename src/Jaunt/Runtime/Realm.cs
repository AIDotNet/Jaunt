using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

internal sealed class GlobalCell
{
    public JsValue Value = JsValue.Empty;
    public bool IsConst;
}

/// <summary>
/// A realm: global object, intrinsics and realm-private feedback. Each <see cref="JsEngine"/> owns one realm;
/// nothing here is shared with other engines.
/// </summary>
internal sealed partial class Realm
{
    public readonly Vm Vm;
    public JsEngine? Engine;
    public readonly bool Hardened;

    // ---- intrinsics (lazily initialized by group; see IntrinsicGroup)
    private JsObject? _objectPrototype;
    public JsObject ObjectPrototype { get => _objectPrototype ?? Ensure(IntrinsicGroup.Object)._objectPrototype!; set => _objectPrototype = value; }
    private JsObject? _functionPrototype;
    public JsObject FunctionPrototype { get => _functionPrototype ?? Ensure(IntrinsicGroup.Function)._functionPrototype!; set => _functionPrototype = value; }
    private JsObject? _arrayPrototype;
    public JsObject ArrayPrototype { get => _arrayPrototype ?? Ensure(IntrinsicGroup.Array)._arrayPrototype!; set => _arrayPrototype = value; }
    private JsObject? _stringPrototype;
    public JsObject StringPrototype { get => _stringPrototype ?? Ensure(IntrinsicGroup.String)._stringPrototype!; set => _stringPrototype = value; }
    public JsObject[]? TypedArrayConstructors;
    public JsObject? IteratorConstructor;
    public JsObject? ArrayBufferConstructor;
    public JsObject? RegExpConstructor;
    /// <summary>
    /// Legacy RegExp static properties (proposal-regexp-legacy-features), recorded lazily: the last successful
    /// built-in match by a legacy-enabled RegExp of this realm; the properties are computed on read.
    /// </summary>
    internal RegExpBuiltins.RegexMatch? LegacyMatch;
    internal int LegacyGroupCount;
    /// <summary>Value assigned through RegExp.input / RegExp.$_ (overrides the last match's input).</summary>
    internal string? LegacyInput;
    /// <summary>Set by a match of a non-legacy (subclass) RegExp: the static properties throw until the next update.</summary>
    internal bool LegacyInvalid;
    public JsObject? DisposableStackPrototype;
    public JsObject? AsyncDisposableStackPrototype;
    public JsObject? SharedArrayBufferConstructor;
    public JsObject? SharedArrayBufferPrototype;
    public JsObject? WeakRefPrototype;
    public JsObject? FinalizationRegistryPrototype;
    public JsObject? ShadowRealmPrototype;
    /// <summary>Module map of a ShadowRealm's realm (ShadowRealm.prototype.importValue); null for the engine's own realm.</summary>
    internal Dictionary<string, ModuleInstance>? ModuleMap;
    public List<WeakReference<JsFinalizationRegistry>>? Registries;
    private List<object>? _keptObjects;

    /// <summary>AddToKeptObjects: targets observed through WeakRef stay alive until the current execution ends.</summary>
    public void KeepAlive(object o) => (_keptObjects ??= new List<object>()).Add(o);

    /// <summary>ClearKeptObjects; also lets FinalizationRegistry cleanup observe collected targets.</summary>
    public void ClearKeptObjects()
    {
        _keptObjects?.Clear();
        if (Registries != null && Registries.Count > 0) WeakRefBuiltins.EnqueueCleanup(this);
    }
    public JsObject? IteratorHelperPrototype;
    public JsObject? WrapForValidIteratorPrototype;
    private JsObject? _bigIntPrototype;
    public JsObject BigIntPrototype { get => _bigIntPrototype ?? Ensure(IntrinsicGroup.BigInt)._bigIntPrototype!; set => _bigIntPrototype = value; }
    private JsObject? _numberPrototype;
    public JsObject NumberPrototype { get => _numberPrototype ?? Ensure(IntrinsicGroup.Number)._numberPrototype!; set => _numberPrototype = value; }
    private JsObject? _booleanPrototype;
    public JsObject BooleanPrototype { get => _booleanPrototype ?? Ensure(IntrinsicGroup.Number)._booleanPrototype!; set => _booleanPrototype = value; }
    private JsObject? _symbolPrototype;
    public JsObject SymbolPrototype { get => _symbolPrototype ?? Ensure(IntrinsicGroup.Symbol)._symbolPrototype!; set => _symbolPrototype = value; }
    private JsObject? _iteratorPrototype;
    public JsObject IteratorPrototype { get => _iteratorPrototype ?? Ensure(IntrinsicGroup.Iterator)._iteratorPrototype!; set => _iteratorPrototype = value; }
    private JsObject? _arrayIteratorPrototype;
    public JsObject ArrayIteratorPrototype { get => _arrayIteratorPrototype ?? Ensure(IntrinsicGroup.Iterator)._arrayIteratorPrototype!; set => _arrayIteratorPrototype = value; }
    private JsObject? _stringIteratorPrototype;
    public JsObject StringIteratorPrototype { get => _stringIteratorPrototype ?? Ensure(IntrinsicGroup.Iterator)._stringIteratorPrototype!; set => _stringIteratorPrototype = value; }
    private JsObject? _mapIteratorPrototype;
    public JsObject MapIteratorPrototype { get => _mapIteratorPrototype ?? Ensure(IntrinsicGroup.MapSet)._mapIteratorPrototype!; set => _mapIteratorPrototype = value; }
    private JsObject? _setIteratorPrototype;
    public JsObject SetIteratorPrototype { get => _setIteratorPrototype ?? Ensure(IntrinsicGroup.MapSet)._setIteratorPrototype!; set => _setIteratorPrototype = value; }
    private JsObject? _regExpStringIteratorPrototype;
    public JsObject RegExpStringIteratorPrototype { get => _regExpStringIteratorPrototype ?? Ensure(IntrinsicGroup.RegExp)._regExpStringIteratorPrototype!; set => _regExpStringIteratorPrototype = value; }
    private JsObject? _generatorPrototype;
    public JsObject GeneratorPrototype { get => _generatorPrototype ?? Ensure(IntrinsicGroup.Iterator)._generatorPrototype!; set => _generatorPrototype = value; }
    private JsObject? _generatorFunctionPrototype;
    public JsObject GeneratorFunctionPrototype { get => _generatorFunctionPrototype ?? Ensure(IntrinsicGroup.FunctionKinds)._generatorFunctionPrototype!; set => _generatorFunctionPrototype = value; }
    private JsObject? _asyncFunctionPrototype;
    public JsObject AsyncFunctionPrototype { get => _asyncFunctionPrototype ?? Ensure(IntrinsicGroup.FunctionKinds)._asyncFunctionPrototype!; set => _asyncFunctionPrototype = value; }
    private JsObject? _asyncGeneratorFunctionPrototype;
    public JsObject AsyncGeneratorFunctionPrototype { get => _asyncGeneratorFunctionPrototype ?? Ensure(IntrinsicGroup.FunctionKinds)._asyncGeneratorFunctionPrototype!; set => _asyncGeneratorFunctionPrototype = value; }
    private JsObject? _asyncGeneratorPrototype;
    public JsObject AsyncGeneratorPrototype { get => _asyncGeneratorPrototype ?? Ensure(IntrinsicGroup.Iterator)._asyncGeneratorPrototype!; set => _asyncGeneratorPrototype = value; }
    private JsObject? _asyncIteratorPrototype;
    public JsObject AsyncIteratorPrototype { get => _asyncIteratorPrototype ?? Ensure(IntrinsicGroup.Iterator)._asyncIteratorPrototype!; set => _asyncIteratorPrototype = value; }
    private JsObject? _asyncFromSyncIteratorPrototype;
    public JsObject AsyncFromSyncIteratorPrototype { get => _asyncFromSyncIteratorPrototype ?? Ensure(IntrinsicGroup.Iterator)._asyncFromSyncIteratorPrototype!; set => _asyncFromSyncIteratorPrototype = value; }
    private JsObject? _promisePrototype;
    public JsObject PromisePrototype { get => _promisePrototype ?? Ensure(IntrinsicGroup.Promise)._promisePrototype!; set => _promisePrototype = value; }
    private JsObject? _promiseConstructor;
    public JsObject PromiseConstructor { get => _promiseConstructor ?? Ensure(IntrinsicGroup.Promise)._promiseConstructor!; set => _promiseConstructor = value; }
    private JsObject? _mapPrototype;
    public JsObject MapPrototype { get => _mapPrototype ?? Ensure(IntrinsicGroup.MapSet)._mapPrototype!; set => _mapPrototype = value; }
    private JsObject? _setPrototype;
    public JsObject SetPrototype { get => _setPrototype ?? Ensure(IntrinsicGroup.MapSet)._setPrototype!; set => _setPrototype = value; }
    private JsObject? _weakMapPrototype;
    public JsObject WeakMapPrototype { get => _weakMapPrototype ?? Ensure(IntrinsicGroup.MapSet)._weakMapPrototype!; set => _weakMapPrototype = value; }
    private JsObject? _weakSetPrototype;
    public JsObject WeakSetPrototype { get => _weakSetPrototype ?? Ensure(IntrinsicGroup.MapSet)._weakSetPrototype!; set => _weakSetPrototype = value; }
    private JsObject? _regExpPrototype;
    public JsObject RegExpPrototype { get => _regExpPrototype ?? Ensure(IntrinsicGroup.RegExp)._regExpPrototype!; set => _regExpPrototype = value; }
    private JsObject? _datePrototype;
    public JsObject DatePrototype { get => _datePrototype ?? Ensure(IntrinsicGroup.Date)._datePrototype!; set => _datePrototype = value; }
    private JsObject? _arrayConstructor;
    public JsObject ArrayConstructor { get => _arrayConstructor ?? Ensure(IntrinsicGroup.Array)._arrayConstructor!; set => _arrayConstructor = value; }
    private JsObject? _objectConstructor;
    public JsObject ObjectConstructor { get => _objectConstructor ?? Ensure(IntrinsicGroup.Object)._objectConstructor!; set => _objectConstructor = value; }
    private JsObject? _functionConstructor;
    public JsObject FunctionConstructor { get => _functionConstructor ?? Ensure(IntrinsicGroup.Function)._functionConstructor!; set => _functionConstructor = value; }
    private JsObject? _arrayBufferPrototype;
    public JsObject ArrayBufferPrototype { get => _arrayBufferPrototype ?? Ensure(IntrinsicGroup.TypedArray)._arrayBufferPrototype!; set => _arrayBufferPrototype = value; }
    private JsObject? _uint8ArrayPrototype;
    public JsObject Uint8ArrayPrototype { get => _uint8ArrayPrototype ?? Ensure(IntrinsicGroup.TypedArray)._uint8ArrayPrototype!; set => _uint8ArrayPrototype = value; }
    private JsBuiltin? _arrayProtoValues;
    public JsBuiltin ArrayProtoValues { get => _arrayProtoValues ?? Ensure(IntrinsicGroup.Array)._arrayProtoValues!; set => _arrayProtoValues = value; }
    private JsBuiltin? _arrayIteratorNext;
    public JsBuiltin ArrayIteratorNext { get => _arrayIteratorNext ?? Ensure(IntrinsicGroup.Iterator)._arrayIteratorNext!; set => _arrayIteratorNext = value; }
    private JsBuiltin? _throwTypeErrorFunction;
    public JsBuiltin ThrowTypeErrorFunction { get => _throwTypeErrorFunction ?? Ensure(IntrinsicGroup.Function)._throwTypeErrorFunction!; set => _throwTypeErrorFunction = value; }
    private JsBuiltin? _evalFunction;
    public JsBuiltin EvalFunction { get => _evalFunction ?? Ensure(IntrinsicGroup.Globals)._evalFunction!; set => _evalFunction = value; }
    private JsObject[]? _errorPrototypes;
    public JsObject[] ErrorPrototypes => _errorPrototypes ?? Ensure(IntrinsicGroup.Error)._errorPrototypes!;
    internal void SetErrorPrototypes(JsObject[] protos) => _errorPrototypes = protos;
    private JsObject? _functionPrototypeHasInstance;
    public JsObject? FunctionPrototypeHasInstance { get => _functionPrototypeHasInstance; set => _functionPrototypeHasInstance = value; }

    public JsObject GlobalObject = null!;
    public readonly Dictionary<string, GlobalCell> GlobalLex = new(StringComparer.Ordinal);
    public int GlobalVersion;
    /// <summary>Prototype-mutation epoch; shared by every realm on the Vm so ICs see cross-realm prototype changes.</summary>
    public ref int ProtoEpoch => ref Vm.ProtoEpoch;

    public Shape ObjectRootShape = null!;
    public Shape IterResultShape = null!;
    /// <summary>GlobalSymbolRegistry: agent-wide, so shared with every realm created on the same Vm.</summary>
    public readonly Dictionary<string, JsSymbol> SymbolRegistry;

    // ---- accounting
    /// <summary>
    /// The tracked-bytes budget, shared by every realm of an engine (ShadowRealm, CreateRealm): a script cannot escape
    /// <c>MaxTrackedBytes</c> by allocating in new realms.
    /// </summary>
    internal sealed class ByteBudget
    {
        public long Used;
        public long Max = long.MaxValue;
    }

    internal readonly ByteBudget Budget;
    public long TrackedBytes { get => Budget.Used; set => Budget.Used = value; }
    public long MaxTrackedBytes { get => Budget.Max; set => Budget.Max = value; }
    public int MaxStringLength = (1 << 29) - 24;
    public uint MaxArrayLength = 100_000_000;
    private bool _accounting;

    /// <summary>
    /// Feedback of the shared scripts this realm has run. A plain dictionary owned by the realm: a ConditionalWeakTable
    /// keyed by the (long-lived, shared) templates would keep every realm alive, because the feedback's inline caches
    /// reference the realm's objects and a weak table's values live as long as their keys.
    /// </summary>
    private Dictionary<ScriptTemplate, Feedback>? _scriptFeedback;
    private ScriptTemplate? _lastScript;
    private Feedback? _lastFeedback;
    internal readonly List<JsPromise> UnhandledRejections = new();

    /// <summary>Non-null while intrinsics are being created (bulk layout mode).</summary>
    internal List<JsObject>? BulkObjects;
    private BulkTable? _bulkFill;          // object → filled slot count (predicted layout), -1 = generic path
    private BulkTable? _bulkOrdinal;       // object → ordinal within the current group

    /// <summary>Tiny identity map for intrinsic setup (no object hash codes: avoids header inflation).</summary>
    private sealed class BulkTable
    {
        private JsObject[] _keys = new JsObject[16];
        private int[] _values = new int[16];
        private int _count;

        public bool TryGetValue(JsObject o, out int value)
        {
            for (int i = _count - 1; i >= 0; i--)
                if (ReferenceEquals(_keys[i], o)) { value = _values[i]; return true; }
            value = 0;
            return false;
        }

        public int this[JsObject o]
        {
            set
            {
                for (int i = _count - 1; i >= 0; i--)
                    if (ReferenceEquals(_keys[i], o)) { _values[i] = value; return; }
                if (_count == _keys.Length) { Array.Resize(ref _keys, _count * 2); Array.Resize(ref _values, _count * 2); }
                _keys[_count] = o;
                _values[_count++] = value;
            }
        }
    }
    private int _bulkGroup;
    private int _bulkNext;

    /// <summary>Process-wide builtin layouts: immutable key/flag arrays only (no realm references).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), (PropertyKey[] Keys, PropFlags[] Flags)> s_sharedLayouts = new();

    private void BeginBulk(int group)
    {
        BulkObjects = new List<JsObject>(16);
        _bulkFill = new BulkTable();
        _bulkOrdinal = new BulkTable();
        _bulkGroup = group;
        _bulkNext = 0;
    }

    /// <summary>
    /// Intrinsic setup fast path: the first realm records the final layout of every builtin object; later realms
    /// write values straight into slots of that (immutable, shared key/flag) layout, with no transitions and no
    /// intermediate dictionaries. Any divergence falls back to the generic path.
    /// </summary>
    internal bool TryBulkFill(JsObject o, in PropertyKey key, JsValue value, PropFlags flags)
    {
        var fill = _bulkFill!;
        if (fill.TryGetValue(o, out int filled))
        {
            if (filled < 0) return false;
            var shape = o.Shape;
            if (filled < shape.Count && shape.Flags[filled] == flags && shape.Keys[filled].Equals(key))
            {
                o.Slots[filled] = value;
                fill[o] = filled + 1;
                return true;
            }
            o.AbandonPredictedLayout(filled);
            fill[o] = -1;
            return false;
        }
        if (o.Dict != null || o.Shape.IsUnique || o.Shape.IsShared && o.Shape.Count != 0 && !(o is JsBuiltin && o.Shape.Count == 2)) return false;
        int ordinal = _bulkNext++;
        _bulkOrdinal![o] = ordinal;
        var proto = o.Proto;
        if (s_sharedLayouts.TryGetValue((_bulkGroup, ordinal), out var cached) && cached.Keys.Length > o.Shape.Count)
        {
            int existing = o.Shape.Count;
            bool prefixOk = true;
            for (int i = 0; i < existing; i++)
                if (!cached.Keys[i].Equals(o.Shape.Keys[i]) || cached.Flags[i] != o.Shape.Flags[i]) { prefixOk = false; break; }
            if (prefixOk && cached.Keys[existing].Equals(key) && cached.Flags[existing] == flags)
            {
                o.InstallPredictedLayout(Shape.CreateFromArrays(proto, cached.Keys, cached.Flags), existing);
                o.Slots[existing] = value;
                fill[o] = existing + 1;
                BulkObjects!.Add(o);
                return true;
            }
        }
        fill[o] = -1;
        return false;
    }

    private void EndBulk()
    {
        var list = BulkObjects!;
        var fill = _bulkFill!;
        var ordinals = _bulkOrdinal!;
        int group = _bulkGroup;
        BulkObjects = null;
        _bulkFill = null;
        _bulkOrdinal = null;
        foreach (var o in list)
        {
            if (ReferenceEquals(o, GlobalObject)) continue;
            if (fill.TryGetValue(o, out int filled) && filled >= 0)
            {
                if (filled != o.Shape.Count) o.AbandonPredictedLayout(filled); // predicted layout not completed
                else { if (o.IsPrototypeObject) ProtoEpoch++; continue; }
            }
            o.CompactToShape(null);
            if (o.Dict == null && ordinals.TryGetValue(o, out int ordinal)) s_sharedLayouts.TryAdd((group, ordinal), (o.Shape.Keys, o.Shape.Flags));
            if (o.IsPrototypeObject) ProtoEpoch++;
        }
    }


    public Realm(bool hardened) : this(hardened, null) { }

    /// <summary>Creates a realm; with <paramref name="parent"/>, a new realm on the parent's Vm (same agent).</summary>
    public Realm(bool hardened, Realm? parent)
    {
        Hardened = hardened;
        Budget = parent?.Budget ?? new ByteBudget();
        if (parent == null)
        {
            Vm = new Vm(this);
            SymbolRegistry = new Dictionary<string, JsSymbol>(StringComparer.Ordinal);
        }
        else
        {
            Vm = parent.Vm;
            SymbolRegistry = parent.SymbolRegistry;
            Engine = parent.Engine;
            MaxStringLength = parent.MaxStringLength;
            TimeZone = parent.TimeZone;
            Clock = parent.Clock;
        }
        var savedRealm = Vm.Realm;
        Vm.Realm = this;
        BeginBulk(-1);
        Intrinsics.Setup(this);
        EndBulk();
        if (hardened) Intrinsics.Harden(this);
        _accounting = true;
        Vm.Realm = savedRealm;
        // a root realm's setup is the engine's own baseline; a further realm's setup is charged to the shared budget
        if (parent == null) TrackedBytes = 0;
        else if (TrackedBytes > MaxTrackedBytes) OverBudget();
    }

    // ------------------------------------------------------------------ lazy intrinsic groups

    private readonly bool[] _groupsInitialized = new bool[(int)IntrinsicGroup.Count_];

    internal void MarkGroupInitialized(IntrinsicGroup g) => _groupsInitialized[(int)g] = true;

    /// <summary>Initializes a builtin group if needed; allocations are not charged to user code.</summary>
    internal Realm Ensure(IntrinsicGroup g)
    {
        if (_groupsInitialized[(int)g]) return this;
        _groupsInitialized[(int)g] = true;
        long saved = TrackedBytes;
        bool acc = _accounting;
        _accounting = false;
        int savedVersion = GlobalVersion;
        var outer = (BulkObjects, _bulkFill, _bulkOrdinal, _bulkGroup, _bulkNext);
        BeginBulk((int)g);
        try { Intrinsics.SetupGroup(this, g); }
        finally
        {
            EndBulk();
            (BulkObjects, _bulkFill, _bulkOrdinal, _bulkGroup, _bulkNext) = outer;
            TrackedBytes = saved;
            _accounting = acc;
            GlobalVersion = savedVersion + 1;
        }
        return this;
    }

    internal void EnsureGlobal(string name)
    {
        if (Intrinsics.GlobalGroups.TryGetValue(name, out var g) && !_groupsInitialized[(int)g]) Ensure(g);
    }

    internal void EnsureAllGroups()
    {
        for (int i = 0; i < (int)IntrinsicGroup.Count_; i++) Ensure((IntrinsicGroup)i);
    }

    // ------------------------------------------------------------------ shapes

    public Shape RootShapeFor(JsObject? proto)
    {
        if (proto == null) return _nullRoot ??= Shape.CreateRoot(null);
        if (ReferenceEquals(proto, _objectPrototype)) return Shape.SharedObjectRoot;
        if (ReferenceEquals(proto, _functionPrototype)) return Shape.SharedFunctionRoot;
        if (ReferenceEquals(proto, _arrayPrototype)) return Shape.SharedArrayRoot;
        var s = proto.ChildRootShape;
        if (s != null) return s;
        proto.IsPrototypeObject = true;
        return proto.ChildRootShape = Shape.CreateRoot(proto);
    }

    private Shape? _nullRoot;

    public Shape FunctionShapeFor(JsObject proto)
    {
        if (ReferenceEquals(proto, _functionPrototype)) return Shape.SharedFunctionShape;
        var s = proto.FunctionChildShape;
        if (s != null) return s;
        return proto.FunctionChildShape = RootShapeFor(proto).AddProperty("length", PropFlags.Configurable)!.AddProperty("name", PropFlags.Configurable)!;
    }

    /// <summary>Resolves a shared shape's prototype role to this realm's intrinsic.</summary>
    internal JsObject? RoleProto(ProtoRole role) => role switch
    {
        ProtoRole.RealmObjectPrototype => _objectPrototype,
        ProtoRole.RealmFunctionPrototype => _functionPrototype,
        _ => ArrayPrototype,
    };

    // ------------------------------------------------------------------ accounting

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ChargeObject()
    {
        var b = Budget;
        b.Used += 56;
        if (b.Used > b.Max && _accounting) OverBudget();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ChargeProperty()
    {
        var b = Budget;
        b.Used += 24;
        if (b.Used > b.Max && _accounting) OverBudget();
    }

    public void ChargeBytes(long bytes)
    {
        var b = Budget;
        b.Used += bytes;
        if (b.Used > b.Max && _accounting) OverBudget();
    }

    /// <summary>Charges a string a builtin has just created (see <see cref="ChargeOutput"/>) and returns it.</summary>
    public string Charged(string created)
    {
        ChargeOutput(created.Length);
        return created;
    }

    /// <summary>
    /// Charges a builtin's output of <paramref name="chars"/> UTF-16 units before (or while) producing it: tracked bytes
    /// (2 per unit) and VM work (1 unit per 256 chars), so one large string operation is bounded by MaxTrackedBytes and
    /// reaches a Timeout/Interrupt checkpoint like a loop of small ones.
    /// </summary>
    public void ChargeOutput(long chars)
    {
        if (chars <= 0) return;
        ChargeBytes(chars * 2);
        if (chars >= 256) Vm.Tick((int)Math.Min(int.MaxValue, chars / 256));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OverBudget() =>
        throw new JsTerminationException(TerminationKind.TrackedBytes, $"Tracked allocation budget of {MaxTrackedBytes} bytes exceeded.", TrackedBytes, MaxTrackedBytes);

    // ------------------------------------------------------------------ strings

    private static readonly string[] CharStrings = BuildChars();
    private static string[] BuildChars()
    {
        var a = new string[128];
        for (int i = 0; i < a.Length; i++) a[i] = ((char)i).ToString();
        return a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string CharString(char c) => c < 128 ? CharStrings[c] : c.ToString();

    // ------------------------------------------------------------------ feedback

    public Feedback FeedbackFor(ScriptTemplate script)
    {
        if (ReferenceEquals(script, _lastScript)) return _lastFeedback!;
        Feedback fb;
        if (_lastScript == null)
        {
            // first script/module of this realm: no table needed yet
            fb = new Feedback(script.Body);
        }
        else
        {
            _scriptFeedback ??= new Dictionary<ScriptTemplate, Feedback>(ReferenceEqualityComparer.Instance);
            if (_lastFeedback != null) _scriptFeedback.TryAdd(_lastScript, _lastFeedback);
            if (!_scriptFeedback.TryGetValue(script, out fb!))
            {
                fb = new Feedback(script.Body);
                _scriptFeedback[script] = fb;
            }
        }
        _lastScript = script;
        _lastFeedback = fb;
        return fb;
    }

    // ------------------------------------------------------------------ globals

    public void InitGlobalLexical(string name, JsValue value)
    {
        if (!GlobalLex.TryGetValue(name, out var cell))
        {
            cell = new GlobalCell();
            GlobalLex[name] = cell;
            GlobalVersion++;
        }
        cell.Value = value;
    }

    /// <summary>GlobalDeclarationInstantiation (ECMA-262 16.1.7) for a classic script.</summary>
    public void InstantiateScript(ScriptTemplate s)
    {
        var g = GlobalObject;
        foreach (var n in s.LexNames) EnsureGlobal(n);
        foreach (var n in s.VarNames) EnsureGlobal(n);
        foreach (var n in s.FunctionNames) EnsureGlobal(n);
        for (int i = 0; i < s.LexNames.Length; i++)
        {
            var name = s.LexNames[i];
            if (GlobalLex.ContainsKey(name)) throw JsErrors.SyntaxError(this, $"Identifier '{name}' has already been declared");
            int idx = g.FindOwn(name, out var f);
            if (idx >= 0 && (f & PropFlags.Configurable) == 0) throw JsErrors.SyntaxError(this, $"Identifier '{name}' has already been declared");
        }
        foreach (var name in s.VarNames)
            if (GlobalLex.ContainsKey(name)) throw JsErrors.SyntaxError(this, $"Identifier '{name}' has already been declared");
        foreach (var name in s.FunctionNames)
            if (GlobalLex.ContainsKey(name)) throw JsErrors.SyntaxError(this, $"Identifier '{name}' has already been declared");
        // CanDeclareGlobalFunction / CanDeclareGlobalVar: checked before any binding is created
        for (int i = s.FunctionNames.Length - 1; i >= 0; i--)
        {
            var name = s.FunctionNames[i];
            if (g.GetOwnProperty(name, out var d))
            {
                if (!d.Configurable && (d.IsAccessor || !d.Writable || !d.Enumerable))
                    throw JsErrors.TypeError(this, $"Cannot redefine global function '{name}'");
            }
            else if (!g.IsExtensibleCheck()) throw JsErrors.TypeError(this, $"Cannot define global function '{name}'");
        }
        foreach (var name in s.VarNames)
            if (!g.HasOwnProperty(name) && !g.IsExtensibleCheck()) throw JsErrors.TypeError(this, $"Cannot define global variable '{name}'");

        foreach (var name in s.FunctionNames)
        {
            int idx = g.FindOwn(name, out var f);
            if (idx < 0 || (f & PropFlags.Configurable) != 0)
                g.DefinePropertyOrThrow(name, PropertyDescriptor.Data(JsValue.Undefined, PropFlags.Writable | PropFlags.Enumerable));
        }
        foreach (var name in s.VarNames)
        {
            if (!g.HasOwnProperty(name))
                g.DefinePropertyOrThrow(name, PropertyDescriptor.Data(JsValue.Undefined, PropFlags.Writable | PropFlags.Enumerable));
        }
        for (int i = 0; i < s.LexNames.Length; i++)
        {
            GlobalLex[s.LexNames[i]] = new GlobalCell { IsConst = s.LexIsConst[i] };
        }
        if (s.LexNames.Length > 0) GlobalVersion++;
    }

    // ------------------------------------------------------------------ helpers

    internal JsBuiltin CreateBuiltinUncharged(LazyFunction lf)
    {
        long saved = TrackedBytes;
        var f = new JsBuiltin(this, lf.Name, lf.Length, lf.Fn);
        TrackedBytes = saved;
        return f;
    }

    public JsBuiltin Function(string name, int length, NativeFn fn, bool constructor = false)
        => new(this, name, length, fn, constructor);

    /// <summary>Defines a builtin method whose function object is created lazily on first read.</summary>
    public void Method(JsObject target, string name, int length, NativeFn fn)
    {
        target.AddOwnProperty(name, new JsValue(JsTag.Empty, 0, new LazyFunction(name, length, fn)), PropFlags.Hidden);
    }

    /// <summary>Defines a builtin method eagerly and returns the function object.</summary>
    public JsBuiltin MethodEager(JsObject target, string name, int length, NativeFn fn)
    {
        var f = new JsBuiltin(this, name, length, fn);
        target.DefineHidden(name, JsValue.FromObject(f));
        return f;
    }

    public JsBuiltin Method(JsObject target, JsSymbol sym, string name, int length, NativeFn fn, PropFlags flags = PropFlags.Hidden)
    {
        var f = new JsBuiltin(this, name, length, fn);
        target.AddOwnProperty(sym, JsValue.FromObject(f), flags);
        return f;
    }

    public void Getter(JsObject target, PropertyKey key, string name, NativeFn fn, NativeFn? setter = null)
    {
        var g = new JsBuiltin(this, "get " + name, 0, fn);
        JsBuiltin? s = setter == null ? null : new JsBuiltin(this, "set " + name, 1, setter);
        target.DefineAccessor(key, g, s, PropFlags.Configurable);
    }

    /// <summary>Creates a constructor function with its prototype object linked both ways.</summary>
    public JsBuiltin Constructor(string name, int length, NativeFn fn, JsObject prototype, JsObject? ctorProto = null, bool readOnlyConstructorLink = false)
    {
        var c = new JsBuiltin(this, name, length, fn, constructor: true, proto: ctorProto);
        c.AddOwnProperty("prototype", JsValue.FromObject(prototype), PropFlags.None);
        if (readOnlyConstructorLink) prototype.AddOwnProperty("constructor", JsValue.FromObject(c), PropFlags.Configurable);
        else prototype.DefineHidden("constructor", JsValue.FromObject(c));
        return c;
    }

    public void DefineGlobal(string name, JsValue value) => GlobalObject.DefineHidden(name, value);

    public JsArray NewArray(int capacity = 0) => new(this, capacity);

    public JsObject NewObject() => new(this, ObjectRootShape, 0);

    // ------------------------------------------------------------------ array iteration fast path guard

    private int _pristineEpoch = -1;
    private int _iterSlot = -1, _nextSlot = -1;

    public bool IsArrayIterationPristine(JsArray arr)
    {
        if (!ReferenceEquals(arr.Proto, ArrayPrototype) || arr.Dict != null && arr.Dict.IndexOf(JsSymbol.Iterator) >= 0) return false;
        if (arr.Shape.HasSymbolKeys) return false;
        if (_pristineEpoch != ProtoEpoch)
        {
            _iterSlot = ArrayPrototype.FindOwn(JsSymbol.Iterator, out _);
            _nextSlot = ArrayIteratorPrototype.FindOwn("next", out _);
            _pristineEpoch = ProtoEpoch;
        }
        if (_iterSlot < 0 || _nextSlot < 0) return false;
        return ReferenceEquals(ArrayPrototype.ReadSlot(_iterSlot).Ref, ArrayProtoValues)
            && ReferenceEquals(ArrayIteratorPrototype.ReadSlot(_nextSlot).Ref, ArrayIteratorNext);
    }

    // ------------------------------------------------------------------ promise rejection tracking

    public void TrackRejection(JsPromise p, bool handled)
    {
        if (handled) UnhandledRejections.Remove(p);
        else UnhandledRejections.Add(p);
    }
}
