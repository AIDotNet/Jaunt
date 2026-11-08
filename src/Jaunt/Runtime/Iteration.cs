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
        }
        else if (iterable.IsString && realm.IsStringIterationPristine())
        {
            rec._string = iterable.AsStringUnchecked;
            return rec;
        }
        var method = Operations.GetV(realm, iterable, JsSymbol.Iterator);
        if (!method.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(iterable)} is not iterable");
        var it = realm.Vm.Call(method, iterable, ReadOnlySpan<JsValue>.Empty);
        if (it.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.iterator method is not an object");
        rec.Iterator = it.AsObjectUnchecked;
        rec.NextMethod = rec.Iterator.Get("next", it);
        return rec;
    }

    public static IteratorRecord FromIterator(Realm realm, JsObject iterator)
    {
        var rec = new IteratorRecord(realm) { Iterator = iterator };
        rec.NextMethod = iterator.Get("next");
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
                if (!a.TryGetDense(_index, out value)) value = a.GetIndex(_index, JsValue.FromObject(a));
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
        var r = _realm.Vm.Call(NextMethod, JsValue.FromObject(Iterator!), ReadOnlySpan<JsValue>.Empty);
        if (r.IsNotObject) throw JsErrors.TypeError(_realm, $"Iterator result {r} is not an object");
        var ro = r.AsObjectUnchecked;
        if (Operations.ToBoolean(ro.Get("done", r)))
        {
            value = default;
            return false;
        }
        value = ro.Get("value", r);
        Done = false;
        return true;
    }

    /// <summary>IteratorClose with a normal completion.</summary>
    public void Close()
    {
        if (Done) return;
        Done = true;
        if (Iterator == null) return;
        var ret = Iterator.Get("return");
        if (ret.IsNullOrUndefined) return;
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

/// <summary>for-in key enumerator (enumerable string keys along the prototype chain, shadowing aware).</summary>
internal sealed class ForInIterator
{
    private readonly JsObject? _root;
    private JsObject? _current;
    private readonly List<PropertyKey> _keys = new();
    private int _pos;
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);

    public ForInIterator(JsObject? obj)
    {
        _root = obj;
        _current = obj;
        if (obj != null) Load(obj);
    }

    private void Load(JsObject o)
    {
        _keys.Clear();
        _pos = 0;
        o.OwnPropertyKeys(_keys);
    }

    public bool Next(out string key)
    {
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
