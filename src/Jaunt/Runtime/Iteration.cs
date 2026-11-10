using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>
/// Iterator record (ECMA-262 7.4.1) with a fast path for unmodified array iteration. Held in registers as an
/// internal value; never visible to JS.
/// </summary>
internal sealed class IteratorRecord
{
    private readonly Realm _realm;
    public JsObject? Iterator;
    public JsValue NextMethod;
    public bool Done;
    // array fast path
    private JsArray? _array;
    private uint _index;
    // Map/Set fast path: the table is walked directly, no iterator or result objects
    private OrderedTable? _table;
    private TableCursor? _cursor;
    private bool _entries;
    // string fast path: code points are produced directly, no iterator or result objects
    private string? _string;
    private int _strIndex;
    // generator fast path: Iterator is a generator and NextMethod its original next: it is resumed without an iterator result object
    private bool _gen;

    private IteratorRecord(Realm realm) => _realm = realm;

    public static IteratorRecord Get(Realm realm, in JsValue iterable)
    {
        var rec = new IteratorRecord(realm);
        if (iterable.IsObject)
        {
            if (iterable.Ref is JsArray arr && realm.IsArrayIterationPristine(arr))
            {
                rec._array = arr;
                return rec;
            }
            if (iterable.Ref is JsMap m && realm.IsMapIterationPristine(m))
            {
                rec._table = m.Table;
                rec._cursor = m.Table.CreateCursor();
                rec._entries = !m.IsSet;
                return rec;
            }
            if (iterable.Ref is JsGenerator pg && realm.IsGeneratorIterationPristine(pg))
            {
                rec.Iterator = pg;
                rec.NextMethod = JsValue.FromObject(realm.GeneratorNext!);
                rec._gen = true;
                return rec;
            }
        }
        else if (iterable.IsString && realm.IsStringIterationPristine())
        {
            rec._string = iterable.AsStringUnchecked;
            return rec;
        }
        var method = Operations.GetV(realm, iterable, JsSymbol.Iterator);
        if (!method.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(iterable)} is not iterable");
        // %IteratorPrototype%[@@iterator] returns its this value and does nothing else
        var it = ReferenceEquals(method.Ref, realm.IteratorProtoIterator) ? iterable : realm.Vm.Call(method, iterable, ReadOnlySpan<JsValue>.Empty);
        if (it.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.iterator method is not an object");
        rec.Iterator = it.AsObjectUnchecked;
        rec.NextMethod = rec.Iterator.Get("next", it);
        if (rec.Iterator is JsGenerator && ReferenceEquals(rec.NextMethod.Ref, realm.GeneratorNext)) rec._gen = true;
        return rec;
    }

    public static IteratorRecord FromIterator(Realm realm, JsObject iterator)
    {
        var rec = new IteratorRecord(realm) { Iterator = iterator };
        rec.NextMethod = iterator.Get("next");
        if (iterator is JsGenerator && ReferenceEquals(rec.NextMethod.Ref, realm.GeneratorNext)) rec._gen = true;
        return rec;
    }

    /// <summary>IteratorStep + IteratorValue. Returns false when done.</summary>
    public bool Step(out JsValue value)
    {
        if (Done) { value = default; return false; }
        if (_array != null)
        {
            var a = _array;
            if (_index < a.Length)
            {
                if (!a.TryGetElement(_index, out value)) value = a.GetIndex(_index, JsValue.FromObject(a));
                _index++;
                return true;
            }
            Done = true;
            value = default;
            return false;
        }
        if (_string != null)
        {
            var s = _string;
            int i = _strIndex;
            if (i < s.Length)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { value = new JsValue(s.Substring(i, 2)); _strIndex = i + 2; }
                else { value = new JsValue(_realm.CharString(c)); _strIndex = i + 1; }
                return true;
            }
            Done = true;
            value = default;
            return false;
        }
        if (_table != null)
        {
            if (_table.Next(_cursor!, out var key, out var val))
            {
                if (_entries)
                {
                    var pair = _realm.NewArray(2);
                    pair.Push(key);
                    pair.Push(val);
                    value = JsValue.FromObject(pair);
                }
                else value = key;
                return true;
            }
            Done = true;
            value = default;
            return false;
        }
        // any abrupt completion of IteratorStep / IteratorValue marks the record done (no IteratorClose)
        Done = true;
        JsValue r;
        if (_gen)
        {
            int kind = Coroutines.ResumeStep(_realm, (JsGenerator)Iterator!, Coroutines.ModeNext, JsValue.Undefined, out r);
            if (kind == Coroutines.StepYield)
            {
                value = r;
                Done = false;
                return true;
            }
            if (kind == Coroutines.StepDone)
            {
                value = default;
                return false;
            }
            // yield* handed out its inner iterator's result object: read it as below
        }
        else r = CallNext();
        if (r.IsNotObject) throw JsErrors.TypeError(_realm, $"Iterator result {r} is not an object");
        var ro = r.AsObjectUnchecked;
        var layout = _realm.IterResultLayout;
        if (ReferenceEquals(ro.Shape, layout.Shape) && ro.Dict == null && !ro.IsExotic)
        {
            // own data properties: reading them runs no code
            if (Operations.ToBoolean(ro.ReadSlot(layout.DoneSlot)))
            {
                value = default;
                return false;
            }
            value = ro.ReadSlot(layout.ValueSlot);
            Done = false;
            return true;
        }
        LearnResultShape(ro);
        if (Operations.ToBoolean(ro.Get("done", r)))
        {
            value = default;
            return false;
        }
        value = ro.Get("value", r);
        Done = false;
        return true;
    }

