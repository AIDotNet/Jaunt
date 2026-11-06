using System;

namespace Jaunt;

/// <summary>Realm semantics profile.</summary>
public enum JsProfile
{
    /// <summary>Default: independent globals and mutable intrinsics, standard language behaviour.</summary>
    Standard,
    /// <summary>
    /// Frozen intrinsics and no code generation from strings (<c>eval</c>, <c>Function</c>, generator/async function
    /// constructors and all their aliases). This is a visible semantic difference, not a process sandbox.
    /// </summary>
    Hardened,
}

/// <summary>Execution limits. All limits are checked cooperatively at VM checkpoints.</summary>
public sealed class JsLimits
{
    /// <summary>Deadline of a complete root execution (null = none).</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>
    /// VM work budget per root execution (0 = unlimited). Work units are counted at loop back-edges, calls and
    /// per-element steps of long-running builtins.
    /// </summary>
    public long MaxInstructions { get; init; }
    /// <summary>Maximum JS call/resume depth (also bounded by available native stack).</summary>
    public int MaxStackDepth { get; init; } = 10_000;
    /// <summary>
    /// Conservative realm-lifetime accounting of engine allocations (objects, properties, array/string storage).
    /// This is not a CLR heap or RSS limit. 0 = unlimited.
    /// </summary>
    public long MaxTrackedBytes { get; init; }
    /// <summary>Maximum ready jobs in the microtask queue.</summary>
    public int MaxPendingJobs { get; init; } = 1_000_000;
    /// <summary>Maximum size of JSON/DTO output produced through the engine APIs.</summary>
    public long MaxOutputBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Maximum number of modules loaded into one engine.</summary>
    public int MaxModules { get; init; } = 1000;
    /// <summary>Maximum string length (UTF-16 code units).</summary>
    public int MaxStringLength { get; init; } = (1 << 29) - 24;
}

/// <summary>Engine configuration (frozen when the engine is created).</summary>
public sealed class JsEngineOptions
{
    /// <summary>Default options.</summary>
    public static JsEngineOptions Default { get; } = new();

    /// <summary>Semantics profile.</summary>
    public JsProfile Profile { get; init; } = JsProfile.Standard;
    /// <summary>Execution limits.</summary>
    public JsLimits Limits { get; init; } = new();
    /// <summary>Time zone used by <c>Date</c> local-time operations. Defaults to UTC for reproducibility.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    /// <summary>Clock used by <c>Date.now()</c> / <c>new Date()</c>.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }
    /// <summary>
    /// When true, messages of exceptions thrown by host functions are visible to scripts. Default false: scripts
    /// see a generic message; the original exception remains available to .NET as <see cref="Exception.InnerException"/>.
    /// </summary>
    public bool ExposeHostExceptionMessages { get; init; }
    /// <summary>Called at root checkpoints for promise rejections that were never handled.</summary>
    public Action<JsUnhandledRejection>? UnhandledRejection { get; init; }
    /// <summary>
    /// Default module resolver: used by <c>LoadModule</c> calls that pass none and by dynamic <c>import()</c> from
    /// scripts. Null (default) denies every import.
    /// </summary>
    public IJsModuleResolver? ModuleResolver { get; init; }
    /// <summary>Compile options used by source-string entry points (Evaluate(string)).</summary>
    public JsCompileOptions CompileOptions { get; init; } = JsCompileOptions.Default;
    /// <summary>
    /// The agent's [[CanBlock]]. Default false: <c>Atomics.wait</c> throws a TypeError, so scripts can never park the
    /// host thread. When true (a dedicated worker thread), <c>Atomics.wait</c> blocks the calling thread until notified,
    /// timed out or terminated by the execution's limits, and a drained job queue waits for pending
    /// <c>Atomics.waitAsync</c> promises that can still settle (a timeout, or memory shared with another engine).
    /// </summary>
    public bool CanBlock { get; init; }
}

/// <summary>Information about an unhandled promise rejection.</summary>
public readonly struct JsUnhandledRejection
{
    /// <summary>String description of the rejection reason.</summary>
    public string Reason { get; }
    internal JsUnhandledRejection(string reason) => Reason = reason;
}

/// <summary>Whether a root execution may wait for asynchronous host work.</summary>
public enum JsExecutionMode
{
    /// <summary>Synchronous: asynchronous host functions are rejected before they are invoked.</summary>
    Sync,
    /// <summary>Asynchronous: the scope's async entry points pump promises and host completions.</summary>
    Async,
}

/// <summary>Per-root limits. Values can only tighten the engine's limits.</summary>
public sealed class JsExecutionOptions
{
    /// <summary>Default.</summary>
    public static JsExecutionOptions Default { get; } = new();
    /// <summary>Deadline for the whole scope.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Work budget for the whole scope (0 = engine default).</summary>
    public long MaxInstructions { get; init; }
    /// <summary>Output budget for conversions inside the scope (0 = engine default).</summary>
    public long MaxOutputBytes { get; init; }
    /// <summary>Sync or async scope.</summary>
    public JsExecutionMode Mode { get; init; } = JsExecutionMode.Sync;
}

/// <summary>Snapshot of engine counters.</summary>
public readonly struct JsEngineStatistics
{
    /// <summary>Realm-lifetime tracked bytes (see <see cref="JsLimits.MaxTrackedBytes"/>).</summary>
    public long TrackedBytes { get; init; }
    /// <summary>Work units consumed by the last root execution.</summary>
    public long LastWorkUnits { get; init; }
    /// <summary>Number of completed root executions.</summary>
    public long Executions { get; init; }
    /// <summary>Number of unhandled rejections reported.</summary>
    public long UnhandledRejections { get; init; }
    /// <summary>Number of modules instantiated.</summary>
    public int Modules { get; init; }
}
