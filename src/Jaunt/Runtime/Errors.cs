using System;

namespace Jaunt.Runtime;

/// <summary>Internal carrier of a JS completion of type throw across native frames.</summary>
internal sealed class JsThrowException : Exception
{
    public readonly JsValue Value;
    public JsThrowException(JsValue value) : base("JavaScript exception") => Value = value;
    public override string Message => Value.IsObject && Value.Ref is JsError e ? e.Name + ": " + e.MessageText : "Uncaught " + Value.ToString();
}

internal enum TerminationKind
{
    Cancelled,
    Timeout,
    Instructions,
    StackDepth,
    TrackedBytes,
    OutputBytes,
    PendingJobs,
    Interrupted,
    AsyncRequired,
    UnjoinedAsyncWork,
    Internal,
}

/// <summary>Uncatchable (by JS) termination of the current root execution.</summary>
internal sealed class JsTerminationException : Exception
{
    public readonly TerminationKind Kind;
    public readonly long Observed;
    public readonly long Limit;
    public JsTerminationException(TerminationKind kind, string message, long observed = 0, long limit = 0) : base(message)
    {
        Kind = kind;
        Observed = observed;
        Limit = limit;
    }
}

internal static class JsErrors
{
    public static JsThrowException Create(Realm realm, ErrorKind kind, string message)
        => new(JsValue.FromObject(JsError.Create(realm, kind, message)));

    public static JsThrowException TypeError(Realm realm, string message) => Create(realm, ErrorKind.TypeError, message);
    public static JsThrowException RangeError(Realm realm, string message) => Create(realm, ErrorKind.RangeError, message);
    public static JsThrowException ReferenceError(Realm realm, string message) => Create(realm, ErrorKind.ReferenceError, message);
    public static JsThrowException SyntaxError(Realm realm, string message) => Create(realm, ErrorKind.SyntaxError, message);
    public static JsThrowException EvalError(Realm realm, string message) => Create(realm, ErrorKind.EvalError, message);
    public static JsThrowException UriError(Realm realm, string message) => Create(realm, ErrorKind.URIError, message);

    public static string Describe(JsValue v) => v.Tag switch
    {
        JsTag.String => "\"" + (string)v.Ref! + "\"",
        JsTag.Object => v.Ref is JsFunctionObject f ? "function " + f.DebugName : "object",
        _ => v.ToString(),
    };
}

internal enum ErrorKind
{
    Error,
    TypeError,
    RangeError,
    ReferenceError,
    SyntaxError,
    EvalError,
    URIError,
    AggregateError,
    SuppressedError,
}

/// <summary>Error instance (has [[ErrorData]]). Captures a JS stack trace at creation.</summary>
internal sealed class JsError : JsObject
{
    public string Name;
    public string MessageText;
    public string StackTrace = "";
    /// <summary>Original CLR exception when this error was produced from a failing host function.</summary>
    public Exception? HostException;

    public JsError(Realm realm, JsObject? proto, string name, string message) : base(realm, proto)
    {
        Name = name;
        MessageText = message;
    }

    public override string ClassName => "Error";

    public static JsError Create(Realm realm, ErrorKind kind, string message)
    {
        var proto = realm.ErrorPrototypes[(int)kind];
        var e = new JsError(realm, proto, kind.ToString(), message);
        e.DefineHidden("message", new JsValue(message));
        e.CaptureStack();
        return e;
    }

    /// <summary>Records the JS stack trace; exposed through the Error.prototype.stack accessor (no own property).</summary>
    public void CaptureStack() => StackTrace = Realm.Vm.CaptureStackTrace();

    /// <summary>The string returned by the Error.prototype.stack getter.</summary>
    public string StackString => Name + (MessageText.Length > 0 ? ": " + MessageText : "") + StackTrace;
}
