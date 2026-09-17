using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the interaction-prompt row (`forge.action.presentation.interaction_text`): the one action
/// that writes one object's prompt rule, and the readback the interaction prompt goes through.
///
/// The rule is held per map object and applied where the game asks for the prompt, not by rewriting any text the
/// game owns: `Interact_Base.InteractionMessage` is the member the interaction layer draws, and both it and the
/// timed override `Interact_Timed` are patched, because a door's button is a timed interaction and a patch on the
/// base alone would never see the derived getter's answer. The two patches share one depth guard so a derived
/// getter that calls the base one applies the rule exactly once.
///
/// The rule table is keyed by the map object's own component, and the patch climbs from the interactable it was
/// handed to that owner: a terminal's interactable is `Interact_ComputerTerminal` and its `m_terminal` is the
/// `LG_ComputerTerminal` the map-object address is built from, while a door's interactable sits under the
/// `LG_SecurityDoor` the address names. The climb runs terminal first on purpose — a terminal mounted on a door
/// belongs to the terminal, and a door-first climb would hand its prompt to the door. An interactable no
/// map-object category owns answers no rule and keeps the game's own prompt.
///
/// The glitch is read off the game clock rather than animated by an update loop of this package's own:
/// the prompt's own redraws sample the rule's refresh interval, so no per-frame work is added here. If the prompt
/// turns out to be drawn once per selection instead of per frame, the styles would be static rather than moving —
/// that is the one part of this row a run in the game has to settle.
///
/// Nothing here writes object state: a prompt cannot unlock, open or alarm anything. The row is a presentation
/// row, so the host decides when the step runs and every addressed client writes its own copy of the rule; a
/// handler that is not on the host still writes, which is why this half never reads `IsHost`.</summary>
internal sealed class InteractionTextActions
{
    /// <summary>This machine cannot write a prompt yet: the session is not ready, or the runtime is not at a
    /// point where a step may run.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    /// <summary>The reference names no interactable map object this provider addresses.</summary>
    internal const string KindCode = "interaction-text-unsupported-recipient";
    /// <summary>The kernel no longer answers for the reference, or its object is gone.</summary>
    internal const string StaleCode = "interaction-text-stale";
    /// <summary>A request that named no target at all.</summary>
    internal const string NoTargetsCode = "interaction-text-no-targets";
    /// <summary>More recipients than the result row budget allows; refused before anything is written.</summary>
    internal const string TargetsCode = "too-many-targets";
    internal const string AllRejectedCode = "interaction-text-all-rejected";
    internal const string AllUnknownCode = "interaction-text-all-unknown";
    /// <summary>A rule that now applies to the recipient's prompt.</summary>
    internal const string AppliedCode = "interaction-text-applied";
    /// <summary>The recipient's prompt was put back to the game's own text.</summary>
    internal const string ClearedCode = "interaction-text-cleared";

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;

    internal InteractionTextActions(RuntimeKernel kernel, Func<bool> ready, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The rules in force, one per map object. The prompt patch reads this table and this action is its
    /// only writer; access is serialized because a patch can run while an action is being applied.</summary>
    private static readonly object RulesGate = new();
    private static readonly Dictionary<Component, InteractionTextRule> Rules = new();

    internal CommandResult Handle(CommandContext context) => Apply(context.Inputs, context.Parameters);

    /// <summary>The row's whole decision: the rule is read once for the command — a request this layer cannot
    /// carry out as asked must not half-apply — and then written per recipient, with a refusal code of its own for
    /// every recipient that could not be reached.</summary>
    internal CommandResult Apply(JsonElement inputs, JsonElement parameters)
    {
        if (!Ready()) return CommandResult.Rejected(AuthorityCode);
        var text = inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty("text", out var written)
            ? written : default;
        if (!InteractionText.TryRead(parameters, text, out var rule, out var code))
            return CommandResult.Rejected(code);
        var targets = Recipients(inputs, "targets");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);

        var rows = new List<InteractionTextRow>(targets.Length);
        foreach (var target in targets)
        {
            if (!Resolve(target, out var owner, out var refusal))
            {
                rows.Add(Refused(target, refusal, targets.Length));
                continue;
            }
            Write(owner, rule);
            rows.Add(Issued(target, rule, targets.Length));
        }
        return Aggregate(rows);
    }

    /// <summary>Whether this machine may write a prompt at all. A prompt is drawn by the machine the player sits
    /// at, so every addressed client writes its own copy and the host fact is deliberately not part of this gate;
    /// what is left is the session's readiness and the runtime's own started world.</summary>
    private bool Ready()
        => _ready() && _kernel.Lifecycle.StartupState == RuntimeStartupState.Ready;

    /// <summary>One map object behind one recipient reference: the reference has to be one the kernel still
    /// answers for, and the address it carries has to resolve through the category reader that owns it — a door
    /// through the door reader, a terminal through the terminal reader. A reference neither category claims is
    /// refused by name rather than read through the wrong one.</summary>
    private bool Resolve(EntityReference? reference, out Component owner, out string refusal)
    {
        owner = null!;
        if (reference == null) { refusal = KindCode; return false; }
        if (!_kernel.IsEntityCurrent(reference)) { refusal = StaleCode; return false; }
        if (TerminalObjectActions.Address(reference) is { } terminalAddress)
        {
            if (TerminalObservation.ByAddress(terminalAddress) is not { } terminal)
            {
                refusal = StaleCode;
                return false;
            }
            owner = terminal;
            refusal = "";
            return true;
        }
        if (DoorActionCommands.IsDoor(reference))
        {
            if (DoorActionCommands.ResolveByAddress(reference) is not { } door)
            {
                refusal = StaleCode;
                return false;
            }
            owner = door;
            refusal = "";
            return true;
        }
        refusal = KindCode;
        return false;
    }

