using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeDevelopment;

/// <summary>Architecture scaffold only. An empty provider grants no runtime capabilities or bindings.</summary>
public static class ModuleDefinition
{
    public const string ProviderId = "forge.module.development";
    public const string Version = "0.1.0";

    public static RuntimeModule Create() => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = ProviderId, kind = "native", version = Version, dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(),
            bindings = Array.Empty<object>()
        }).GetRawText(),
        new Dictionary<string, CommandHandler>(),
        Array.Empty<BindingSupport>());
}
