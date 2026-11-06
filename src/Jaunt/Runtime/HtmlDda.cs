using System;

namespace Jaunt.Runtime;

/// <summary>
/// An object with the [[IsHTMLDDA]] internal slot (Annex B.3.6): <c>typeof</c> is "undefined", ToBoolean is false and
/// it is loosely equal to null and undefined. Calling it returns null. Only hosts create one (test262's
/// <c>$262.IsHTMLDDA</c>; browsers' <c>document.all</c>).
/// </summary>
internal sealed class JsHtmlDda : JsFunctionObject
{
    public JsHtmlDda(Realm realm) : base(realm, realm.FunctionPrototype) { }
    public override string DebugName => "IsHTMLDDA";
    public override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args) => JsValue.Null;
}