    /// <summary>
    /// Calls next() with no arguments. A plain closure is called directly (<see cref="Vm.CallCallback"/>), as builtins call
    /// their callbacks; it is looked at once per record, as NextMethod never changes. The record can be stepped from
    /// different native depths (a generator holding it is resumed from anywhere), so the stack is checked per call.
    /// </summary>
    private JsValue CallNext()
    {
        var vm = _realm.Vm;
        var c = _nextClosure;
        if (c == null)
        {
            if (_nextChecked || !vm.TryPrepareFastCalls(NextMethod, out c))
            {
                _nextChecked = true;
                return vm.Call(NextMethod, JsValue.FromObject(Iterator!), ReadOnlySpan<JsValue>.Empty);
            }
            _nextClosure = c;
        }
        else vm.EnsureNativeStack();
        return vm.CallCallback(c, JsValue.FromObject(Iterator!), default, default, default, default, 0);
    }

    private JsClosure? _nextClosure;
    private bool _nextChecked;

    /// <summary>Learns the layout of an iterator result whose done and value are own data properties (see <see cref="Realm.IterResultLayout"/>).</summary>
    private void LearnResultShape(JsObject ro)
    {
        var shape = ro.Shape;
        if (ro.Dict != null || ro.IsExotic || shape.IsUnique) return;
        int d = shape.IndexOf("done"), v = shape.IndexOf("value");
        if (d < 0 || v < 0 || ((shape.Flags[d] | shape.Flags[v]) & PropFlags.Accessor) != 0) return;
        _realm.IterResultLayout = new IterResultLayout(shape, d, v);
    }

    /// <summary>IteratorClose with a normal completion.</summary>
    public void Close()
    {
        if (Done) return;
        Done = true;
        if (Iterator == null) return;
        var ret = Iterator.Get("return");
        if (ret.IsNullOrUndefined) return;
        // %GeneratorPrototype%.return always produces an object; only its effects on the generator count
        if (_gen && ReferenceEquals(ret.Ref, _realm.GeneratorReturn))
        {
            Coroutines.ResumeStep(_realm, (JsGenerator)Iterator!, Coroutines.ModeReturn, JsValue.Undefined, out _);
            return;
        }
        var r = _realm.Vm.Call(ret, JsValue.FromObject(Iterator), ReadOnlySpan<JsValue>.Empty);
        if (r.IsNotObject) throw JsErrors.TypeError(_realm, "Iterator result is not an object");
    }

    /// <summary>IteratorClose with a throw completion: errors from return() are suppressed.</summary>
    public void CloseQuiet()
    {
        if (Done) return;
        Done = true;
        if (Iterator == null) return;
        try
        {
            var ret = Iterator.Get("return");
            if (!ret.IsNullOrUndefined) _realm.Vm.Call(ret, JsValue.FromObject(Iterator), ReadOnlySpan<JsValue>.Empty);
        }
        catch (JsThrowException) { }
    }
}

/// <summary>An iterator result layout: done and value are own data properties at these slots of objects with this shape.</summary>
internal sealed class IterResultLayout
{
    public readonly Shape? Shape;
    public readonly int DoneSlot, ValueSlot;
    public IterResultLayout(Shape? shape, int doneSlot, int valueSlot) { Shape = shape; DoneSlot = doneSlot; ValueSlot = valueSlot; }
    public static readonly IterResultLayout None = new(null, 0, 0);
}