    private static void Write(Component owner, InteractionTextRule rule)
    {
        lock (RulesGate)
        {
            if (rule.IsClear) Rules.Remove(owner);
            else Rules[owner] = rule;
        }
    }

    /// <summary>The one rule in force for the map object an interactable belongs to, or null. Read by the prompt
    /// patches, which is the only caller.</summary>
    private static InteractionTextRule? RuleFor(Component instance)
    {
        if (OwnerOf(instance) is not { } owner) return null;
        lock (RulesGate) return Rules.TryGetValue(owner, out var rule) ? rule : null;
    }

    /// <summary>The map object an interactable's prompt belongs to: a terminal through the interactable's own
    /// `m_terminal`, otherwise the door the interactable sits under. Anything else is an interactable no
    /// map-object category addresses, and it keeps the game's own prompt.</summary>
    private static Component? OwnerOf(Component instance)
    {
        try
        {
            if (instance.TryCast<Interact_ComputerTerminal>() is { } interactable && interactable.m_terminal != null)
                return interactable.m_terminal;
            return instance.GetComponentInParent<LG_SecurityDoor>();
        }
        catch (Exception) { return null; }
    }

    /// <summary>One recipient's row of the result, in the runtime's own column order: the four fixed columns and
    /// the text this recipient's prompt now shows. The presentation tier commits no world state, so `committed` is
    /// always `none` — either the prompt now shows the rule, or the row names why it does not.</summary>
    private readonly record struct InteractionTextRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code, string Text,
        [property: JsonPropertyName("target_count")] int TargetCount);

    private static InteractionTextRow Issued(EntityReference target, InteractionTextRule rule, int count)
        => new(target, CommandStatuses.Succeeded, CommitStates.None,
            rule.IsClear ? ClearedCode : AppliedCode, rule.IsClear ? "" : rule.Text, count);

    private static InteractionTextRow Refused(EntityReference target, string code, int count)
        => new(target, CommandStatuses.Rejected, CommitStates.None, code, "", count);

    /// <summary>The command-level conclusion, in the shape a presentation row has to end in: every recipient
    /// applied is a success, none is a rejection, and anything in between is a partial — all three with no commit
    /// state, because a prompt is not world state.</summary>
    private static CommandResult Aggregate(IReadOnlyList<InteractionTextRow> rows)
    {
        var outputs = RuntimeJson.From(new { results = rows });
        int applied = 0;
        foreach (var row in rows) if (row.Status == CommandStatuses.Succeeded) applied++;
        if (applied == rows.Count) return CommandResult.Create(CommandStatuses.Succeeded, CommitStates.None, Single(rows, ""), "", outputs);
        if (applied == 0) return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(rows, AllRejectedCode), "", outputs);
        return CommandResult.Create(CommandStatuses.Partial, CommitStates.None, Single(rows, AllUnknownCode), "", outputs);
    }

    /// <summary>The one code a command reports: the single row's own code, or the shared code when the rows
    /// disagreed.</summary>
    private static string Single(IReadOnlyList<InteractionTextRow> rows, string fallback)
    {
        var first = rows[0].Code;
        foreach (var row in rows) if (row.Code != first) return fallback;
        return first;
    }

    private static EntityReference[] Recipients(JsonElement inputs, string port)
        => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(port, out var value)
           && value.ValueKind == JsonValueKind.Array
            ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(value.EnumerateArray(), RuntimeJson.Entity))
            : Array.Empty<EntityReference>();

    /// <summary>Whether a prompt read is nested inside another patched prompt read on this thread, and the depth
    /// the patch pair keeps: the outermost call is the one that applies the rule, so a derived getter calling the
    /// base one applies it once.</summary>
    [ThreadStatic] private static int _depth;

    internal static bool Enter()
    {
        _depth++;
        return _depth > 1;
    }

    internal static void Exit(Component instance, ref string result, bool nested)
    {
        _depth--;
        if (nested || result == null) return;
        if (RuleFor(instance) is not { } rule) return;
        result = rule.Render(result, Time.time);
    }
}

/// <summary>The prompt every interactable inherits: the game draws `InteractionMessage` and this patch rewrites it
/// for a map object that carries a rule.</summary>
[HarmonyPatch(typeof(Interact_Base), "get_InteractionMessage")]
internal static class InteractionTextPromptReadback
{
    [HarmonyPrefix]
    private static void Prefix(out bool __state) => __state = InteractionTextActions.Enter();

    [HarmonyPostfix]
    private static void Postfix(Interact_Base __instance, ref string __result, bool __state)
        => InteractionTextActions.Exit(__instance, ref __result, __state);
}

/// <summary>The timed override, which is the getter a door button actually answers with.</summary>
[HarmonyPatch(typeof(Interact_Timed), "get_InteractionMessage")]
internal static class TimedInteractionTextPromptReadback
{
    [HarmonyPrefix]
    private static void Prefix(out bool __state) => __state = InteractionTextActions.Enter();

    [HarmonyPostfix]
    private static void Postfix(Interact_Timed __instance, ref string __result, bool __state)
        => InteractionTextActions.Exit(__instance, ref __result, __state);
}
