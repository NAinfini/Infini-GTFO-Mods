using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMap;
using ForgeRuntime.Framework;
using GameData;
using LevelGeneration;
using Localization;
using Player;

namespace ForgeMap.Native;

/// <summary>
/// One zone address the environment rows act on: the level's own three coordinates, exactly the three the two
/// zone-addressed light entries and the fog entry carry (`DimensionIndex`, `Layer`, `LocalIndex`). The reference
/// text is the `gtfo.zone` grammar <see cref="RuntimeZones"/> writes, so a zone a selector answered and a zone
/// this layer reads are the same place.
/// </summary>
internal readonly struct EnvironmentZone
{
    private EnvironmentZone(int dimension, int layer, int zone)
    {
        Dimension = dimension; Layer = layer; Zone = zone;
    }

    internal int Dimension { get; }
    internal int Layer { get; }
    internal int Zone { get; }

    /// <summary>Whether each of the three coordinates is inside the enum the game indexes that axis with. The
    /// check is made here rather than at the cast, because a cast of an out-of-range integer produces a member
    /// the game never assigned and the entry would then read a zone that does not exist.</summary>
    internal bool InRange => Dimension >= 0 && Dimension < (int)eDimensionIndex.MAX_COUNT
        && Layer >= 0 && Layer <= (int)LG_LayerType.ThirdLayer
        && Zone >= 0 && Zone <= (int)eLocalZoneIndex.Zone_19;

    internal eDimensionIndex DimensionIndex => (eDimensionIndex)Dimension;
    internal LG_LayerType LayerType => (LG_LayerType)Layer;
    internal eLocalZoneIndex LocalIndex => (eLocalZoneIndex)Zone;

    internal GlobalZoneIndex Global => new(Dimension, Layer, Zone);

    /// <summary>One `gtfo.zone:<dimension>:<layer>:<zone>` reference, or null when the text is not that
    /// grammar. Nothing is inferred from a shorter or longer id: an address this layer cannot read is refused
    /// rather than defaulted to the reality layer.</summary>
    internal static EnvironmentZone? Parse(EntityReference reference)
    {
        string id = reference.Id ?? "";
        string prefix = RuntimeZones.EntityKind + ":";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = id.Substring(prefix.Length).Split(':');
        if (parts.Length != 3) return null;
        var numbers = new int[3];
        for (var index = 0; index < 3; index++)
            if (!int.TryParse(parts[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out numbers[index]))
                return null;
        var zone = new EnvironmentZone(numbers[0], numbers[1], numbers[2]);
        return zone.InRange ? zone : null;
    }
}

/// <summary>
/// The host half of the environment rows. Six of the nine checklist rows the game executes itself are one
/// `WardenObjectiveEventData` handed to the game's own level-event entry, and every one of them is built here:
/// the type member selects the effect, the fields carry the parameters, and the game's own execution entry
/// replicates whatever the effect replicates. Nothing in this file re-implements an effect's state, which is why
/// there is no light table, no fog table and no marker table in it — the game owns all three and publishes them
/// through its own state replicator.
///
/// The whole plan-level request check runs before the first write, because these entries are fire-and-forget:
/// `ExecuteEvent` returns nothing, so an argument that cannot be carried has to be refused before the entry is
/// reached rather than reported afterwards. That is also why every handler's result is a row per named zone with
/// the commit the entry was invoked under, and never a claim about the state the effect will end in.
/// </summary>
internal sealed class EnvironmentActions
{
    /// <summary>The one authority refusal, spelled the way the other Map handlers spell it.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    /// <summary>A request that named no target at all. The recipient contract makes the port non-optional, so
    /// this is reached by an empty collection rather than by an absent one.</summary>
    internal const string NoTargetCode = "environment-target-required";
    internal const string TargetCode = "environment-target-unsupported";
    internal const string StaleTargetCode = "environment-target-stale";
    internal const string ScopeCode = "lighting-scope-unsupported";
    internal const string ModeCode = "lighting-mode-unsupported";
    internal const string FogRequiredCode = "fog-preset-required";
    internal const string SlotRequiredCode = "fog-cycle-slot-required";
    internal const string FilterRequiredCode = "world-event-object-filter-required";
    internal const string FilterTooLongCode = "world-event-object-filter-too-long";
    /// <summary>The level's event manager is not there to run the entry: no level is loaded, or the session is
    /// shutting down. Every row answers this rather than calling a static entry on a torn-down manager.</summary>
    internal const string UnavailableCode = "level-event-unavailable";
    /// <summary>A `position` port that is present and not three finite numbers. It is refused rather than
    /// narrowed, because the entry would read the position it was handed.</summary>
    internal const string PositionCode = "environment-position-invalid";
    /// <summary>A negative `count`. The entry reads it as a light budget, so a negative one is refused.</summary>
    internal const string CountCode = "environment-count-invalid";
    /// <summary>The game's own maximum for `WorldEventObjectFilter`: a name longer than this cannot be one.</summary>
    internal const int MaximumFilterLength = 512;

    private readonly Func<bool> _canExecute;
    private readonly Func<WorldEventManager?> _events;
    private readonly Action<string> _report;

    internal EnvironmentActions(Func<bool> canExecute, Func<WorldEventManager?> events, Action<string> report)
    {
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The production wiring: the session's own readiness gate and the level event manager, read late
    /// through the property that owns it. A world that was torn down leaves the property null, which is what
    /// every row refuses as <see cref="UnavailableCode"/>.</summary>
    internal static EnvironmentActions For(Func<bool> canExecute, Action<string> report)
        => new(canExecute, () => WorldEventManager.Current, report);

    internal CommandResult HandleLighting(CommandContext context) => Lighting(context);
    internal CommandResult HandleFog(CommandContext context) => Fog(context);
    internal CommandResult HandleFogCycle(CommandContext context) => FogCycle(context);
    internal CommandResult HandleNavMarker(CommandContext context) => NavMarker(context);
    internal CommandResult HandleAnimation(CommandContext context) => Animation(context);

    /// <summary>The `forge.action.presentation.lighting` command: the level-wide and the zone-addressed light
    /// entries, one row per zone the request named.</summary>
    internal CommandResult Lighting(CommandContext context)
    {
        if (!Authoritative(context)) return CommandResult.Rejected(AuthorityCode);
        string? scope = Text(context.Parameters, "scope");
        if (scope != "zones" && scope != "expedition") return CommandResult.Rejected(ScopeCode);
        string? mode = Text(context.Parameters, "mode");
        int modeIndex = mode == null ? -1 : Array.IndexOf(EnvironmentContract.LightingModes, mode);
        if (modeIndex < 0) return CommandResult.Rejected(ModeCode);
        // The game's event table has no level-wide flip, so the one member that would have to be served as a
        // read-then-write is refused instead of being approximated by one.
        if (scope == "expedition" && mode == "toggle") return CommandResult.Rejected(ModeCode);
        if (!Targets(context, "zones", out var zones, out var refusal)) return refusal;
        // `position` and `count` are the zone entry's own sequence arguments: the game picks at most `count`
        // lights around `position`. They are read once for the whole request and written into every zone's event,
        // and a present-but-mis-shaped vector is refused rather than narrowed to the origin.
        bool hasPosition = TryVector(context.Inputs, "position", out var position, out bool malformedPosition);
        if (malformedPosition) return CommandResult.Rejected(PositionCode);
        int count = Integer(context.Inputs, "count", 0);
        if (count < 0) return CommandResult.Rejected(CountCode);
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);

        float transition = (float)Number(context.Inputs, "transition", 0);
        var rows = new List<EnvironmentRow>(zones.Count);
        if (scope == "expedition")
        {
            var data = Event(mode == "on" ? eWardenObjectiveEventType.AllLightsOn : eWardenObjectiveEventType.AllLightsOff);
            data.DimensionIndex = zones[0].DimensionIndex;
            rows.Add(Issue(context, data, zones[0]));
        }
        else
        {
            var type = mode == "toggle" ? eWardenObjectiveEventType.LightsInZoneToggle : eWardenObjectiveEventType.LightsInZone;
            foreach (var zone in zones)
            {
                var data = Event(type);
                data.DimensionIndex = zone.DimensionIndex;
                data.Layer = (LG_LayerType)zone.Layer;
                data.LocalIndex = zone.LocalIndex;
                if (type == eWardenObjectiveEventType.LightsInZone) data.Enabled = mode == "on";
                data.Duration = transition;
                if (hasPosition) data.Position = position;
                data.Count = count;
                rows.Add(Issue(context, data, zone));
            }
        }
        return Issued(rows);
    }

    /// <summary>The `forge.action.presentation.fog` command: the game's own fog transition, whose three
    /// arguments are the fog block id, the transition duration and the dimension the named zone belongs to.</summary>
    internal CommandResult Fog(CommandContext context)
    {
        if (!Authoritative(context)) return CommandResult.Rejected(AuthorityCode);
        if (!Targets(context, "zone", out var zones, out var refusal)) return refusal;
        int fog = Integer(context.Inputs, "fog", -1);
        if (fog < 0) return CommandResult.Rejected(FogRequiredCode);
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);
        var data = Event(eWardenObjectiveEventType.SetFogSetting);
        var zone = zones[0];
        data.DimensionIndex = zone.DimensionIndex;
        data.Layer = (LG_LayerType)zone.Layer;
        data.LocalIndex = zone.LocalIndex;
        data.FogSetting = (uint)fog;
        data.FogTransitionDuration = (float)Number(context.Inputs, "transition", 0);
        return Issued(new List<EnvironmentRow> { Issue(context, data, zone) });
    }

    /// <summary>The `forge.action.presentation.fog_cycle` command: the game's sustained-event pair. `start`
    /// fills the slot's own state count, state duration, start delay, fog parameters and loop sound; `stop`
    /// clears the slot and reads none of them, which is why the stop row carries the same shape without
    /// requiring any of the fields it does not use.</summary>
    internal CommandResult FogCycle(CommandContext context)
    {
        if (!Authoritative(context)) return CommandResult.Rejected(AuthorityCode);
        string? mode = Text(context.Parameters, "mode");
        if (mode != "start" && mode != "stop") return CommandResult.Rejected(ModeCode);
        if (!Targets(context, "zone", out var zones, out var refusal)) return refusal;
        int slot = Integer(context.Parameters, "slot", -1);
        if (slot < 0) return CommandResult.Rejected(SlotRequiredCode);
        int fog = Integer(context.Inputs, "fog", -1);
        if (mode == "start" && fog < 0) return CommandResult.Rejected(FogRequiredCode);
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);

        var zone = zones[0];
        var data = Event(mode == "start"
            ? eWardenObjectiveEventType.StartRepeatingFog
            : eWardenObjectiveEventType.StopSustainedEvent);
        data.DimensionIndex = zone.DimensionIndex;
        data.Layer = (LG_LayerType)zone.Layer;
        data.LocalIndex = zone.LocalIndex;
        data.SustainedEventSlotIndex = slot;
        if (mode == "start")
        {
            // The game's own "loop until stopped" value. It is the default the vanilla slot uses, so an absent
            // `states` port means the same thing the level data means by it.
            data.SustainedEventStateCount = Integer(context.Parameters, "states", -1);
            data.SustainedEventStateDuration = (float)Number(context.Inputs, "state_duration", 0);
            data.SustainedEventDelay = (float)Number(context.Inputs, "start_delay", 0);
            data.FogSetting = (uint)fog;
            data.FogTransitionDuration = (float)Number(context.Inputs, "transition", 0);
            int sound = Integer(context.Inputs, "sound", -1);
            if (sound >= 0) data.SoundID = (uint)sound;
        }
        return Issued(new List<EnvironmentRow> { Issue(context, data, zone) });
    }

    /// <summary>The `forge.action.presentation.hud_marker` command: the game's own navigation marker on the
    /// world event object the filter names, shown or hidden by the same entry the level data uses.</summary>
    internal CommandResult NavMarker(CommandContext context)
    {
        if (!Authoritative(context)) return CommandResult.Rejected(AuthorityCode);
        if (!Filter(context, out var filter, out var refusal)) return refusal;
        if (!Enabled(context, out bool enabled)) return refusal;
        if (!Targets(context, "zone", out var zones, out refusal)) return refusal;
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);
        var data = Event(eWardenObjectiveEventType.SetNavMarker);
        var zone = zones[0];
        data.DimensionIndex = zone.DimensionIndex;
        data.Layer = (LG_LayerType)zone.Layer;
        data.LocalIndex = zone.LocalIndex;
        data.WorldEventObjectFilter = filter;
        data.Enabled = enabled;
        return Issued(new List<EnvironmentRow> { Issue(context, data, zone) });
    }

    /// <summary>The `forge.action.presentation.animation_trigger` command: the game's own scene animation
    /// trigger. `enabled` is the game's own play/reset flag — the entry plays the named object's animation
    /// components and activates its objects, and resets the same components when the flag is false.</summary>
    internal CommandResult Animation(CommandContext context)
    {
        if (!Authoritative(context)) return CommandResult.Rejected(AuthorityCode);
        if (!Filter(context, out var filter, out var refusal)) return refusal;
        if (!Enabled(context, out bool enabled)) return refusal;
        if (!Targets(context, "zone", out var zones, out refusal)) return refusal;
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);
        var data = Event(eWardenObjectiveEventType.AnimationTrigger);
        var zone = zones[0];
        data.DimensionIndex = zone.DimensionIndex;
        data.Layer = (LG_LayerType)zone.Layer;
        data.LocalIndex = zone.LocalIndex;
        data.WorldEventObjectFilter = filter;
        data.Enabled = enabled;
        return Issued(new List<EnvironmentRow> { Issue(context, data, zone) });
    }

    /// <summary>One event of the given type with the game's own "check no condition" gate written explicitly. A
    /// freshly constructed event carries the condition struct's zero value, which is slot 0 expecting false —
    /// a gate no vanilla event asks for — so the gate every row means is written here rather than inherited.</summary>
    internal static WardenObjectiveEventData Event(eWardenObjectiveEventType type)
    {
        var data = new WardenObjectiveEventData { Type = type };
        data.Condition = new WorldEventConditionPair { ConditionIndex = NoCondition };
        return data;
    }

    /// <summary>The game's own "this event checks no condition slot" index.</summary>
    internal const int NoCondition = -1;

    /// <summary>Runs one built event through the game's own level-event entry and answers the one row that entry
    /// can support: the entry returns nothing, so the row reports that it was invoked and never a state.</summary>
    private EnvironmentRow Issue(CommandContext context, WardenObjectiveEventData data, EnvironmentZone zone)
    {
        var target = RuntimeZones.Reference(context.WorldEpoch, zone.Dimension, zone.Layer, zone.Zone);
        try
        {
            WorldEventManager.ExecuteEvent(data, 0f);
            return EnvironmentRow.For(target, CommandStatuses.Succeeded, CommitStates.Confirmed, "level-event-issued");
        }
        catch (Exception error)
        {
            // The entry is fire-and-forget, so a throw is the only failure it can report. It is recorded as an
            // unknown commit rather than a clean refusal: part of the effect may already have been applied.
            Report("map.environment-event-failed: " + error.GetType().Name + ": " + error.Message);
            return EnvironmentRow.For(target, CommandStatuses.Failed, CommitStates.Unknown, "level-event-exception");
        }
    }

    private bool Authoritative(CommandContext context) => context.IsHost && _canExecute();

    /// <summary>The zone references a request named, in the order it supplied them: the row's recipient port is
    /// one entity for a request that acts on one place and a collection for a request that acts on several, and
    /// both spellings arrive here as the one list the entries are built from. Every member has to be a zone of
    /// this world: a reference of another kind, of a malformed address or of an earlier world is refused by name,
    /// because the entry would otherwise be handed a coordinate nobody assigned.</summary>
    private bool Targets(CommandContext context, string port, out List<EnvironmentZone> zones, out CommandResult refusal)
    {
        zones = new List<EnvironmentZone>();
        refusal = CommandResult.Rejected(NoTargetCode);
        if (!context.Inputs.TryGetProperty(port, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (!Target(context, item, zones, out refusal)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (!Target(context, value, zones, out refusal)) return false;
        }
        else return false;
        return zones.Count != 0;
    }

    private static bool Target(CommandContext context, JsonElement item, List<EnvironmentZone> zones,
        out CommandResult refusal)
    {
        refusal = CommandResult.Rejected(TargetCode);
        EntityReference reference;
        try { reference = RuntimeJson.Entity(item); }
        catch (Exception) { return false; }
        if (reference.WorldEpoch != context.WorldEpoch)
        {
            refusal = CommandResult.Rejected(StaleTargetCode);
            return false;
        }
        if (EnvironmentZone.Parse(reference) is not { } zone) return false;
        zones.Add(zone);
        return true;
    }

    /// <summary>The world event object name a request names. The name is what the game's own object registry is
    /// queried with, so an absent or empty one is refused rather than looked up as the empty string.</summary>
    private static bool Filter(CommandContext context, out string filter, out CommandResult refusal)
    {
        refusal = CommandResult.Rejected(FilterRequiredCode);
        filter = Text(context.Inputs, "filter") ?? "";
        if (filter.Length == 0) return false;
        if (filter.Length > MaximumFilterLength)
        {
            refusal = CommandResult.Rejected(FilterTooLongCode);
            return false;
        }
        return true;
    }

    private static bool Enabled(CommandContext context, out bool enabled)
    {
        enabled = false;
        if (!context.Inputs.TryGetProperty("enabled", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        enabled = value.ValueKind == JsonValueKind.True;
        return true;
    }

    /// <summary>One host row: the named zone the entry was invoked for, the status the invocation was recorded
    /// under, and the commit the entry's own fire-and-forget nature allows. A presentation row carries no target
    /// at all — its write lands on a screen, not on an object — so the column is left out rather than filled
    /// with a reference to something the request never named.</summary>
    internal sealed record EnvironmentRow(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntityReference? Target,
        string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code)
    {
        internal static EnvironmentRow For(EntityReference? target, string status, string commitState, string code)
            => new(target, status, commitState, code);
    }

    internal static JsonElement Envelope(IReadOnlyList<EnvironmentRow> rows) => RuntimeJson.From(new { results = rows });

    /// <summary>The host aggregate: every row invoked is a committed request, a row that failed is an unknown
    /// commit, and a request whose rows are all refusals is a refusal. There is no "partly invoked" status the
    /// game's own entry can distinguish, so the aggregate never claims one.</summary>
    internal static CommandResult Issued(IReadOnlyList<EnvironmentRow> rows)
    {
        var outputs = Envelope(rows);
        int unknown = rows.Count(row => row.CommitState == CommitStates.Unknown);
        if (unknown == rows.Count) return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, rows[0].Code, "", outputs);
        if (unknown > 0) return CommandResult.Partial(outputs, CommitStates.Unknown);
        return CommandResult.Succeeded(outputs);
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception) { /* a reporter that throws must not replace the outcome it reports */ }
    }

    internal static string? Text(JsonElement bag, string name)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static int Integer(JsonElement bag, string name, int fallback)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : fallback;

    internal static double Number(JsonElement bag, string name, double fallback)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : fallback;

    /// <summary>One vector3 input as the game's own type. Absent and explicitly null both mean "the plan named
    /// none"; a value that is present and not three finite numbers sets <paramref name="malformed"/>, so the
    /// caller refuses it rather than narrowing a broken position to the origin.</summary>
    internal static bool TryVector(JsonElement bag, string name, out UnityEngine.Vector3 vector, out bool malformed)
    {
        vector = default;
        malformed = false;
        if (bag.ValueKind != JsonValueKind.Object || !bag.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
        {
            malformed = true;
            return false;
        }
        var components = new float[3];
        int cursor = 0;
        foreach (var component in value.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out double number)
                || !float.IsFinite((float)number))
            {
                malformed = true;
                return false;
            }
            components[cursor++] = (float)number;
        }
        vector = new UnityEngine.Vector3(components[0], components[1], components[2]);
        return true;
    }
}

