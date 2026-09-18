using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>Session-wide actions owned by the Map package. Their typed graph is generated from the reviewed
/// Primitive SSOT; this file owns only provider identity, binding identity, permission and handler shape.</summary>
public static class SessionActionContract
{
    public const string CheckpointSaveCapability = "forge.action.session.checkpoint_save";
    public const string CheckpointSaveBinding = ModuleDefinition.ProviderId + ".binding.session.checkpoint_save";
    public const string CheckpointSaveHandler = "gtfo.map.session.checkpoint_save";
    public const string CheckpointPermission = "session.checkpoint";

    public static readonly HandlerShape CheckpointSaveShape =
        new HandlerShape().Inputs("participants", "anchor").Outputs("result");

    public static object[] CapabilityRows() => new object[]
    {
        new
        {
            id = CheckpointSaveCapability,
            owner = ModuleDefinition.ProviderId,
            kind = "action",
            label = "立即保存检查点",
            version = "1.0.0",
            parameters = new { description = "通过当前世界的检查点交互通道保存一个检查点。" },
            graph = PrimitiveGraphSource.Get(CheckpointSaveCapability)
        }
    };

    public static object[] BindingRows() => new object[]
    {
        new
        {
            id = CheckpointSaveBinding,
            capabilityId = CheckpointSaveCapability,
            providerId = ModuleDefinition.ProviderId,
            handler = CheckpointSaveHandler,
            role = "execute",
            status = "implemented",
            dependencies = Array.Empty<string>(),
            requires = Array.Empty<string>()
        }
    };

    public static BindingSupport[] Supports() => new[]
    {
        new BindingSupport(CheckpointSaveBinding, "implementation-only", new[] { CheckpointPermission })
    };

    public static IReadOnlyDictionary<string, HandlerShape> Shapes() =>
        new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [CheckpointSaveHandler] = CheckpointSaveShape
        };
}
