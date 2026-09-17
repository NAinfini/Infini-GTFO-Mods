using System;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>Binding identity and metadata; all semantic ports come from Primitive SSOT.</summary>
public static class PlayerSelectorContract
{
    public const string CapabilityId = "forge.selector.target.players";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.players";
    public const string HandlerName = "gtfo.map.players";
    public static readonly HandlerShape Shape = new HandlerShape().Outputs("targets");
    public static readonly string CapabilityRowJson = RuntimeJson.From(new
    {
        id = CapabilityId, owner = ModuleDefinition.ProviderId, kind = "selector", version = "1.0.0",
        label = "查询全部玩家",
        parameters = new { description = "读取当前世界中身份有效的玩家集合；关系与范围由后续选择器处理。" },
        graph = PrimitiveGraphSource.Get(CapabilityId)
    }).GetRawText();
}
