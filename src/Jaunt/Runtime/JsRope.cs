using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>
/// A lazily concatenated JavaScript string (a "cons string"). <c>a + b</c> on long strings allocates one small node that
/// points at both halves instead of copying them, so building a string with <c>s += x</c> in a loop costs O(n) in total
/// instead of O(n²) (and no longer churns the large-object heap). The characters are copied exactly once, the first time
/// anything needs the flat <see cref="string"/> (<see cref="Flatten"/>); the result is cached and the children released.
/// </summary>
/// <remarks>
/// A <see cref="JsValue"/> with the String tag holds either a <see cref="string"/> or a <see cref="JsRope"/> in <c>Ref</c>.
/// Everything that needs characters goes through <c>JsValue.AsStringUnchecked</c>/<c>AsString</c>, which flatten; code that
/// only needs the length uses <c>JsValue.StringLength</c>. Realms are single-threaded, so no locking is needed.
/// </remarks>
internal sealed class JsRope
{
    /// <summary>Concatenations shorter than this stay flat: copying a few hundred bytes is cheaper than a node.</summary>
    internal const int MinLength = 256;

    private object? _left;   // string or JsRope
    private object? _right;  // string or JsRope
    private string? _flat;
    private Realm? _realm;   // charged for the characters when they are materialized
    public readonly int Length;

    public JsRope(Realm realm, object left, object right, int length)
    {
        _realm = realm;
        _left = left;
        _right = right;
        Length = length;
    }

    public static int LengthOf(object part) => part is string s ? s.Length : ((JsRope)part).Length;

    public string Flatten()
    {
        var flat = _flat;
        if (flat != null) return flat;
        _realm!.ChargeBytes((long)Length * 2 + 24);
        flat = string.Create(Length, this, static (span, root) => Fill(span, root));
        _flat = flat;
        _left = null;
        _right = null;
        _realm = null;
        return flat;
    }

    /// <summary>Writes the characters right-to-left with an explicit stack, so a chain of any depth cannot overflow.</summary>
    private static void Fill(Span<char> span, JsRope root)
    {
        int pos = span.Length;
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is string s)
            {
                pos -= s.Length;
                s.AsSpan().CopyTo(span.Slice(pos));
            }
            else
            {
                var r = (JsRope)node;
                if (r._flat is { } f)
                {
                    pos -= f.Length;
                    f.AsSpan().CopyTo(span.Slice(pos));
                }
                else
                {
                    stack.Push(r._left!);   // popped second
                    stack.Push(r._right!);  // popped first: we fill from the end
                }
            }
        }
    }

    public override string ToString() => Flatten();
}
