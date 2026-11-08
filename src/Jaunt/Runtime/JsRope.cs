using System.Threading;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        // the real allocation: charged to the execution running on this thread (none when the host reads the string)
        Vm.OnThread?.Realm.ChargeBytes(Length * 2L + 24);
        flat = string.Create(Length, this, static (span, root) => Fill(span, root));
        Volatile.Write(ref _flat, flat);
        Volatile.Write(ref _left, null);
        Volatile.Write(ref _right, null);
        return flat;
    }

    /// <summary>
    /// Writes the characters right-to-left. An append chain (<c>s += x</c>: flat right children) needs no stack at all, so it
    /// walks straight down the left spine; a pending left child goes on an explicit stack only when the right child is itself
    /// a rope, so a chain of any depth cannot overflow.
    /// </summary>
    private static void Fill(Span<char> span, JsRope root)
    {
        int pos = span.Length;
        Stack<object>? stack = null;
        object node = root;
        while (true)
        {
            if (node is JsRope r)
            {
                var left = Volatile.Read(ref r._left);
                var right = Volatile.Read(ref r._right);
                var f = Volatile.Read(ref r._flat);
                if (f == null && left != null && right != null)
                {
                    if (right is string rs)
                    {
                        pos -= rs.Length;
                        rs.AsSpan().CopyTo(span.Slice(pos));
                        node = left;
                    }
                    else
                    {
                        (stack ??= new Stack<object>()).Push(left);   // popped once the right side is written: we fill from the end
                        node = right;
                    }
                    continue;
                }
                // flattened (possibly by another thread just now): children are released only after _flat is set
                node = f ?? Volatile.Read(ref r._flat)!;
            }
            var s = (string)node;
            pos -= s.Length;
            s.AsSpan().CopyTo(span.Slice(pos));
            if (stack == null || !stack.TryPop(out node!)) return;
        }
    }

    public override string ToString() => Flatten();
}
