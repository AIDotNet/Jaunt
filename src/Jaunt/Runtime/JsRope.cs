using System.Threading;
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
    public readonly int Length;

    public JsRope(object left, object right, int length)
    {
        _left = left;
        _right = right;
        Length = length;
    }

    public static int LengthOf(object part) => part is string s ? s.Length : ((JsRope)part).Length;

    /// <summary>
    /// The characters as one string. Safe to call from several threads at once (a string value may be shared across
    /// engines and read by the host): the flat string is published before the children are released, so a reader that
    /// finds the children gone always finds the flat string; concurrent callers at worst build equal copies.
    /// </summary>
    public string Flatten()
    {
        var flat = Volatile.Read(ref _flat);
        if (flat != null) return flat;
        flat = string.Create(Length, this, static (span, root) => Fill(span, root));
        Volatile.Write(ref _flat, flat);
        Volatile.Write(ref _left, null);
        Volatile.Write(ref _right, null);
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
                var left = Volatile.Read(ref r._left);
                var right = Volatile.Read(ref r._right);
                var f = Volatile.Read(ref r._flat);
                if (f != null || left == null || right == null)
                {
                    // flattened (possibly by another thread just now): children are released only after _flat is set
                    f ??= Volatile.Read(ref r._flat)!;
                    pos -= f.Length;
                    f.AsSpan().CopyTo(span.Slice(pos));
                }
                else
                {
                    stack.Push(left);    // popped second
                    stack.Push(right);   // popped first: we fill from the end
                }
            }
        }
    }

    public override string ToString() => Flatten();
}
