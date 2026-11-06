using System.Collections.Generic;

namespace Jaunt.Runtime;

internal sealed partial class Realm
{
    /// <summary>%AbstractModuleSource% (source phase imports), created on first use.</summary>
    public JsBuiltin? AbstractModuleSource;
    /// <summary>Per-kind prototypes of host Module Source objects (each inherits from %AbstractModuleSource%.prototype).</summary>
    public Dictionary<string, JsObject>? ModuleSourcePrototypes;
}
