using System.Text;
using Jaunt;
using Jaunt.Compiler;
using Xunit;

namespace Jaunt.Tests;

/// <summary>
/// The shared-code invariant (design/01-research.md §3.2/§4.2, design/02): a compiled script/module is immutable and
/// realm-independent; inline caches, feedback, template objects and closure state live in each realm. Executing the
/// same compiled code hot, in many engines on many threads, must leave every template bit-for-bit unchanged.
/// </summary>
public class SharedCodeInvariantTests
{
    private const string Source = """
        'use strict';
        class Base { #secret = 1; static count = 0; constructor(x) { this.x = x; Base.count++; } get value() { return this.x + this.#secret; } }
        class Derived extends Base { constructor(x) { super(x * 2); } get value() { return super.value + 1; } }
        function* gen(n) { for (let i = 0; i < n; i++) yield i; }
        async function later(v) { await null; return v * 2; }
        const tag = (s, ...v) => s.raw.join('|') + v.join(',');
        function main(n) {
          let total = 0;
          const objs = [];
          for (let i = 0; i < n; i++) {
            const d = new Derived(i);
            const { x, value } = d;
            total += x + value + [...gen(3)].reduce((a, b) => a + b, 0);
            objs.push({ a: i, b: `${i}`, c: [i, i + 1], [`k${i % 3}`]: i });
            try { if (i % 7 === 0) throw new RangeError('r' + i); } catch (e) { total += e.message.length; } finally { total++; }
            total += /(\d+)-(?<n>\d+)/.exec(`${i}-${i * 2}`).groups.n.length;
            total += JSON.stringify(objs[i]).length + tag`a${i}b${i + 1}`.length;
            const m = new Map([[i, String(i)]]); total += m.get(i).length;
          }
          later(n).then(v => { globalThis.lastLater = v; });
          return total + eval('n + 1') + (Base.count > 0 ? 1 : 0);
        }
        """;

    [Fact]
    public void ExecutingSharedCodeNeverChangesItsTemplates()
    {
        var script = JsCompiler.CompileScript(Source);
        var before = Snapshot(script.Template);
        var errors = new List<Exception>();
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            try
            {
                for (int e = 0; e < 3; e++)
                {
                    using var engine = new JsEngine();
                    engine.Evaluate(script);
                    var main = engine.GetFunction("main");
                    double first = engine.Invoke(main, JsValue.From(50)).AsNumber();
                    for (int i = 0; i < 30; i++) Assert.Equal(first, engine.Invoke(main, JsValue.From(50)).AsNumber());
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Empty(errors);
        Assert.Equal(before, Snapshot(script.Template));

        // the snapshot does see a change to a nested function's bytecode
        var nested = script.Template.Body.Functions[0];
        nested.Code[0] ^= 1;
        try { Assert.NotEqual(before, Snapshot(script.Template)); }
        finally { nested.Code[0] ^= 1; }
        Assert.Equal(before, Snapshot(script.Template));
    }

    /// <summary>A structural dump of a script template and every nested function template (arrays by content).</summary>
    private static string Snapshot(ScriptTemplate script)
    {
        var sb = new StringBuilder();
        sb.Append(script.SourceName).Append('|').Append(script.Strict).Append('|').Append(script.IsModule).Append('\n');
        Dump(script.Body, sb, new HashSet<FunctionTemplate>(ReferenceEqualityComparer.Instance), 0);
        return sb.ToString();
    }

    private static void Dump(FunctionTemplate f, StringBuilder sb, HashSet<FunctionTemplate> seen, int depth)
    {
        if (!seen.Add(f)) { sb.Append(' ', depth).Append("(seen ").Append(f.Name).Append(")\n"); return; }
        sb.Append(' ', depth).Append(f.Name).Append(' ').Append(f.Length).Append(' ').Append(f.ParamCount).Append(' ')
          .Append(f.RegisterCount).Append(' ').Append((int)f.Flags).Append(' ').Append(f.IcCount).Append(' ')
          .Append(f.SourceStart).Append('-').Append(f.SourceEnd).Append(' ').Append(f.RestIndex).Append('\n');
        sb.Append(' ', depth).Append("code ").AppendJoin(',', f.Code).Append('\n');
        sb.Append(' ', depth).Append("consts ");
        foreach (var c in f.Constants) sb.Append(c.Bits).Append(':').Append(c.Ref is null ? "-" : c.Ref is string s ? s : c.Ref.GetType().Name + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.Ref)).Append(',');
        sb.Append('\n').Append(' ', depth).Append("objects ");
        foreach (var o in f.Objects) sb.Append(o is string s ? s : o.GetType().Name + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o)).Append(',');
        sb.Append('\n').Append(' ', depth).Append("handlers ");
        foreach (var h in f.Handlers) sb.Append(System.Text.Json.JsonSerializer.Serialize(h, new System.Text.Json.JsonSerializerOptions { IncludeFields = true })).Append(',');
        sb.Append('\n').Append(' ', depth).Append("positions ");
        foreach (var p in f.Positions) sb.Append(System.Text.Json.JsonSerializer.Serialize(p, new System.Text.Json.JsonSerializerOptions { IncludeFields = true })).Append(',');
        sb.Append('\n');
        foreach (var child in f.Functions) Dump(child, sb, seen, depth + 1);
    }
}