/// <summary>for-in key enumerator (enumerable string keys along the prototype chain, shadowing aware).</summary>
internal sealed class ForInIterator
{
    private readonly JsObject? _root;
    private JsObject? _current;
    private List<PropertyKey>? _keys;
    private int _pos;
    private HashSet<string>? _visited;
    // Fast mode for an ordinary object in shape mode: its enumerable string keys come from the shape's cache, nothing
    // is allocated per key, and the prototypes are only looked at once the own keys are done (see NextFast).
    private string[]? _fast;
    private Shape? _fastShape;

    public ForInIterator(JsObject? obj)
    {
        _root = obj;
        _current = obj;
        if (obj == null) return;
        if (obj.GetType() == typeof(JsObject) && obj.Dict == null && !obj.Shape.IsUnique)
        {
            var s = obj.Shape;
            _fast = s.EnumKeys ?? ComputeEnumKeys(s);
            _fastShape = s;
            return;
        }
        _keys = new();
        _visited = new(StringComparer.Ordinal);
        Load(obj);
    }

    /// <summary>The enumerable string keys of a shape in OrdinaryOwnPropertyKeys order (array indices ascending, then creation order).</summary>
    private static string[] ComputeEnumKeys(Shape s)
    {
        var list = new List<string>();
        if (s.HasIndexKeys)
        {
            var idx = new List<(uint, string)>();
            for (int i = 0; i < s.Count; i++)
                if (s.Keys[i].Value is string str && s.Keys[i].TryGetArrayIndex(out uint n) && (s.Flags[i] & PropFlags.Enumerable) != 0) idx.Add((n, str));
            idx.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            foreach (var (_, str) in idx) list.Add(str);
        }
        for (int i = 0; i < s.Count; i++)
        {
            if (s.Keys[i].Value is not string str || (s.Flags[i] & PropFlags.Enumerable) == 0) continue;
            if (s.HasIndexKeys && s.Keys[i].TryGetArrayIndex(out _)) continue;
            list.Add(str);
        }
        var keys = list.Count == 0 ? Array.Empty<string>() : list.ToArray();
        if (!s.IsUnique) s.EnumKeys = keys;   // shapes are immutable; a racing write stores an equal array
        return keys;
    }

    /// <summary>True when no object from <paramref name="p"/> up has an enumerable string key, checked without running code.</summary>
    private static bool NoEnumerableKeys(JsObject? p)
    {
        for (; p != null; p = p.Proto)
        {
            if (p.GetType() != typeof(JsObject) && p is not JsImmutableProtoObject) return false;
            var d = p.Dict;
            if (d != null)
            {
                for (int i = 0; i < d.Count; i++)
                    if (d.Live[i] && (d.Flags[i] & PropFlags.Enumerable) != 0 && d.Keys[i].IsString) return false;
            }
            else if ((p.Shape.EnumKeys ?? ComputeEnumKeys(p.Shape)).Length != 0) return false;
        }
        return true;
    }

    private bool NextFast(out string key)
    {
        var root = _root!;
        var keys = _fast!;
        while (_pos < keys.Length)
        {
            var s = keys[_pos++];
            // the same shape means the same keys with the same attributes; otherwise check that s is still an enumerable own key
            if (!ReferenceEquals(root.Shape, _fastShape) && (!root.GetOwnProperty(s, out var desc) || !desc.Enumerable)) continue;
            key = s;
            return true;
        }
        _fast = null;
        var proto = root.GetPrototypeOf();
        if (NoEnumerableKeys(proto))
        {
            _current = null;
            key = "";
            return false;
        }
        // the general walk from the prototype: every string key the root had at the start shadows
        _visited = new(StringComparer.Ordinal);
        var shape = _fastShape!;
        for (int i = 0; i < shape.Count; i++) if (shape.Keys[i].Value is string str) _visited.Add(str);
        _keys = new();
        _current = proto;
        if (proto != null) Load(proto);
        return Next(out key);
    }

    private void Load(JsObject o)
    {
        _keys!.Clear();
        _pos = 0;
        o.OwnPropertyKeys(_keys);
    }

    public bool Next(out string key)
    {
        if (_fast != null) return NextFast(out key);
        var _keys = this._keys!;
        var _visited = this._visited!;
        while (_current != null)
        {
            while (_pos < _keys.Count)
            {
                var k = _keys[_pos++];
                if (k.Value is not string s) continue;
                if (!_visited.Add(s)) continue;
                if (!_current.GetOwnProperty(k, out var desc)) continue; // deleted during iteration
                if (!desc.Enumerable) continue;
                key = s;
                return true;
            }
            _current = _current.GetPrototypeOf();
            if (_current != null) Load(_current);
        }
        key = "";
        return false;
    }
}
