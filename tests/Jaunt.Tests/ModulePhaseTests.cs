using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>Stage-3 module proposals: import defer, source phase imports and text modules.</summary>
public class ModulePhaseTests
{
    private static JsModule Root(string source) => JsCompiler.CompileModule(source, new JsCompileOptions { SourceName = "root.js" });

    [Fact]
    public void ImportDeferEvaluatesOnFirstExportAccess()
    {
        var resolver = new JsDictionaryModuleResolver()
            .Add("log.js", "export const log = [];")
            .Add("dep.js", "import { log } from './log.js'; log.push('dep'); export const x = 1; export function then() {}");
        using var e = new JsEngine();
        var m = e.LoadModule(Root("""
            import { log } from './log.js';
            import defer * as ns from './dep.js';
            import * as eager from './log.js';
            log.push('root');
            const tag = ns[Symbol.toStringTag];
            const thenBefore = ns.then;           // "then" is never an export of a deferred namespace
            const proto = Object.getPrototypeOf(ns);
            const before = log.join();
            const x = ns.x;                       // triggers evaluation
            export const result = [tag, String(thenBefore), String(proto), before, x, log.join(), Object.keys(ns).join(), eager[Symbol.toStringTag]].join('|');
            """), resolver, "root.js");
        Assert.Equal("Deferred Module|undefined|null|root|1|root,dep|x|Module", m.GetValue("result").AsString());
    }

    [Fact]
    public void ImportDeferredSelfWhileEvaluatingThrowsTypeError()
    {
        var resolver = new JsDictionaryModuleResolver()
            .Add("a.js", "import defer * as self from './a.js'; let err; try { self.x; } catch (e) { err = e.constructor.name; } export const x = err;");
        using var e = new JsEngine();
        var m = e.LoadModule(Root("export { x } from './a.js';"), resolver, "root.js");
        Assert.Equal("TypeError", m.GetValue("x").AsString());
    }

    [Fact]
    public void DeferredModuleErrorIsRethrownOnEveryAccess()
    {
        var resolver = new JsDictionaryModuleResolver().Add("bad.js", "throw new RangeError('boom'); export const x = 1;");
        using var e = new JsEngine();
        var m = e.LoadModule(Root("""
            import defer * as ns from './bad.js';
            const out = [];
            for (let i = 0; i < 2; i++) { try { ns.x; } catch (err) { out.push(err.message); } }
            export const r = out.join();
            """), resolver, "root.js");
        Assert.Equal("boom,boom", m.GetValue("r").AsString());
    }

    [Fact]
    public async Task AsyncDependenciesOfDeferredModulesAreEvaluatedEagerly()
    {
        var resolver = new JsDictionaryModuleResolver()
            .Add("log.js", "export const log = [];")
            .Add("tla.js", "import { log } from './log.js'; await 0; log.push('tla');")
            .Add("deferred.js", "import { log } from './log.js'; import './tla.js'; log.push('deferred'); export const v = 7;");
        using var e = new JsEngine();
        var m = await e.LoadModuleAsync(Root("""
            import { log } from './log.js';
            import defer * as ns from './deferred.js';
            log.push('root');
            const before = log.join();
            export const r = before + '|' + ns.v + '|' + log.join();
            """), resolver, "root.js");
        Assert.Equal("tla,root|7|tla,root,deferred", m.GetValue("r").AsString());
    }

    [Fact]
    public async Task DynamicImportDeferReturnsUnevaluatedNamespace()
    {
        var resolver = new JsDictionaryModuleResolver()
            .Add("log.js", "export const log = [];")
            .Add("dep.js", "import { log } from './log.js'; log.push('dep'); export const v = 3;");
        using var e = new JsEngine();
        var m = await e.LoadModuleAsync(Root("""
            import { log } from './log.js';
            const ns = await import.defer('./dep.js');
            const before = log.length;
            export const r = [ns[Symbol.toStringTag], before, ns.v, log.join(), ns === await import.defer('./dep.js')].join();
            """), resolver, "root.js");
        Assert.Equal("Deferred Module,0,3,dep,true", m.GetValue("r").AsString());
    }

