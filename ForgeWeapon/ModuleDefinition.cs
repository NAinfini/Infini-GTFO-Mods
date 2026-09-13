using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>Weapon provider. Wield facts come only from Weapon's own equipment observation; the actor is a
/// player reference owned by the player domain and is never created here. Both bindings are observe-only.</summary>
public static class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.weapon";
    public const string Version = "0.1.0";
    public const string EquippedCapability = "forge.trigger.input.equipped";
    public const string UnequippedCapability = "forge.trigger.input.unequipped";
    public const string EquippedBinding = ProviderId + ".binding.equipped";
    public const string UnequippedBinding = ProviderId + ".binding.unequipped";
    public const string WieldReadPermission = "gtfo.equipment.wield.read";

    public static RuntimeModule Create() => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = ProviderId, kind = "native", version = Version, dependencies = Array.Empty<string>() } },
            capabilities = new[]
            {
                Capability(EquippedCapability, "装备切入", "玩家切到了某件装备。"),
                Capability(UnequippedCapability, "装备切出", "玩家把某件装备收起来了。")
            },
            bindings = new[]
            {
                Binding(EquippedBinding, EquippedCapability, "gtfo.equipment.equipped"),
                Binding(UnequippedBinding, UnequippedCapability, "gtfo.equipment.unequipped")
            }
        }).GetRawText(),
        new Dictionary<string, CommandHandler>(),
        new[]
        {
            new BindingSupport(EquippedBinding, "implementation-only", new[] { WieldReadPermission }),
            new BindingSupport(UnequippedBinding, "implementation-only", new[] { WieldReadPermission })
        });

    // Label, description and graph are the canonical catalog entry for the same ID, unchanged.
    private static object Capability(string id, string label, string description) => new
    {
        id, owner = ProviderId, kind = "trigger", label, version = "1.0.0",
        parameters = new { description },
        graph = new
        {
            domains = new[] { "weapon", "tool", "consumable", "player" },
            execution = "host",
            inputs = Array.Empty<object>(),
            outputs = new[]
            {
                new { id = "next", type = "execution" },
                new { id = "actor", type = "entity" },
                new { id = "equipment", type = "entity" }
            },
            parameters = Array.Empty<object>()
        }
    };

    private static object Binding(string id, string capabilityId, string handler) => new
    {
        id, capabilityId, providerId = ProviderId, handler, role = "observe", status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };
}
