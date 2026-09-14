using System;
using System.Collections.Generic;

namespace ForgeRuntime.Framework;

/// <summary>D-017 R4-a control-flow primitives. `forge.control.flow.branch` (D-004) is native and versioned the
/// same way as <see cref="CombatContracts"/>. The kernel dispatches `control`-kind steps internally by reading their
/// `condition` input and routing to the `then`/`otherwise` successor (see RuntimeKernel.AdvanceCore) — it never
/// looks the binding up in <c>RuntimeRegistry.Handlers</c>, so no handler function is supplied here.
/// <see cref="RuntimeRegistry.WithModule"/> only requires a supplied handler for `action`-kind capabilities bound
/// with role `execute`; a `control`-kind capability bound the same way is exempt, but every implemented binding —
/// `control` included — still needs exactly one <see cref="BindingSupport"/> row.</summary>
public static class ControlContracts
{
    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RegistryJson,
        new Dictionary<string, CommandHandler>(), new[] {
            new BindingSupport("forge.contract.control.binding.branch", "implementation-only", Array.Empty<string>())
        });
    private const string RegistryJson = """
    {
      "providers": [
        {
          "id": "forge.contract.control",
          "kind": "native",
          "version": "1.0.0",
          "dependencies": []
        }
      ],
      "capabilities": [
        {
          "id": "forge.control.flow.branch",
          "owner": "forge.contract.control",
          "kind": "control",
          "label": "条件分支",
          "version": "1.0.0",
          "parameters": {
            "description": "条件成立走一边，不成立走另一边。"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "enemy",
              "weapon",
              "tool",
              "consumable",
              "player",
              "logic"
            ],
            "execution": "host",
            "inputs": [
              {
                "id": "in",
                "type": "execution"
              },
              {
                "id": "condition",
                "type": "boolean"
              }
            ],
            "outputs": [
              {
                "id": "then",
                "type": "execution"
              },
              {
                "id": "otherwise",
                "type": "execution"
              }
            ],
            "parameters": []
          }
        }
      ],
      "bindings": [
        {
          "id": "forge.contract.control.binding.branch",
          "capabilityId": "forge.control.flow.branch",
          "providerId": "forge.contract.control",
          "handler": "runtime.control.branch",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
      ]
    }
    """;
}
