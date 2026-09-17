using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>The suite's harness: the production decision rules driven by the suite's own bridge, and a way to
/// dispatch one behaviour command through a command context built the way the kernel builds one. The rules are
/// the same sources the native module calls; only the bridge differs, so a rule that passes here is a rule the
/// game path runs.
///
/// The context is constructed by reflection only because its constructor is internal to the Framework assembly;
/// every value handed to it is built with the SDK's own public helpers, so the handler sees the shapes a real
/// dispatch hands it.</summary>
internal static class Scene
{
    private static readonly ConstructorInfo ContextConstructor = typeof(CommandContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 10);

    internal const long World = 1;

    internal static EntityReference Reference(string key = "gtfo.enemy:7")
        => new(key, World, 1);

    internal static CommandResult DispatchAbility(CommandContext context, FakePorts ports, EnemyBehaviorLedger ledger,
        byte ability = 0)
        => AbilityDecision.Run(context, ports, ledger, context.WorldEpoch);

    internal static CommandContext AbilityContext(EntityReference[] targets, ResourceRef? ability, int targetPolicy = 0,
        int cooldownScope = 0, long worldEpoch = World)
        => Context(worldEpoch, RuntimeJson.From(new
        {
            enemies = targets,
            ability = ability?.ToJson()
        }), RuntimeJson.From(new { target_policy = targetPolicy, cooldown_scope = cooldownScope }));

    internal static CommandContext NoiseContext(EntityReference source, double radius, double x = 1, double y = 2,
        double z = 3, long worldEpoch = World)
        => Context(worldEpoch, RuntimeJson.From(new
        {
            source,
            position = new[] { x, y, z },
            radius
        }), RuntimeJson.From(new { }));

    internal static ResourceRef Ability(string id) => new(EnemyAbilityResources.Kind, id);

    private static CommandContext Context(long worldEpoch, JsonElement inputs, JsonElement parameters)
    {
        var origin = new RuntimeEvent("test.behavior:" + worldEpoch, "forge.module.gtfo.enemy.binding.ability",
            worldEpoch, 0, "gtfo.world:" + worldEpoch, RuntimeJson.EmptyObject);
        return (CommandContext)ContextConstructor.Invoke(new object?[]
        {
            origin, 0L, "test.command", "test.plan", "test.resource", "1", "Step", parameters, inputs, true
        });
    }

    /// <summary>One kernel whose only registered rows are this slice's, so the kernel's own contract resolver can
    /// be asked what layout the declared rows have. The handlers here are stand-ins with the contract's own
    /// shapes: what the kernel checks at registration is that every declared row resolves against its handler's
    /// shape, and the real handlers' rules are covered by the cases that run the decisions.</summary>
    internal static RuntimeKernel RowKernel()
    {
        var kernel = new RuntimeKernel(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        kernel.BeginWorld(1);
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [EnemyBehaviorContract.AbilityHandler] = _ => CommandResult.Rejected("not-dispatched"),
            [EnemyBehaviorContract.NoiseEmitHandler] = _ => CommandResult.Rejected("not-dispatched")
        };
        kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, """
        {
          "providers": [
            {
              "id": "forge.module.gtfo.enemy",
              "kind": "native",
              "version": "1.0.0",
              "dependencies": []
            }
          ],
          "capabilities": [
        """ + "\n" + EnemyBehaviorContract.CapabilityRows + "\n  ],\n  \"bindings\": [\n"
            + EnemyBehaviorContract.BindingRows + "\n  ]\n}",
            handlers, EnemyBehaviorContract.Support())
        { Shapes = EnemyBehaviorContract.Shapes() }, RuntimeLogLevel.Off);
        // The registration is only resolved when the runtime starts: every declared row is matched against its
        // handler's shape there, which is the check this kernel exists for.
        kernel.StartRuntime(static () => { });
        return kernel;
    }
}

internal static class T
{
    internal sealed record CheckRow(string Id, bool Passed, string Detail);
    internal static readonly List<CheckRow> Rows = new();
    internal static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    internal static void Equal<TValue>(TValue expected, TValue actual, string message)
    {
        if (!EqualityComparer<TValue>.Default.Equals(expected, actual))
            throw new Exception(message + " expected=" + expected + " actual=" + actual);
    }
    internal static void Case(string id, Action test)
    {
        try { test(); Rows.Add(new(id, true, "passed")); }
        catch (Exception e) { Rows.Add(new(id, false, e.ToString())); Console.Error.WriteLine("FAIL " + id + ": " + e.Message); }
    }
    internal static JsonElement Row(CommandResult result, int index)
        => result.Outputs.GetProperty("results").EnumerateArray().ElementAt(index);
    internal static string Code(CommandResult result, int index) => Row(result, index).GetProperty("code").GetString()!;
    internal static string Status(CommandResult result, int index) => Row(result, index).GetProperty("status").GetString()!;
    internal static string Committed(CommandResult result, int index) => Row(result, index).GetProperty("committed").GetString()!;
}
