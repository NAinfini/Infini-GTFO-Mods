using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>Binding identity. The generated Primitive graph is the semantic authority.</summary>
public static class EnemySelectorContract
{
    public const string CapabilityId = "forge.selector.target.enemies";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.enemies";
    public const string HandlerName = "gtfo.enemy.enemies";
    public const string EntityKind = "gtfo.enemy";
    public static readonly HandlerShape Shape = new HandlerShape().Outputs("targets");
    public static readonly string CapabilityRowJson = RuntimeJson.From(new
    {
        id = CapabilityId, owner = ModuleDefinition.ProviderId, kind = "selector", version = "1.0.0",
        label = "查询全部敌人",
        parameters = new { description = "读取当前世界中身份有效的敌人集合；不把敌人种类等同于敌对关系。" },
        graph = PrimitiveGraphSource.Get(CapabilityId)
    }).GetRawText();
    public const string BindingRowJson = """
    {"id":"forge.module.gtfo.enemy.binding.target.enemies","capabilityId":"forge.selector.target.enemies",
     "providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.enemies","role":"observe",
     "status":"implemented","dependencies":[],"requires":[]}
    """;
}