    [Fact]
    public async Task TextModulesExposeTheirContentAsDefault()
    {
        var resolver = new JsDictionaryModuleResolver().AddText("notes.txt", "hello\nworld");
        using var e = new JsEngine();
        var m = await e.LoadModuleAsync(Root("""
            import text from './notes.txt' with { type: 'text' };
            const dyn = await import('./notes.txt', { with: { type: 'text' } });
            export const r = [text.length, dyn.default === text, Object.keys(dyn).join()].join();
            """), resolver, "root.js");
        Assert.Equal("11,true,default", m.GetValue("r").AsString());
        Assert.True(JsCompiler.CreateTextModule("x").IsTextModule);

        // a text module needs the attribute (and vice versa)
        using var e2 = new JsEngine();
        Assert.ThrowsAny<Exception>(() => e2.LoadModule(Root("import t from './notes.txt'; export default t;"), resolver, "root.js"));
    }

    [Fact]
    public async Task SourcePhaseImportOfHostModuleSource()
    {
        var wasm = JsCompiler.CreateSourceModule("WebAssembly.Module");
        Assert.Equal("WebAssembly.Module", wasm.ModuleSourceKind);
        var resolver = new JsDictionaryModuleResolver()
            .Add("mod.wasm", wasm)
            .Add("reexport.js", "import source s from './mod.wasm'; export { s };");
        using var e = new JsEngine();
        e.SetFunction("abstractModuleSource", static (ref JsCallContext c) => c.GetAbstractModuleSource(), 0);
        var m = await e.LoadModuleAsync(Root("""
            import source s from './mod.wasm';
            import { s as s2 } from './reexport.js';
            const AMS = abstractModuleSource();
            const d = await import.source('./mod.wasm');
            export const r = [
                s instanceof AMS, Object.getPrototypeOf(Object.getPrototypeOf(s)) === AMS.prototype,
                Object.prototype.toString.call(s), s === s2, s === d,
            ].join();
            """), resolver, "root.js");
        Assert.Equal("true,true,[object WebAssembly.Module],true,true", m.GetValue("r").AsString());
    }

    [Fact]
    public async Task SourcePhaseImportOfSourceTextModuleIsSyntaxError()
    {
        var resolver = new JsDictionaryModuleResolver()
            .Add("plain.js", "export const x = 1;")
            .Add("importer.js", "import source s from './plain.js';");
        using var e = new JsEngine();
        var ex = Assert.Throws<JsException>(() => e.LoadModule(Root("import './importer.js';"), resolver, "root.js"));
        Assert.Equal("SyntaxError", ex.Name);

        using var e2 = new JsEngine();
        var m = await e2.LoadModuleAsync(Root("""
            let name;
            try { await import.source('./plain.js'); } catch (err) { name = err.name; }
            export const r = name;
            """), resolver, "root2.js");
        Assert.Equal("SyntaxError", m.GetValue("r").AsString());
    }

    [Fact]
    public void AbstractModuleSourceIntrinsic()
    {
        using var e = new JsEngine();
        e.SetFunction("abstractModuleSource", static (ref JsCallContext c) => c.GetAbstractModuleSource(), 0);
        var r = e.Evaluate("""
            const AMS = abstractModuleSource();
            const get = Object.getOwnPropertyDescriptor(AMS.prototype, Symbol.toStringTag).get;
            let threw = false; try { new AMS(); } catch (err) { threw = err instanceof TypeError; }
            [AMS.name, AMS.length, threw, get.call(AMS.prototype), AMS === abstractModuleSource()].map(String).join()
            """).ToString();
        Assert.Equal("AbstractModuleSource,0,true,undefined,true", r);
    }

    [Fact]
    public void RequestedModulesListsEachSpecifierOnce()
    {
        var m = JsCompiler.CompileModule("import defer * as a from './a.js'; import './a.js'; import source s from './b.js';");
        Assert.Equal(new[] { "./a.js", "./b.js" }, m.RequestedModules);
    }
}
