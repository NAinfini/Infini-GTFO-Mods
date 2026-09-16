using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The `forge.selector.target.enemies` declaration: the capability's identity, the binding that
/// implements it, the one entity kind its answer comes from, and the port shape its handler is resolved
/// against. It sits in the game-independent Enemy assembly because the runtime module, the manifest and the
/// website read it there; the world-bound evaluator is the native assembly's `EnemySelector`, and the
/// candidate set it reads is the native module's own `gtfo.enemy` source, which answers through this same
/// shape.</summary>
public static class EnemySelectorContract
{
    public const string CapabilityId = "forge.selector.target.enemies";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.enemies";
    public const string HandlerName = "gtfo.enemy.enemies";
    /// <summary>The one entity kind this selector answers for. It is the kind the provider already resolves,
    /// because the kernel only lets a kind's own owner say which entities of it exist.</summary>
    public const string EntityKind = "gtfo.enemy";
    /// <summary>The one shape of this handler: the entity collection it answers with, and the two structural
    /// enums it reads by declaration index. It is declared here rather than by each registration site, so a
    /// caller that only adds the native evaluator cannot quietly describe a different port layout.</summary>
    public static readonly HandlerShape Shape = new HandlerShape().Outputs("targets").Parameters("relation", "empty");
    /// <summary>The capability row a registration declares, spelled exactly as the authoring catalog row
    /// `forge.selector.target.enemies`: `inputs` is empty, the one output is the entity collection, and the two
    /// parameters are the catalog's own structural enums. The catalog's `descriptionZh` promises selection by
    /// type and state, but the row declares no parameter that could carry either, so nothing here filters the
    /// provider's candidate set — the same shape as the already-implemented `forge.selector.target.players`
    /// row, whose description promises the same unmatchable state selection.</summary>
    public const string CapabilityRowJson = """
    {
      "id": "forge.selector.target.enemies",
      "owner": "forge.module.gtfo.enemy",
      "kind": "selector",
      "label": "按敌人原型与状态选择敌人",
      "version": "1.0.0",
      "parameters": { "description": "按种类和状态挑敌人。" },
      "graph": {
        "domains": ["map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic"],
        "execution": "query",
        "inputs": [],
        "outputs": [
          { "id": "targets", "type": "entity", "cardinality": "many" }
        ],
        "parameters": [
          { "id": "relation", "type": "enum", "role": "structural", "required": true, "set": "recipient_relation" },
          { "id": "empty", "type": "enum", "role": "structural", "required": true, "set": "empty_policy" }
        ]
      }
    }
    """;
    /// <summary>The binding row the same registration declares. `observe` is the role every on-demand selector
    /// binding in this repository uses, and the kernel registers its evaluator through that role for a
    /// `selector` capability; the handler name is the one <see cref="HandlerName"/> spells.</summary>
    public const string BindingRowJson = """
    {
      "id": "forge.module.gtfo.enemy.binding.target.enemies",
      "capabilityId": "forge.selector.target.enemies",
      "providerId": "forge.module.gtfo.enemy",
      "handler": "gtfo.enemy.enemies",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;
}
