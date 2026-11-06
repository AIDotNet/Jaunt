using System;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>A JavaScript exception (ordinary <c>throw</c>) that reached the host.</summary>
/// <remarks>The engine stays usable afterwards; state changes made before the throw are not rolled back.</remarks>
public sealed class JsException : Exception
{
    /// <summary>JS error name (e.g. <c>TypeError</c>) or <c>"Error"</c> for non-Error values.</summary>
    public string Name { get; }
    /// <summary>JS error message (or the string form of the thrown value).</summary>
    public string ErrorMessage { get; }
    /// <summary>JS stack trace text (frames only).</summary>
    public string JsStackTrace { get; }
    /// <summary>
    /// The thrown value. Primitive values are always usable; object values stay valid only while the owning
    /// engine is alive.
    /// </summary>
    public JsValue ThrownValue { get; }

    internal JsException(string name, string message, string stack, JsValue thrown, Exception? inner)
        : base(name.Length > 0 ? $"{name}: {message}" : message, inner)
    {
        Name = name;
        ErrorMessage = message;
        JsStackTrace = stack;
        ThrownValue = thrown;
    }

    internal static JsException From(Realm realm, JsValue value)
    {
        if (value.IsObject && value.Ref is JsObject o)
        {
            string name = "Error", msg = "", stack = "";
            try
            {
                var n = o.Get("name");
                if (n.IsString) name = n.AsStringUnchecked;
                else if (o is not JsError)
                {
                    // thrown non-Error objects (e.g. test harness errors): use the constructor's name
                    var c = o.Get("constructor");
                    if (c.IsObject && c.AsObjectUnchecked.Get("name") is { IsString: true } cn && cn.AsStringUnchecked.Length > 0) name = cn.AsStringUnchecked;
                }
                var m = o.Get("message");
                if (m.IsString) msg = m.AsStringUnchecked;
            }
            catch (JsThrowException) { }
            if (o is JsError e)
            {
                stack = e.StackTrace;
                if (name == "Error" && e.Name.Length > 0) name = e.Name;
                return new JsException(name, msg, stack, value, e.HostException);
            }
            return new JsException(name, msg.Length > 0 ? msg : ObjectDescription(o), stack, value, null);
        }
        return new JsException("", "Uncaught " + value.ToString(), "", value, null);
    }

    private static string ObjectDescription(JsObject o) => "[object " + o.ClassName + "]";
}

/// <summary>Kinds of resource limits enforced by the engine.</summary>
public enum JsLimitKind
{
    /// <summary>Wall-clock deadline of the root execution.</summary>
    Timeout,
    /// <summary>VM work units (see <see cref="JsLimits.MaxInstructions"/>).</summary>
    Instructions,
    /// <summary>Call/resume depth.</summary>
    StackDepth,
    /// <summary>Conservative tracked allocation budget.</summary>
    TrackedBytes,
    /// <summary>Output (JSON/DTO) size.</summary>
    OutputBytes,
    /// <summary>Pending job queue capacity.</summary>
    PendingJobs,
    /// <summary>Number of modules in the graph.</summary>
    Modules,
}

/// <summary>Raised when an execution exceeds a configured limit. The engine is faulted afterwards.</summary>
public sealed class JsLimitExceededException : Exception
{
    /// <summary>Which limit was exceeded.</summary>
    public JsLimitKind Kind { get; }
    /// <summary>Observed value when known (0 otherwise).</summary>
    public long Observed { get; }
    /// <summary>Configured limit when known (0 otherwise).</summary>
    public long Limit { get; }

    internal JsLimitExceededException(JsLimitKind kind, string message, long observed, long limit) : base(message)
    {
        Kind = kind;
        Observed = observed;
        Limit = limit;
    }
}

/// <summary>
/// Raised when a synchronous execution reached an asynchronous host function. The host delegate was not invoked;
/// prior JS side effects are not rolled back. Use the Async entry points instead.
/// </summary>
public sealed class JsAsyncRequiredException : InvalidOperationException
{
    /// <summary>Diagnostic code.</summary>
    public string Code => "AsyncRequired";
    internal JsAsyncRequiredException(string message) : base(message) { }
}

/// <summary>
/// The engine reached a state it cannot safely continue from (cancellation, unjoined async work, internal
/// error). The engine is faulted and must be disposed.
/// </summary>
public sealed class JsEngineFaultedException : InvalidOperationException
{
    /// <summary>Diagnostic code (e.g. <c>UnjoinedAsyncWork</c>, <c>InternalError</c>, <c>Faulted</c>).</summary>
    public string Code { get; }
    internal JsEngineFaultedException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

/// <summary>DTO/JSON conversion failure, with the member path.</summary>
public sealed class JsConversionException : Exception
{
    /// <summary>Path of the failing member (e.g. <c>$.items[2].name</c>).</summary>
    public string Path { get; }
    internal JsConversionException(string path, string message) : base($"{message} (at {path})") => Path = path;
}