/// <summary>
/// The recipient half of the six presentation rows: the two level events whose whole effect is local to the
/// machine that runs it (a sound, and the paired stop of a looping one), the warden-intel line, the two that pick a
/// local player (the nearest-player line and a player's own voice event) and the HUD readout.
///
/// They run on each addressed client, which is exactly what the kernel's `presentation` tier means: the host
/// decided which step runs and for whom, and the write is the recipient's own. Nothing here reads a world fact
/// the plan did not carry, and nothing here writes anything the kernel would have to replicate — which is also
/// why every result is a non-committing one and why a handler that reports a commit is refused by the tier
/// rather than trusted.
/// </summary>
internal sealed class EnvironmentPresentation
{
    internal const string ViewerRequiredCode = "viewers-required";
    internal const string ViewerCode = "viewers-unsupported";
    internal const string TextRequiredCode = "intel-text-required";
    internal const string SoundRequiredCode = "sound-id-required";
    internal const string DialogueRequiredCode = "dialogue-id-required";
    internal const string VoiceRequiredCode = "voice-id-required";
    /// <summary>A speaker port that is absent, is not a player entity, or names a life this process cannot read a
    /// slot from. The game's own entry takes a slot index, so a speaker nobody can convert is refused rather than
    /// handed to the manager.</summary>
    internal const string SpeakerCode = "player-speaker-unsupported";
    internal const string UnavailableCode = EnvironmentActions.UnavailableCode;
    internal const string SubtitleCode = "sound-subtitle-too-long";
    /// <summary>The game's own bound on one line of text: the subtitle and the intel line are both handed to a
    /// `LocalizedText`, whose string form the game renders into one bounded label.</summary>
    internal const int MaximumTextLength = 1024;

