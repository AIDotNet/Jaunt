extern alias gen;
using System.Collections.Immutable;
using Jaunt;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Jaunt.Tests;

public sealed record GenLine(string Name, int Qty, double Price);
public sealed record GenOrder(string Id, List<GenLine> Lines, int? Priority, Dictionary<string, string> Tags, DateTimeOffset Created);
public sealed class GenTreeNode
{
    public string Label { get; set; } = "";
    [JsPropertyName("kids")] public List<GenTreeNode> Children { get; set; } = new();
    [JsIgnore] public object? HostOnly { get; set; }
    public long? Weight { get; set; }
}

[JsSerializable(typeof(GenLine))]
[JsSerializable(typeof(GenOrder))]
[JsSerializable(typeof(GenTreeNode))]
public partial class TestBindings : JsBindingContext { }

public class GeneratorTests
{
    [Fact]
    public void GeneratedBindingsRoundTripThroughJs()
    {
        using var engine = new JsEngine();
        using var ex = engine.BeginExecution();
        var order = new GenOrder("A-7", new List<GenLine> { new("pen", 2, 1.5), new("ink", 1, 4) }, null,
            new Dictionary<string, string> { ["channel"] = "web" }, new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        var js = ex.FromObject(order, TestBindings.Default.GenOrder);
        var fn = JsFunction.FromValue(engine, ex.Evaluate("""
            (o) => ({ ...o, id: o.id + '!', lines: o.lines.map(l => ({ ...l, qty: l.qty * 10 })), priority: 3,
                      tags: { ...o.tags, seen: 'yes' }, created: o.created })
            """));
        var back = ex.ToObject(ex.Invoke(fn, js), TestBindings.Default.GenOrder);
        Assert.Equal("A-7!", back.Id);
        Assert.Equal(new[] { 20, 10 }, back.Lines.Select(l => l.Qty));
        Assert.Equal(3, back.Priority);
        Assert.Equal("yes", back.Tags["seen"]);
        Assert.Equal(order.Created, back.Created);
        Assert.Equal("""{"id":"A-7","lines":[{"name":"pen","qty":2,"price":1.5},{"name":"ink","qty":1,"price":4}],"priority":null,"tags":{"channel":"web"},"created":"2026-11-06T08:00:00.0000000+00:00"}""",
            ex.ToJsonString(js));
        ex.Complete();
    }

    [Fact]
    public void GeneratedBindingsSupportRecursiveTypesNamesAndIgnore()
    {
        using var engine = new JsEngine();
        using var ex = engine.BeginExecution();
        var tree = new GenTreeNode { Label = "root", Weight = 5, HostOnly = new object(), Children = { new GenTreeNode { Label = "leaf" } } };
        var js = ex.FromObject(tree, TestBindings.Default.GenTreeNode);
        Assert.Equal("""{"label":"root","kids":[{"label":"leaf","kids":[],"weight":null}],"weight":5}""", ex.ToJsonString(js));
        var back = ex.ToObject(ex.ParseJson("""{"label":"x","kids":[{"label":"y","kids":[]}]}"""), TestBindings.Default.GenTreeNode);
        Assert.Equal("y", back.Children[0].Label);
        Assert.Null(back.Weight);
        ex.Complete();
    }

    private static ImmutableArray<Diagnostic> RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create("t",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(JsBindingContext).Assembly.Location),
                MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new gen::Jaunt.Generators.JsBindingGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diags);
        return diags;
    }

    [Fact]
    public void UnsupportedMemberIsABuildError()
    {
        var diags = RunGenerator("""
            using Jaunt;
            public class Dto { public decimal Amount { get; set; } public System.IO.Stream S { get; set; } }
            [JsSerializable(typeof(Dto))] public partial class B : JsBindingContext { }
            """);
        Assert.Contains(diags, d => d.Id == "JAUNT003" && d.GetMessage().Contains("Amount"));
        Assert.All(diags.Where(d => d.Id.StartsWith("JAUNT")), d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void ContextMustBePartialAndDeriveFromBindingContext()
    {
        Assert.Contains(RunGenerator("""
            using Jaunt;
            public class Dto { public int X { get; set; } }
            [JsSerializable(typeof(Dto))] public class B : JsBindingContext { }
            """), d => d.Id == "JAUNT001");
        Assert.Contains(RunGenerator("""
            using Jaunt;
            public class Dto { public int X { get; set; } }
            [JsSerializable(typeof(Dto))] public partial class B { }
            """), d => d.Id == "JAUNT002");
    }

    [Fact]
    public void NameCollisionIsReported()
    {
        Assert.Contains(RunGenerator("""
            using Jaunt;
            public class Dto { public int Value { get; set; } [JsPropertyName("value")] public int Other { get; set; } }
            [JsSerializable(typeof(Dto))] public partial class B : JsBindingContext { }
            """), d => d.Id == "JAUNT004");
    }
}
