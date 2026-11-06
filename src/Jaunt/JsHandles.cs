using System;
using System.Collections.Generic;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>
/// Handle to a specific function object of one engine. It does not follow later re-assignments of the variable
/// or export it was read from (use <see cref="JsExport"/> for live bindings).
/// </summary>
public sealed class JsFunction
{
    internal readonly JsEngine Engine;
    internal readonly JsFunctionObject Target;

    internal JsFunction(JsEngine engine, JsFunctionObject target)
    {
        Engine = engine;
        Target = target;
    }

    /// <summary>The function as a JS value (valid for the owning engine only).</summary>
    public JsValue Value => JsValue.FromObject(Target);

    internal void Check(JsEngine engine)
    {
        if (!ReferenceEquals(engine, Engine)) throw new ArgumentException("The function belongs to another engine.");
        engine.EnsureUsable();
    }

    /// <summary>Creates a handle from a function value owned by <paramref name="engine"/>.</summary>
    public static JsFunction FromValue(JsEngine engine, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.EnsureOwned(value, nameof(value));
        if (value.Ref is not JsFunctionObject f) throw new ArgumentException("Value is not a function.", nameof(value));
        return new JsFunction(engine, f);
    }
}

/// <summary>Handle to an export binding of a module instance; each use reads the current (live) value.</summary>
public sealed class JsExport
{
    internal readonly JsEngine Engine;
    private readonly ImportBinding _binding;
    /// <summary>Exported name.</summary>
    public string Name { get; }

    internal JsExport(JsEngine engine, ImportBinding binding, string name)
    {
        Engine = engine;
        _binding = binding;
        Name = name;
    }

    internal void Check(JsEngine engine)
    {
        if (!ReferenceEquals(engine, Engine)) throw new ArgumentException("The export belongs to another engine.");
        engine.EnsureUsable();
    }

    internal JsValue Read() => _binding.Read(Engine.Realm, Name);
}

/// <summary>A module instance that has been linked and evaluated inside a specific engine.</summary>
public sealed class JsModuleInstance
{
    internal readonly JsEngine Engine;
    internal readonly ModuleInstance Module;

    internal JsModuleInstance(JsEngine engine, ModuleInstance module)
    {
        Engine = engine;
        Module = module;
    }

    /// <summary>Canonical module id.</summary>
    public string Id => Module.Id;

    /// <summary>Names exported by the module.</summary>
    public IEnumerable<string> ExportNames { get { Engine.EnsureReadable(); return Modules.GetNamespace(Engine.Realm, Module).ExportNames; } }

    /// <summary>The module namespace object.</summary>
    public JsValue Namespace { get { Engine.EnsureReadable(); return JsValue.FromObject(Modules.GetNamespace(Engine.Realm, Module)); } }

    /// <summary>Returns a live binding handle for an export.</summary>
    public JsExport GetExport(string name)
    {
        Engine.EnsureReadable();
        var ns = Modules.GetNamespace(Engine.Realm, Module);
        var binding = ns.GetBinding(name) ?? throw new KeyNotFoundException($"Module '{Id}' has no export named '{name}'.");
        return new JsExport(Engine, binding, name);
    }

    /// <summary>Reads an export now and returns its current function object.</summary>
    public JsFunction GetFunction(string name)
    {
        var export = GetExport(name);
        JsValue v;
        try { v = export.Read(); }
        catch (JsThrowException ex) { throw JsException.From(Engine.Realm, ex.Value); }
        if (v.Ref is not JsFunctionObject f) throw new InvalidOperationException($"Export '{name}' of module '{Id}' is not a function.");
        return new JsFunction(Engine, f);
    }

    /// <summary>Reads the current value of an export (no JS code is executed).</summary>
    public JsValue GetValue(string name)
    {
        var export = GetExport(name);
        try { return export.Read(); }
        catch (JsThrowException ex) { throw JsException.From(Engine.Realm, ex.Value); }
    }
}