    private readonly Func<WorldEventManager?> _events;
    private readonly Func<int, bool> _playerExists;
    private readonly Action<string> _report;

    internal EnvironmentPresentation(Func<WorldEventManager?> events, Func<int, bool> playerExists, Action<string> report)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _playerExists = playerExists ?? throw new ArgumentNullException(nameof(playerExists));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The production wiring: the level event manager and the game's own "is there a player in this
    /// slot" predicate, both read late, so a torn-down level is refused rather than called into.</summary>
    internal static EnvironmentPresentation For(Action<string> report)
        => new(() => WorldEventManager.Current, PlayerSlotExists, report);

    internal CommandResult HandleAudio(CommandContext context) => Audio(context);
    internal CommandResult HandleAudioStop(CommandContext context) => AudioStop(context);
    internal CommandResult HandleIntel(CommandContext context) => Intel(context);
    internal CommandResult HandleDialogue(CommandContext context) => Dialogue(context);
    internal CommandResult HandlePlayerVoice(CommandContext context) => PlayerVoice(context);

    /// <summary>The `forge.action.presentation.audio_play` command: the game's own sound event, run on each
    /// addressed client so each one plays it locally. The optional subtitle is free text the plan computes.</summary>
    internal CommandResult Audio(CommandContext context)
    {
        if (!Audience(context, out var refusal)) return refusal;
        int sound = EnvironmentActions.Integer(context.Inputs, "sound", -1);
        if (sound < 0) return CommandResult.Rejected(SoundRequiredCode);
        string? subtitle = EnvironmentActions.Text(context.Inputs, "subtitle");
        if (subtitle is { Length: > MaximumTextLength }) return CommandResult.Rejected(SubtitleCode);
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);
        var data = EnvironmentActions.Event(eWardenObjectiveEventType.PlaySound);
        data.SoundID = (uint)sound;
        if (!string.IsNullOrEmpty(subtitle)) data.SoundSubtitle = new LocalizedText(subtitle);
        if (EnvironmentActions.Text(context.Inputs, "filter") is { Length: > 0 } filter) data.WorldEventObjectFilter = filter;
        return Present(data);
    }

    /// <summary>The `forge.action.presentation.audio_stop` command: the loop the author picked, stopped on this
    /// client. The game stops a loop by running the stop event the audio bank pairs with it, and that event goes
    /// through the same `PlaySound` entry the play row uses, so this row carries the stop event's id and nothing
    /// else. It names one loop: other sounds and the global music are untouched.</summary>
    internal CommandResult AudioStop(CommandContext context)
    {
        if (!Audience(context, out var refusal)) return refusal;
        int sound = EnvironmentActions.Integer(context.Inputs, "sound", -1);
        if (sound < 0) return CommandResult.Rejected(SoundRequiredCode);
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);
        var data = EnvironmentActions.Event(eWardenObjectiveEventType.PlaySound);
        data.SoundID = (uint)sound;
        if (EnvironmentActions.Text(context.Inputs, "filter") is { Length: > 0 } filter) data.WorldEventObjectFilter = filter;
        return Present(data);
    }

    /// <summary>The `forge.action.presentation.hud_message` command: the game's warden-intel line. The text is
    /// free — `WardenIntel` is a `LocalizedText` and its string constructor is what carries it — so a plan can
    /// broadcast a door's name or a count it just computed.</summary>
    internal CommandResult Intel(CommandContext context)
    {
        if (!Audience(context, out var refusal)) return refusal;
        string? text = EnvironmentActions.Text(context.Inputs, "text");
        if (string.IsNullOrEmpty(text)) return CommandResult.Rejected(TextRequiredCode);
        if (text.Length > MaximumTextLength) return CommandResult.Rejected(SubtitleCode);
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);
        var data = EnvironmentActions.Event(eWardenObjectiveEventType.None);
        data.WardenIntel = new LocalizedText(text);
        return Present(data);
    }

    /// <summary>The `forge.action.presentation.dialogue` command: the game's own nearest-player line. Which
    /// player is nearest is read on the client that runs the entry, from that client's own position, which is
    /// why this row is presented rather than hosted.</summary>
    internal CommandResult Dialogue(CommandContext context)
    {
        if (!Audience(context, out var refusal)) return refusal;
        int dialogue = EnvironmentActions.Integer(context.Inputs, "dialogue", -1);
        if (dialogue < 0) return CommandResult.Rejected(DialogueRequiredCode);
        if (_events() is null) return CommandResult.Rejected(UnavailableCode);
        var data = EnvironmentActions.Event(eWardenObjectiveEventType.DialogueOnClosest);
        data.DialogueID = (uint)dialogue;
        if (EnvironmentActions.Text(context.Inputs, "filter") is { Length: > 0 } filter) data.WorldEventObjectFilter = filter;
        return Present(data);
    }

    /// <summary>The `forge.action.player.voice` command: a player line through the game's own voice manager. The
    /// speaker is the player entity the plan wired, which is the identity the rest of the graph speaks in; the
    /// manager's own entry takes that player's slot index, so the entity is converted rather than the author being
    /// asked for a number that only this machine could have computed. A speaker this process cannot name, or a
    /// reference of another kind, is refused by name instead of being handed to the manager.</summary>
    internal CommandResult PlayerVoice(CommandContext context)
    {
        if (!Audience(context, out var refusal)) return refusal;
        int voice = EnvironmentActions.Integer(context.Inputs, "voice", -1);
        if (voice < 0) return CommandResult.Rejected(VoiceRequiredCode);
        if (!context.Inputs.TryGetProperty("speaker", out var speakerPort) || speakerPort.ValueKind != JsonValueKind.Object)
            return CommandResult.Rejected(SpeakerCode);
        EntityReference speaker;
        try { speaker = RuntimeJson.Entity(speakerPort); }
        catch (Exception) { return CommandResult.Rejected(SpeakerCode); }
        if (speaker.Id == null || !speaker.Id.StartsWith(PlayerIdentityModule.EntityKind + ":", StringComparison.Ordinal))
            return CommandResult.Rejected(SpeakerCode);
        int slot = PlayerSessions.SlotOf(speaker);
        if (slot < 0) return CommandResult.Rejected(SpeakerCode);
        try
        {
            if (!_playerExists(slot)) return CommandResult.Rejected(SpeakerCode);
            PlayerVoiceManager.WantToSay(slot, (uint)voice);
            return Presented(new List<EnvironmentActions.EnvironmentRow>
            {
                EnvironmentActions.EnvironmentRow.For(null, CommandStatuses.Succeeded, CommitStates.None, "player-voice-issued")
            });
        }
        catch (Exception error)
        {
            Report("map.player-voice-failed: " + error.GetType().Name + ": " + error.Message);
            return CommandResult.Create(CommandStatuses.Failed, CommitStates.None, "player-voice-exception", "", RuntimeJson.EmptyObject);
        }
    }

    /// <summary>The game's own slot predicate: whether a player currently occupies the slot index the entry
    /// takes. The slot came from the speaker's recorded life, and this second check is the session hub's own list,
    /// so a slot this machine's identity half knows and the hub does not is still refused.</summary>
    private static bool PlayerSlotExists(int slot)
    {
        var hub = SNetwork.SNet.SessionHub;
        if (hub == null) return false;
        var players = hub.PlayersInSession;
        if (players == null) return false;
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index];
            if (player != null && player.PlayerSlotIndex() == slot) return true;
        }
        return false;
    }

    /// <summary>One event run locally, and the one row that says it was. A presentation write never commits, so
    /// the row's commit is the tier's own `none`.</summary>
    private CommandResult Present(WardenObjectiveEventData data)
    {
        try
        {
            WorldEventManager.ExecuteEvent(data, 0f);
            return Presented(new List<EnvironmentActions.EnvironmentRow>
            {
                EnvironmentActions.EnvironmentRow.For(null, CommandStatuses.Succeeded, CommitStates.None, "level-event-presented")
            });
        }
        catch (Exception error)
        {
            Report("map.environment-present-failed: " + error.GetType().Name + ": " + error.Message);
            return CommandResult.Create(CommandStatuses.Failed, CommitStates.None, "level-event-exception", "", RuntimeJson.EmptyObject);
        }
    }

    /// <summary>The audience a presentation request declares. The port is a routing list the kernel's own
    /// recipient resolver owns; what this layer checks is that the plan named players at all and that every
    /// member is a player reference, because a step addressed to something else cannot be presented to it.</summary>
    private static bool Audience(CommandContext context, out CommandResult refusal)
    {
        refusal = CommandResult.Rejected(ViewerRequiredCode);
        if (!context.Inputs.TryGetProperty("viewers", out var value) || value.ValueKind != JsonValueKind.Array)
            return false;
        string prefix = PlayerIdentityModule.EntityKind + ":";
        int count = 0;
        foreach (var item in value.EnumerateArray())
        {
            EntityReference reference;
            try { reference = RuntimeJson.Entity(item); }
            catch (Exception) { refusal = CommandResult.Rejected(ViewerCode); return false; }
            if (reference.Id == null || !reference.Id.StartsWith(prefix, StringComparison.Ordinal))
            {
                refusal = CommandResult.Rejected(ViewerCode);
                return false;
            }
            count++;
        }
        return count != 0;
    }

    /// <summary>The presentation aggregate: the tier's result never commits, so a presented row is a succeeded
    /// non-committing result and a refused one stays a refusal.</summary>
    internal static CommandResult Presented(IReadOnlyList<EnvironmentActions.EnvironmentRow> rows)
        => CommandResult.Create(CommandStatuses.Succeeded, CommitStates.None, rows[0].Code, "",
            EnvironmentActions.Envelope(rows));

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception) { /* a reporter that throws must not replace the outcome it reports */ }
    }
}
