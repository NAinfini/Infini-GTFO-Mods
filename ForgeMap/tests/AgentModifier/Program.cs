using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.AgentModifierFacts;

/// <summary>The focused suite for the three rows this adapter answers — the two player attribute-modifier rows and
/// the movement preset that writes the same native table. Every case drives the production handler through a real
/// `CommandContext`, asserts the result row's own columns, and then reads what reached the native double and what
/// the adapter's own ledger still holds. A case that claims a world-level cleanup also advances the kernel, because
/// a provider learns about a tick, a world change and a stop from the lifecycle and from nowhere else.</summary>
internal static class Program
{
    private const string Attribute = "movement-speed";
    private static int _checks;
    private static int _failures;

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.Error.WriteLine("FAIL: " + name);
    }

    private static int Main()
    {
        Registration();
        RuledShape();
        ValueTable();
        ApplySuccess();
        ApplyRefusals();
        MovementProfile();
        MovementProfileRefusals();
        HandleCancel();
        Remove();
        Lifetimes();
        Console.WriteLine($"checks={_checks} failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>The two registrations the real startup performs are accepted, the handler table reaches the
    /// adapter through the static entry points, and one adapter belongs to one registration.</summary>
    private static void Registration()
    {
        using var world = new ModifierWorld();
        Check(ReferenceEquals(AgentModifierAdapter.Current, world.Adapter), "the registered handler table reaches the one adapter");
        Check(world.Adapter.LiveModifiers == 0, "a fresh world holds no modification");
        var a = world.Spawn(1);
        var frame = new { targets = new[] { a.Reference }, source = a.Reference, attribute = Attribute, amount = 0.5, duration = 0 };
        var direct = world.Apply("set", frame);
        Check(direct.Status == CommandStatuses.Succeeded, "the row registers under its own capability and shape");
        Check(AgentModifierManager.Adds.Count == 1, "the write reached the native entry once");
    }

    /// <summary>The ruled shape, taken from the contract the registration declares: `attribute` is an
    /// `agent_modifier` member, `priority` is gone, `operation` keeps the three members the native entry can
    /// express, and the handle the apply row returns is the `effect`/`entity_life` one the remove row consumes.</summary>
    private static void RuledShape()
    {
        var apply = AgentModifierContract.ApplyCapability;
        var applyGraph = apply.GetProperty("graph");
        Check(Ports(applyGraph, "inputs").SequenceEqual(new[] { "in", "targets", "source", "attribute", "amount", "duration" }),
            "apply declares the ruled ports and no priority");
        var attribute = Port(applyGraph, "inputs", "attribute");
        Check(attribute.GetProperty("type").GetString() == "enum"
            && attribute.GetProperty("schema").GetString() == AgentModifierContract.AttributeSet,
            "attribute is an agent_modifier member, not a free string");
        var duration = Port(applyGraph, "inputs", "duration");
        Check(duration.GetProperty("type").GetString() == "integer" && duration.GetProperty("unit").GetString() == "tick",
            "duration stays a tick count");
        var operation = Port(applyGraph, "parameters", "operation");
        Check(operation.GetProperty("values").EnumerateArray().Select(v => v.GetString())
            .SequenceEqual(new[] { "set", "add", "subtract" }), "operation keeps set, add and subtract only");
        Check(Ports(applyGraph, "outputs").SequenceEqual(new[] { "next", "result", "modifier" })
            && Port(applyGraph, "outputs", "modifier").GetProperty("handleKind").GetString() == "effect"
            && Port(applyGraph, "outputs", "modifier").GetProperty("lifetime").GetString() == "entity_life",
            "apply returns the effect handle the remove row consumes");
        Check(apply.GetProperty("owner").GetString() == AgentModifierContract.OwnerProviderId,
            "the capability stays owned by the combat contract provider");

        var remove = AgentModifierContract.RemoveCapability;
        var removeGraph = remove.GetProperty("graph");
        Check(Ports(removeGraph, "inputs").SequenceEqual(new[] { "in", "modifiers", "attribute" })
            && Port(removeGraph, "inputs", "modifiers").GetProperty("cardinality").GetString() == "many"
            && Port(removeGraph, "inputs", "attribute").GetProperty("schema").GetString() == AgentModifierContract.AttributeSet,
            "remove takes the handle collection and the same attribute member");

        // The movement preset is this provider's own row, not the combat contract's: it declares its own binding,
        // its own support, its own shape, and the one refusal the native table forces on it.
        var preset = RuntimeJson.From(MovementProfileContract.Row());
        var presetGraph = preset.GetProperty("graph");

        // Every code the adapter can answer with is declared on a row of this slice, so a refusal is part of the
        // published contract instead of a string the handler invented after the shape was frozen.
        var applyCodes = Codes(apply);
        var removeCodes = Codes(remove);
        var declaredCodes = applyCodes.Concat(removeCodes).Concat(Codes(preset)).ToHashSet(StringComparer.Ordinal);
        foreach (var code in AgentModifierAdapter.RefusalCodes)
            Check(declaredCodes.Contains(code), "declared code: " + code);
        Check(applyCodes.Contains("attribute-no-op") && applyCodes.Contains("modifier-id-exhausted")
            && applyCodes.Contains("handle-budget") && applyCodes.Contains("modifier-budget"),
            "the apply row declares its own refusals");
        Check(removeCodes.Contains("modifier-handle-missing") && removeCodes.Contains("stale-handle")
            && removeCodes.Contains("modifier-attribute-mismatch"), "the remove row declares its own refusals");

        Check(AgentModifierContract.Binding(AgentModifierContract.ApplyCapabilityId)
                == ModuleDefinition.ProviderId + ".binding.attribute_apply"
            && AgentModifierContract.Support().All(row => row.RequiredPermissions.SequenceEqual(new[] { AgentModifierContract.Permission })),
            "each binding is this provider's own and carries the catalog's permission");
        Check(AgentModifierContract.Shapes().Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(
            new[] { AgentModifierContract.ApplyHandlerName, AgentModifierContract.RemoveHandlerName }.OrderBy(k => k, StringComparer.Ordinal)),
            "the handler shapes cover both rows");

        // The movement preset's own shape and binding.
        Check(preset.GetProperty("id").GetString() == MovementProfileContract.CapabilityId
            && preset.GetProperty("owner").GetString() == ModuleDefinition.ProviderId,
            "the preset is this provider's own capability");
        Check(Ports(presetGraph, "inputs").SequenceEqual(
                new[] { "in", "targets", "source", "speed", "acceleration", "jump_gravity", "duration" }),
            "the preset declares the catalog's ports in the catalog's order");
        Check(Port(presetGraph, "inputs", "jump_gravity").GetProperty("optional").GetBoolean(),
            "jump_gravity is declared and optional, because no native member carries it");
        Check(Port(presetGraph, "inputs", "source").GetProperty("entityKinds").EnumerateArray()
                .Select(kind => kind.GetString()).SequenceEqual(new[] { "gtfo.player" }),
            "the preset's source is the player namespace");
        Check(Ports(presetGraph, "outputs").SequenceEqual(new[] { "next", "result", "profile_handle" })
            && Port(presetGraph, "outputs", "profile_handle").GetProperty("handleKind").GetString() == "effect"
            && Port(presetGraph, "outputs", "profile_handle").GetProperty("lifetime").GetString() == "entity_life",
            "the preset returns the effect handle a remove or a cancel names");
        var presetRecipients = presetGraph.GetProperty("recipients");
        Check(presetRecipients.GetProperty("handle").GetString() == "profile_handle"
            && presetRecipients.GetProperty("requires").EnumerateArray().Select(p => p.GetString())
                .SequenceEqual(new[] { MovementProfileContract.Permission }),
            "the recipient contract names the preset's permission and its handle");
        Check(MovementProfileContract.BindingId == ModuleDefinition.ProviderId + ".binding.player.movement_profile"
            && MovementProfileContract.Support().RequiredPermissions.SequenceEqual(new[] { MovementProfileContract.Permission })
            && MovementProfileContract.Shapes().Keys.SequenceEqual(new[] { MovementProfileContract.HandlerName }),
            "the preset's binding, support and shape are this provider's own");
        Check(Codes(preset).Contains(AgentModifierAdapter.GravityCode),
            "the preset declares the refusal the native table forces on it");

        // The integration's own composition: the combat contract module declares both rows and this provider only
        // binds them. The fixture registers exactly that composition, so this case fails if either id stops being
        // declared by the provider that owns it.
        var canonical = RuntimeJson.Parse(CombatContracts.Module().RegistryJson);
        var declared = canonical.GetProperty("capabilities").EnumerateArray()
            .Select(capability => capability.GetProperty("id").GetString()).ToArray();
        Check(canonical.GetProperty("providers").EnumerateArray()
                .Single().GetProperty("id").GetString() == AgentModifierContract.OwnerProviderId,
            "the capability owner is the provider the canonical combat rows belong to");
        Check(declared.Contains(AgentModifierContract.ApplyCapabilityId)
            && declared.Contains(AgentModifierContract.RemoveCapabilityId),
            "both sourced-modifier rows are declared by the combat contract provider");
    }

    /// <summary>The explicit table: every native member with its own value, the shared set fully covered, and a
    /// name the table does not carry refused rather than mapped to a neighbouring member.</summary>
    private static void ValueTable()
    {
        var members = AgentModifierValues.Members;
        Check(members.Count == 54, "the table carries all 54 native members");
        Check(members.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() == members.Count, "no member name is duplicated");
        Check(Value("none") == (int)AgentModifier.None && Value("pistol-damage") == 50 && Value("glue-strength") == 100
            && Value("hacking-proficiency") == 150 && Value("melee-damage") == 200 && Value("movement-speed") == 250
            && Value("movement-acceleration") == 251, "the jumping native values are stated explicitly");
        Check(!AgentModifierValues.TryParse("experience-change", out _)
            && !AgentModifierValues.TryParse(null, out _), "a name outside the native enum is refused");

        // The shared set is the index basis a plan compiles against, so every member it can name has to resolve
        // here; the comparison reads the framework's own table rather than restating the names.
        var sets = (System.Collections.IDictionary)typeof(RuntimeKernel).Assembly
            .GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!
            .GetField("EnumSets", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var declared = (string[])sets[AgentModifierContract.AttributeSet]!;
        foreach (var name in declared) Check(AgentModifierValues.TryParse(name, out _), "shared member resolves: " + name);
    }

    /// <summary>One write per recipient, the signed contribution the row's operation names, and a result row whose
    /// columns are the canonical ones. The native call is what the ledger and the handle hang on, so the row's
    /// `amount` is the submission and never a total the native entry did not answer.</summary>
    private static void ApplySuccess()
    {
        using (var world = new ModifierWorld())
        {
            var a = world.Spawn(1);
            var b = world.Spawn(2);
            var result = world.Apply("set", new
            {
                targets = new[] { a.Reference, b.Reference }, source = a.Reference,
                attribute = Attribute, amount = 0.25, duration = 0
            });
            Check(result.Status == CommandStatuses.Succeeded && result.CommitState == CommitStates.Confirmed,
                "two recipients commit");
            Check(AgentModifierManager.Adds.Count == 2
                && AgentModifierManager.Adds[0].Modifier == AgentModifier.MovementSpeed
                && AgentModifierManager.Adds[0].Value == 0.25f && AgentModifierManager.Adds[0].DeltaPerSec == 0f
                && ReferenceEquals(AgentModifierManager.Adds[1].Agent, b.Agent),
                "each recipient's own agent reached the native entry with the submitted value");
            Check(world.Adapter.LiveModifiers == 2, "the ledger holds one id per write");
            var rows = Rows(result);
            Check(rows.Length == 2 && Row(rows, 0).GetProperty("target_count").GetInt32() == 2
                && Row(rows, 0).GetProperty("code").GetString() == "committed"
                && Row(rows, 0).GetProperty("amount").GetDouble() == 0.25
                && RuntimeJson.Entity(Row(rows, 0).GetProperty("target")) == a.Reference,
                "the row carries the target, the committed state, the submission and the command's count");
            Check(result.Outputs.TryGetProperty("modifier", out var handle)
                && handle.GetProperty("local").GetInt32() >= 0, "the apply returned the effect handle it minted");

            // `add` and `set` land as the same signed contribution because the native entry takes one contribution
            // and has no absolute-assignment form; `subtract` is that contribution with the opposite sign.
            var again = world.Apply("add", new
            {
                targets = new[] { a.Reference }, source = a.Reference, attribute = "movement-speed", amount = 0.5, duration = 0
            });
            Check(again.Status == CommandStatuses.Succeeded && AgentModifierManager.Adds[2].Value == 0.5f,
                "add submits the amount as it stands");
            world.Apply("subtract", new
            {
                targets = new[] { a.Reference }, source = a.Reference, attribute = "movement-speed", amount = 0.5, duration = 0
            });
            Check(AgentModifierManager.Adds[3].Value == -0.5f, "subtract submits the negated amount");
            Check(world.Adapter.LiveModifiers == 4, "every accepted write is one ledger entry");
        }
    }

    /// <summary>Every request the native entry cannot carry is refused by name before the first write, and a
    /// refusal leaves the native table and the ledger untouched.</summary>
    private static void ApplyRefusals()
    {
        using var world = new ModifierWorld();
        var a = world.Spawn(1);
        object Frame(object? targets = null, object? attribute = null, object? amount = null, object? duration = null) => new
        {
            targets = targets ?? new[] { a.Reference }, source = a.Reference,
            attribute = attribute ?? Attribute, amount = amount ?? 0.25, duration = duration ?? 0
        };

        Check(Code(world.Apply("set", Frame(attribute: "experience-change"))) == "attribute-unknown",
            "an attribute outside the native enum is refused");
        Check(Code(world.Apply("set", Frame(attribute: "none"))) == "attribute-no-op",
            "the no-op member is refused by name rather than written");
        Check(Code(world.Apply("set", Frame(amount: 0))) == "amount-out-of-range"
            && Code(world.Apply("set", Frame(amount: 2000000))) == "amount-out-of-range",
            "a zero or unbounded amount is refused");
        Check(Code(world.Apply("set", Frame(duration: -1))) == "duration-out-of-range"
            && Code(world.Apply("set", Frame(duration: 1.5))) == "duration-out-of-range",
            "a lifetime that is not a non-negative tick count is refused");
        Check(Code(world.Apply("multiply", Frame())) == "operation-unsupported",
            "the ruled-out operations never reach the native entry");
        Check(Code(world.Apply("set", Frame(targets: new[] { new EntityReference("gtfo.enemy:1", world.Kernel.WorldEpoch, 1) }))) == "modifier-target-kind",
            "a target of another kind is refused by name");
        Check(Code(world.Apply("set", Frame(targets: new[] { new EntityReference("gtfo.player:99", world.Kernel.WorldEpoch, 99) }))) == "stale-or-unsupported-recipient",
            "a player life the identity does not hold is refused");
        Check(AgentModifierManager.Adds.Count == 0 && world.Adapter.LiveModifiers == 0,
            "no refused request reached the native entry");

        // The identity half's own gate is the only authority check: a session that cannot commit writes nothing.
        world.CanObserve = false;
        Check(Code(world.Apply("set", Frame())) == "authority-or-phase" && AgentModifierManager.Adds.Count == 0,
            "a non-authoritative session is refused before the write");
        world.CanObserve = true;

        // An empty recipient set is not a failure: nothing was asked for, so nothing is written and no handle is
        // minted to name an effect nobody has.
        var none = world.Apply("set", Frame(targets: Array.Empty<EntityReference>()));
        Check(none.Status == CommandStatuses.Succeeded && Rows(none).Length == 0 && !none.Outputs.TryGetProperty("modifier", out _),
            "an empty recipient set commits nothing and names no effect");

        // The per-tick write budget is a provider counter: the 33rd write of one tick is refused and said so.
        var many = Enumerable.Range(0, 33).Select(i => world.Spawn((ulong)(100 + i))).ToArray();
        var over = world.Apply("set", new
        {
            targets = many.Select(m => m.Reference).ToArray(), source = a.Reference,
            attribute = Attribute, amount = 0.1, duration = 0
        });
        Check(over.Status == CommandStatuses.Partial && Row(Rows(over), 32).GetProperty("code").GetString() == "modifier-budget"
            && AgentModifierManager.Adds.Count == 32 && world.Adapter.LiveModifiers == 32,
            "the 33rd write of a tick is refused with the budget code");
        world.Advance(1);
        world.Apply("set", new { targets = new[] { many[0].Reference }, source = a.Reference, attribute = Attribute, amount = 0.1, duration = 0 });
        Check(AgentModifierManager.Adds.Count == 33, "a new tick reopens the write budget");

        // A zero id is the native entry registering nothing: refused, never retried and never invented.
        AgentModifierManager.Add = (_, _, _, _) => 0;
        Check(Code(world.Apply("set", Frame())) == "modifier-id-exhausted", "a zero id is a refusal");
        AgentModifierManager.Add = null;

        // A throwing native call leaves the commit unknown and stops the remaining recipients instead of retrying.
        var b = world.Spawn(500);
        AgentModifierManager.Add = (_, _, _, _) => throw new InvalidOperationException("native failure");
        var failed = world.Apply("set", new
        {
            targets = new[] { b.Reference, a.Reference }, source = a.Reference, attribute = Attribute, amount = 0.1, duration = 0
        });
        var failedRows = Rows(failed);
        Check(failed.Status == CommandStatuses.Failed && failed.CommitState == CommitStates.Unknown
            && Row(failedRows, 0).GetProperty("code").GetString() == "native-commit-exception"
            && Row(failedRows, 1).GetProperty("code").GetString() == "not-attempted-after-unknown-commit",
            "a native exception is an unknown commit and stops the command");
        AgentModifierManager.Add = null;
    }

    /// <summary>The effect handle's own cancel hook: a plan that cancels the handle an apply returned releases
    /// exactly the modifications that command wrote.</summary>
    private static void HandleCancel()
    {
        using var world = new ModifierWorld();
        var a = world.Spawn(1);
        var result = world.Apply("set", new
        {
            targets = new[] { a.Reference }, source = a.Reference, attribute = Attribute, amount = 0.25, duration = 0
        });
        var handle = result.Outputs.GetProperty("modifier");
        Check(world.Adapter.LiveModifiers == 1, "the applied modification is live before the cancel");
        var hook = CancelHook(world.Kernel, handle);
        Check(hook != null, "the minted handle carries the provider's cancel hook");
        hook!();
        Check(AgentModifierManager.Clears.Count == 1 && world.Adapter.LiveModifiers == 0,
            "cancelling the handle releases the modification it named");
    }

    /// <summary>Removal names what an apply handed out: the handle collection is resolved against the ledger, the
    /// optional attribute narrows it, and a request that cannot be carried out is refused as a whole before
    /// anything is cleared.</summary>
    private static void Remove()
    {
        using var world = new ModifierWorld();
        var a = world.Spawn(1);
        var b = world.Spawn(2);
        var applied = world.Apply("set", new
        {
            targets = new[] { a.Reference, b.Reference }, source = a.Reference, attribute = Attribute, amount = 0.25, duration = 0
        });
        var handle = applied.Outputs.GetProperty("modifier");

        Check(Code(world.Remove(new { modifiers = Array.Empty<object>() })) == "modifier-handle-missing",
            "a remove without a handle is refused");
        Check(Code(world.Remove(new
        {
            modifiers = new object[]
            {
                new { worldEpoch = world.Kernel.WorldEpoch, lifeEpoch = 1, local = 0, provider = 7 }
            }
        })) == "stale-handle", "a handle no group answers for is refused");
        Check(Code(world.Remove(new { modifiers = new object[] { handle }, attribute = "glue-strength" })) == "modifier-attribute-mismatch",
            "an attribute the handles were not written under is refused");
        Check(AgentModifierManager.Clears.Count == 0 && world.Adapter.LiveModifiers == 2,
            "no refused remove cleared anything");

        var removed = world.Remove(new { modifiers = new object[] { handle }, attribute = Attribute });
        var rows = Rows(removed);
        Check(removed.Status == CommandStatuses.Succeeded && rows.Length == 2
            && Row(rows, 0).GetProperty("target_count").GetInt32() == 2
            && RuntimeJson.Entity(Row(rows, 0).GetProperty("target")) == a.Reference
            && RuntimeJson.Entity(Row(rows, 1).GetProperty("target")) == b.Reference,
            "removal reports one row per modification with the life it belonged to");
        Check(AgentModifierManager.Clears.Count == 2 && world.Adapter.LiveModifiers == 0,
            "every modification the handle covered was released");
        Check(Code(world.Remove(new { modifiers = new object[] { handle } })) == "stale-handle",
            "a handle whose whole group was released is refused");
    }

    /// <summary>Lifetime: a duration expires, an entity loss releases, a world change and a stop release
    /// everything, and a native clear that throws keeps its id for the next pass instead of leaking silently.</summary>
    private static void Lifetimes()
    {
        // A duration is Forge's own: the native table has no expiry, so the ledger's tick is what releases it.
        using (var world = new ModifierWorld())
        {
            var a = world.Spawn(1);
            world.Apply("set", new
            {
                targets = new[] { a.Reference }, source = a.Reference, attribute = Attribute, amount = 0.25, duration = 5
            });
            world.Advance(4);
            Check(AgentModifierManager.Clears.Count == 0 && world.Adapter.LiveModifiers == 1, "a modification outlives an earlier tick");
            world.Advance(5);
            Check(AgentModifierManager.Clears.Count == 1 && world.Adapter.LiveModifiers == 0, "the due tick releases the modification");
        }

        using (var world = new ModifierWorld())
        {
            var a = world.Spawn(1);
            world.Apply("set", new
            {
                targets = new[] { a.Reference }, source = a.Reference, attribute = Attribute, amount = 0.25, duration = 0
            });
            world.Advance(1000);
            Check(AgentModifierManager.Clears.Count == 0 && world.Adapter.LiveModifiers == 1,
                "a modification without a duration is not released by time");
            world.Despawn(a);
            world.Advance(1001);
            Check(AgentModifierManager.Clears.Count == 1 && world.Adapter.LiveModifiers == 0,
                "a life that stopped resolving takes its modifications with it");
        }

        using (var world = new ModifierWorld())
        {
            var a = world.Spawn(1);
            var applied = world.Apply("set", new
            {
                targets = new[] { a.Reference }, source = a.Reference, attribute = Attribute, amount = 0.25, duration = 0
            });
            var handle = applied.Outputs.GetProperty("modifier");
            world.Kernel.BeginWorld(world.Kernel.WorldEpoch + 1);
            Check(AgentModifierManager.Clears.Count == 1 && world.Adapter.LiveModifiers == 0,
                "a new world releases every id the previous one held");
            Check(Code(world.Remove(new { modifiers = new object[] { handle } })) == "stale-handle",
                "a handle minted in the previous world is refused");
        }

        using (var world = new ModifierWorld())
        {
            var a = world.Spawn(1);
            world.Apply("set", new
            {
                targets = new[] { a.Reference }, source = a.Reference, attribute = Attribute, amount = 0.25, duration = 0
            });
            world.Kernel.StopRuntime();
            Check(AgentModifierManager.Clears.Count == 1 && world.Adapter.LiveModifiers == 0,
                "a stopped runtime releases the ids it can no longer own");
        }

        // A clear that throws is one diagnostic and a retained id: dropping it would leave a native modification
        // nothing could ever name again, and the world-end pass still gets to try it.
        using (var world = new ModifierWorld())
        {
            var a = world.Spawn(1);
            world.Apply("set", new
            {
                targets = new[] { a.Reference }, source = a.Reference, attribute = Attribute, amount = 0.25, duration = 3
            });
            AgentModifierManager.Clear = _ => throw new InvalidOperationException("native clear failure");
            world.Advance(3);
            Check(world.Adapter.LiveModifiers == 1 && world.Adapter.ClearFailures == 1,
                "a failed clear keeps its id and counts once");
            Check(world.Reports.Count(report => report.Contains("clear-failed", StringComparison.Ordinal)) == 1,
                "a failed clear is reported once, not once per tick");
            world.Advance(4);
            Check(world.Adapter.ClearFailures == 2
                && world.Reports.Count(report => report.Contains("clear-failed", StringComparison.Ordinal)) == 1,
                "the next pass retries without repeating the diagnostic");
            AgentModifierManager.Clear = null;
            world.Adapter.BeginWorld();
            Check(AgentModifierManager.Clears.Count == 3 && world.Adapter.LiveModifiers == 0,
                "the world-end pass releases what a failing clear left behind");
        }
    }

    /// <summary>One preset is two native writes per recipient under one handle: both members in the row's own order,
    /// the first recipient before the second, and the row carrying the speed the command asked for. The handle
    /// releases the whole preset, which is what makes the profile reversible.</summary>
    private static void MovementProfile()
    {
        using (var world = new ModifierWorld())
        {
            var a = world.Spawn(1);
            var b = world.Spawn(2);
            var result = world.Profile(new
            {
                targets = new[] { a.Reference, b.Reference }, source = a.Reference,
                speed = 1.5, acceleration = 0.5, duration = 0
            });
            Check(result.Status == CommandStatuses.Succeeded && result.CommitState == CommitStates.Confirmed,
                "both recipients commit the preset");
            Check(AgentModifierManager.Adds.Count == 4
                && AgentModifierManager.Adds[0].Modifier == AgentModifier.MovementSpeed
                && AgentModifierManager.Adds[0].Value == 1.5f
                && AgentModifierManager.Adds[1].Modifier == AgentModifier.MovementAcceleration
                && AgentModifierManager.Adds[1].Value == 0.5f
                && ReferenceEquals(AgentModifierManager.Adds[2].Agent, b.Agent),
                "each recipient's own agent took both movement members in the row's order");
            Check(world.Adapter.LiveModifiers == 4, "the ledger holds one id per write of the preset");
            var rows = Rows(result);
            Check(rows.Length == 2 && Row(rows, 0).GetProperty("speed").GetDouble() == 1.5
                && Row(rows, 0).GetProperty("target_count").GetInt32() == 2
                && Row(rows, 0).GetProperty("code").GetString() == "committed"
                && RuntimeJson.Entity(Row(rows, 0).GetProperty("target")) == a.Reference,
                "the row carries the target, the committed state, the speed and the command's count");
            Check(result.Outputs.TryGetProperty("profile_handle", out var handle)
                && handle.GetProperty("local").GetInt32() >= 0,
                "the preset returned the one effect handle its writes hang on");

            var removed = world.Remove(new { modifiers = new object[] { handle } });
            Check(removed.Status == CommandStatuses.Succeeded && AgentModifierManager.Clears.Count == 4
                && world.Adapter.LiveModifiers == 0,
                "the preset's own handle releases both members of every recipient");
        }
    }

    /// <summary>The preset's lifetime, its cancel hook and every input it cannot carry. A refusal happens before the
    /// first write of the whole command, the per-tick budget is spent two writes at a time, and a preset the native
    /// entry stops half way is revoked rather than left applied.</summary>
    private static void MovementProfileRefusals()
    {
        using var world = new ModifierWorld();
        var a = world.Spawn(1);
        object Frame(object? speed = null, object? acceleration = null, object? jumpGravity = null,
            object? duration = null, object? targets = null) => new
        {
            targets = targets ?? new[] { a.Reference }, source = a.Reference,
            speed = speed ?? 1.5, acceleration = acceleration ?? 0.5,
            jump_gravity = jumpGravity, duration = duration ?? 0
        };

        Check(Code(world.Profile(Frame(jumpGravity: 1.0))) == AgentModifierAdapter.GravityCode,
            "the input the native table cannot carry is refused by name");
        Check(Code(world.Profile(Frame(speed: 0))) == "amount-out-of-range"
            && Code(world.Profile(Frame(acceleration: 2000000))) == "amount-out-of-range",
            "a preset multiplier outside the native entry's range is refused, either member");
        Check(Code(world.Profile(Frame(duration: -1))) == "duration-out-of-range",
            "a lifetime that is not a non-negative tick count is refused");
        Check(Code(world.Profile(Frame(targets: new[] { new EntityReference("gtfo.enemy:1", world.Kernel.WorldEpoch, 1) })))
                == "modifier-target-kind",
            "a target of another kind is refused by name");
        Check(AgentModifierManager.Adds.Count == 0 && world.Adapter.LiveModifiers == 0,
            "no refused preset reached the native entry");

        world.CanObserve = false;
        Check(Code(world.Profile(Frame())) == "authority-or-phase", "a non-authoritative session is refused before the write");
        world.CanObserve = true;

        var none = world.Profile(Frame(targets: Array.Empty<EntityReference>()));
        Check(none.Status == CommandStatuses.Succeeded && Rows(none).Length == 0
            && !none.Outputs.TryGetProperty("profile_handle", out _),
            "an empty recipient set commits nothing and names no effect");

        // The per-tick budget is spent one preset at a time: the 17th recipient of a tick needs two writes the
        // budget no longer has, and finds neither.
        var many = Enumerable.Range(0, 17).Select(i => world.Spawn((ulong)(100 + i))).ToArray();
        var over = world.Profile(new
        {
            targets = many.Select(m => m.Reference).ToArray(), source = a.Reference,
            speed = 1.5, acceleration = 0.5, duration = 0
        });
        Check(over.Status == CommandStatuses.Partial
            && Row(Rows(over), 16).GetProperty("code").GetString() == "modifier-budget"
            && AgentModifierManager.Adds.Count == 32 && world.Adapter.LiveModifiers == 32,
            "the 17th preset of a tick is refused with the budget code");
        world.Advance(1);
        world.Profile(new { targets = new[] { many[0].Reference }, source = a.Reference, speed = 1.5, acceleration = 0.5, duration = 0 });
        Check(AgentModifierManager.Adds.Count == 34, "a new tick reopens the write budget");

        // A zero answer on the second write of a recipient: the first one landed and is revoked, so the command
        // reports the refusal instead of leaving half a preset behind.
        world.Adapter.BeginWorld();
        AgentModifierManager.Adds.Clear();
        AgentModifierManager.Clears.Clear();
        AgentModifierManager.Add = (_, modifier, _, _) => modifier == AgentModifier.MovementAcceleration ? 0u : 7u;
        Check(Code(world.Profile(Frame())) == "modifier-id-exhausted"
            && AgentModifierManager.Clears.SequenceEqual(new[] { 7u }) && world.Adapter.LiveModifiers == 0,
            "a preset the second write refuses is revoked, not left half applied");
        AgentModifierManager.Add = null;
    }

    private static int Value(string name) => AgentModifierValues.TryParse(name, out var modifier) ? (int)modifier : -1;

    private static string Code(CommandResult result) => result.Code;

    private static JsonElement[] Rows(CommandResult result)
        => result.Outputs.GetProperty("results").EnumerateArray().ToArray();

    private static JsonElement Row(JsonElement[] rows, int index) => rows[index];

    private static string[] Ports(JsonElement graph, string side)
        => graph.GetProperty(side).EnumerateArray().Select(port => port.GetProperty("id").GetString()!).ToArray();

    private static JsonElement Port(JsonElement graph, string side, string id)
        => graph.GetProperty(side).EnumerateArray().Single(port => port.GetProperty("id").GetString() == id);

    private static string[] Codes(JsonElement capability)
        => Port(capability.GetProperty("graph"), "outputs", "result").GetProperty("codes")
            .EnumerateArray().Select(code => code.GetString()!).ToArray();

    /// <summary>The cancel hook the provider registered on a handle it minted, read out of the kernel's own handle
    /// table: the kernel runs it from a `cancel` control step, and a fixture has no plan to walk, so it invokes
    /// the same delegate the kernel would.</summary>
    private static Action? CancelHook(RuntimeKernel kernel, JsonElement handle)
    {
        var slots = (System.Collections.IEnumerable)typeof(RuntimeKernel)
            .GetField("handleSlots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        int local = handle.GetProperty("local").GetInt32();
        long generation = handle.GetProperty("lifeEpoch").GetInt64();
        object? slot = slots.Cast<object?>().ElementAt(local);
        if (slot == null) return null;
        var type = slot.GetType();
        if ((int)type.GetField("Generation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot)! != generation) return null;
        return type.GetField("Cancel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot) as Action;
    }
}
