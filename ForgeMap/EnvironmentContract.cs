using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>
/// The thirteen checklist rows this batch owns: the twelve whose native half is the game's own level-event system
/// — the nine environment actions (`a-lights`, `a-fog`, `a-fog-repeat`, `a-sound`, `a-sound-stop`, `a-intel`,
/// `a-dialogue`, `a-navmarker`, `a-anim`), the player line (`a-p-say`) and the read-only environment value row
/// (`v-env`) — and the light-colour row (`a-lights-color`), whose effect the game has no entry for at all. Every
/// one of the twelve executes the game's own entry rather than re-implementing its effect: lights, fog, the
/// repeating-fog
/// pair, sound, its paired stop, intel, dialogue, the nav marker and the animation trigger are all
/// `eWardenObjectiveEventType` members
/// of `WardenObjectiveEventData`, and the host hands one built event to `WorldEventManager.ExecuteEvent` /
/// `AttemptTriggerSustainedEventMaster` / `AttemptClearSustainedEventMaster`; a player line is
/// `PlayerVoiceManager.WantToSay`. The two value reads are `EnvironmentStateManager.GetCurrentFogID` and
/// `GetLightMode`. The light-colour row is the exception the checklist's own row asked for: it writes the zone's
/// light objects itself, because no event carries a colour or an intensity and no state replicator would carry
/// one.
///
/// The rows live in the game-independent assembly because the runtime module, the manifest and the website read
/// them there; the handlers are the native assembly's `EnvironmentActions`, `EnvironmentPresentation`,
/// `EnvironmentQuery` and `HudActions`, which answer through these same shapes. A registration only declares
/// what it answers, which is why this file is a declaration and not part of the identity table.
///
/// Three shapes deviate from the website catalog on purpose, and each deviation is one of the catalog's own
/// design-requirement ports that no native field carries. The catalog's presentation rows are written against
/// resource references (`clip`, `volumes`, `icon`), entity collections (`lights`, `subject`) and effect knobs
/// (`volume`, `pitch`, `color`, `intensity`, `range`) that no provider in this runtime resolves and no vanilla
/// field holds; `a-anim` and `a-hud` have no catalog row that carries what their native entry actually takes.
/// The rule this file follows instead is the checklist's own: a port carries only information the game really
/// has, so the ports below are the fields of `WardenObjectiveEventData` and of the two player voice entries,
/// spelled the way an author reads them. Every removed, renamed and added port is listed in this slice's
/// `integration.json` for the website catalog batch to converge on.
/// </summary>
public static class EnvironmentContract
{
    /// <summary>The nine checklist rows that are one `WardenObjectiveEventData` handed to the game's own level
    /// event entry, the player line, and the two read-only environment facts. The canonical ids follow the
    /// checklist's own mapping (`%TEMP%\nlgap\site.tsv`), except where the site recorded "no landing at all":
    /// `a-fog-repeat`, `a-dialogue`, `a-anim`, `a-p-say` and `v-env` are new ids in the existing domains, and
    /// `a-hud` is new because the catalog's one HUD row carries no value, placement, form, colour or audience.
    /// `a-sound-stop` is the same entry as `a-sound` with the stop event the author's loop is paired with, which
    /// is how the game's own audio bank stops a loop; it is a row of its own because "play this" and "stop that"
    /// are two author intents.
    /// `v-env`'s id spells the query tier it runs in (`forge.query.environment.state`), the same way the player
    /// value rows spell theirs.</summary>
    public const string LightingCapability = "forge.action.presentation.lighting";
    /// <summary>`a-lights-color`: the one environment row whose effect the game has no entry for. Lights carry no
    /// level event with a colour or an intensity and no state replicator that would carry one, so the transition
    /// is written by the mod itself on the light objects of the zone the request names.</summary>
    public const string LightColorCapability = "forge.action.presentation.light_color";
    public const string FogCapability = "forge.action.presentation.fog";
    public const string FogCycleCapability = "forge.action.presentation.fog_cycle";
    public const string AudioCapability = "forge.action.presentation.audio_play";
    public const string AudioStopCapability = "forge.action.presentation.audio_stop";
    public const string IntelCapability = "forge.action.presentation.hud_message";
    public const string DialogueCapability = "forge.action.presentation.dialogue";
    public const string NavMarkerCapability = "forge.action.presentation.hud_marker";
    public const string AnimationCapability = "forge.action.presentation.animation_trigger";
    public const string PlayerVoiceCapability = "forge.action.player.voice";
    /// <summary>The `v-env` row. Its capability kind is `condition` — the family that answers a value on demand
    /// and the one the registry registers evaluators for — while its execution tier is `query`, which is the tier
    /// the checklist's own values rows run in and the tier that lets a step read the world through the kernel's
    /// budgeted session. The id says `query` because the plan node an author places is a query step; the two names
    /// are different fields and neither is derived from the other, exactly as the player value rows spell it. Its
    /// outputs are the two facts the checklist names rather than one boolean, which the runtime accepts: a
    /// condition step reads the ports its own shape declares.</summary>
    public const string EnvironmentStateCapability = "forge.query.environment.state";
    /// <summary>`v-zone-lights`: how many native light objects a zone holds and whether that zone's lights are on.
    /// It is the read half of the `a-lights` row above — the game has no runtime entry that switches one light
    /// object, so the write path stays zone-or-expedition plus a boolean and this row answers the data — and it is
    /// a `state` row like the player and enemy value rows, addressed by the level's own zone entity kind.</summary>
    public const string ZoneLightsCapability = "forge.query.map.zone_lights";

    public const string LightingBindingId = ModuleDefinition.ProviderId + ".binding.lighting";
    public const string LightColorBindingId = ModuleDefinition.ProviderId + ".binding.light_color";
    public const string FogBindingId = ModuleDefinition.ProviderId + ".binding.fog";
    public const string FogCycleBindingId = ModuleDefinition.ProviderId + ".binding.fog_cycle";
    public const string AudioBindingId = ModuleDefinition.ProviderId + ".binding.audio_play";
    public const string AudioStopBindingId = ModuleDefinition.ProviderId + ".binding.audio_stop";
    public const string IntelBindingId = ModuleDefinition.ProviderId + ".binding.intel";
    public const string DialogueBindingId = ModuleDefinition.ProviderId + ".binding.dialogue";
    public const string NavMarkerBindingId = ModuleDefinition.ProviderId + ".binding.nav_marker";
    public const string AnimationBindingId = ModuleDefinition.ProviderId + ".binding.animation_trigger";
    public const string PlayerVoiceBindingId = ModuleDefinition.ProviderId + ".binding.player_voice";
    public const string EnvironmentStateBindingId = ModuleDefinition.ProviderId + ".binding.environment_state";
    public const string ZoneLightsBindingId = ModuleDefinition.ProviderId + ".binding.zone_lights";

    public const string LightingHandler = "gtfo.map.lighting";
    public const string LightColorHandler = "gtfo.map.light_color";
    public const string FogHandler = "gtfo.map.fog";
    public const string FogCycleHandler = "gtfo.map.fog_cycle";
    public const string AudioHandler = "gtfo.map.audio_play";
    public const string AudioStopHandler = "gtfo.map.audio_stop";
    public const string IntelHandler = "gtfo.map.intel";
    public const string DialogueHandler = "gtfo.map.dialogue";
    public const string NavMarkerHandler = "gtfo.map.nav_marker";
    public const string AnimationHandler = "gtfo.map.animation_trigger";
    public const string PlayerVoiceHandler = "gtfo.map.player_voice";
    public const string EnvironmentStateHandler = "gtfo.map.environment_state";
    public const string ZoneLightsHandler = "gtfo.map.zone_lights";

    /// <summary>Permissions, spelled the way the catalog's own `recipients.requires` spells the entries these
    /// rows do have. A row's binding carries the permission of the effect it asks the game for, so a plan that
    /// may dim the lights is not thereby allowed to change the fog.</summary>
    public const string LightingPermission = "presentation.light";
    public const string FogPermission = "presentation.fog";
    public const string AudioPermission = "presentation.audio";
    public const string IntelPermission = "presentation.hud";
    public const string DialoguePermission = "presentation.voice";
    public const string NavMarkerPermission = "presentation.marker";
    public const string AnimationPermission = "presentation.animation";
    public const string PlayerVoicePermission = "presentation.voice";
    public const string EnvironmentReadPermission = "environment.read";

    /// <summary>The entity kind the environment rows are addressed by. A zone is the level's own place identity
    /// (`RuntimeZones`), which is exactly what the two zone-addressed vanilla entries take (`Layer` +
    /// `LocalIndex`), so no second addressing model is invented for them.</summary>
    public const string ZoneEntityKind = RuntimeZones.EntityKind;

    /// <summary>`scope` members, in this provider's own order: `zones` runs the zone-addressed entry once per
    /// named zone, `expedition` runs the level-wide entry once. The two are the game's own two spellings for the
    /// same author intent and neither is a default of the other.</summary>
    public static readonly string[] LightingScopes = { "zones", "expedition" };

    /// <summary>`mode` members. `toggle` is the game's own `LightsInZoneToggle` and exists only in zone scope:
    /// there is no level-wide flip in the game's event table, and a request for one is refused by name rather
    /// than served as a read-then-write that would not be atomic.</summary>
    public static readonly string[] LightingModes = { "on", "off", "toggle" };

    /// <summary>`category` members, in the order `LG_Light.LightCategory` declares them: the seven kinds of light
    /// the game's own `LightSettings` blocks are written per, so the member an author picks is the category the
    /// game would have picked. The order is the enum's own, which is what lets the index of a member here be the
    /// value the game indexes a light's `m_category` with. A request that names none drives every category.</summary>
    public static readonly string[] LightCategories =
        { "general", "special", "emergency", "independent", "door", "sign", "door_important" };

    /// <summary>`mode` members of the repeating-fog row: the game's own start/stop pair over one sustained-event
    /// slot. They are one author node because the slot, not the mode, is what the two entries share.</summary>
    public static readonly string[] FogCycleModes = { "start", "stop" };

    /// <summary>`placement` members: the two places the checklist names. `status_bar` writes the game's own
    /// local player shield readout, `teammate_overhead` writes the extra line under a teammate's name marker.
    /// Both are readouts the game already draws; this provider adds no placement of its own.</summary>
    public static readonly string[] HudPlacements = { "status_bar", "teammate_overhead" };

    /// <summary>`form` members: how one value is rendered. `bar` writes the game's own bar without text, so only
    /// the status bar has a sprite to write; the other three render text into whichever readout the placement
    /// owns.</summary>
    public static readonly string[] HudForms = { "number", "number_of_max", "percent", "bar" };

    /// <summary>`audience` members. `team` is the whole routing list one presentation step carries; `self` is the
    /// one player the value belongs to, whose session travels with the command and whose machine is the only one
    /// that draws it. `self` with `teammate_overhead` is refused by name: the local player has no marker of its
    /// own to hang the line on.</summary>
    public static readonly string[] HudAudiences = { "team", "self" };

    /// <summary>The one channel a Warden intel line is shown in. The game has a single warden-intel feed, so the
    /// catalog's `center`/`log`/`banner` members name readouts this entry does not reach.</summary>
    public static readonly string[] IntelChannels = { "intel" };

    /// <summary>Layer members, in the order `LG_LayerType` declares them: the same three the objective rows
    /// index, taken from there rather than respelled here.</summary>
    public static readonly string[] Layers = ObjectiveActionContract.Layers;

    // One shape per handler, resolved once at registration against the capability its binding implements. Every
    // one names exactly the ports and parameters its row declares, so the shape and the row cannot drift.
    public static readonly HandlerShape LightingShape = new HandlerShape()
        .Inputs("zones", "transition", "position", "count").Outputs("result").Parameters("scope", "mode");
    public static readonly HandlerShape LightColorShape = new HandlerShape()
        .Inputs("zones", "color", "brightness", "transition").Outputs("result").Parameters("scope", "category");
    public static readonly HandlerShape FogShape = new HandlerShape()
        .Inputs("zone", "fog", "transition").Outputs("result");
    public static readonly HandlerShape FogCycleShape = new HandlerShape()
        .Inputs("zone", "fog", "transition", "state_duration", "start_delay", "sound").Outputs("result")
        .Parameters("mode", "slot", "states");
    public static readonly HandlerShape AudioShape = new HandlerShape()
        .Inputs("viewers", "sound", "subtitle", "filter").Outputs("result");
    public static readonly HandlerShape AudioStopShape = new HandlerShape()
        .Inputs("viewers", "sound", "filter").Outputs("result");
    public static readonly HandlerShape IntelShape = new HandlerShape()
        .Inputs("viewers", "text").Outputs("result");
    public static readonly HandlerShape DialogueShape = new HandlerShape()
        .Inputs("viewers", "dialogue", "filter").Outputs("result");
    public static readonly HandlerShape NavMarkerShape = new HandlerShape()
        .Inputs("zone", "filter", "enabled").Outputs("result");
    public static readonly HandlerShape AnimationShape = new HandlerShape()
        .Inputs("zone", "filter", "enabled").Outputs("result");
    public static readonly HandlerShape PlayerVoiceShape = new HandlerShape()
        .Inputs("viewers", "speaker", "voice").Outputs("result");
    public static readonly HandlerShape EnvironmentStateShape = new HandlerShape()
        .Outputs("fog", "light").Parameters("dimension", "layer", "zone");
    public static readonly HandlerShape ZoneLightsShape = new HandlerShape()
        .Inputs("zone").Outputs("on", "count");

    /// <summary>The one declaration of each action row, as the shared capability array takes it: the registry
    /// parses this text and validates the row's own graph, and no built-in contract module declares these eleven, so
    /// the Map provider is where they live. The text is built from <see cref="Graphs"/> rather than written twice.</summary>
    public static string LightingCapabilityJson => CapabilityJson(LightingCapability);
    public static string LightColorCapabilityJson => CapabilityJson(LightColorCapability);
    public static string FogCapabilityJson => CapabilityJson(FogCapability);
    public static string FogCycleCapabilityJson => CapabilityJson(FogCycleCapability);
    public static string AudioCapabilityJson => CapabilityJson(AudioCapability);
    public static string AudioStopCapabilityJson => CapabilityJson(AudioStopCapability);
    public static string IntelCapabilityJson => CapabilityJson(IntelCapability);
    public static string DialogueCapabilityJson => CapabilityJson(DialogueCapability);
    public static string NavMarkerCapabilityJson => CapabilityJson(NavMarkerCapability);
    public static string AnimationCapabilityJson => CapabilityJson(AnimationCapability);
    public static string PlayerVoiceCapabilityJson => CapabilityJson(PlayerVoiceCapability);
    public static string EnvironmentStateCapabilityJson => CapabilityJson(EnvironmentStateCapability);
    public static string ZoneLightsCapabilityJson => CapabilityJson(ZoneLightsCapability);

    /// <summary>The thirteen rows in declaration order, for a reader that wants the whole set at once.</summary>
    public static string CapabilitiesJson => "[\n" + string.Join(",\n", CapabilityOrder().ConvertAll(CapabilityJson)) + "\n]";

    private static string CapabilityJson(string capabilityId)
        => RuntimeJson.From(((IReadOnlyDictionary<string, object>)Rows())[capabilityId]).GetRawText();

    private static List<string> CapabilityOrder() => new()
    {
        LightingCapability, LightColorCapability, FogCapability, FogCycleCapability, AudioCapability, AudioStopCapability,
        IntelCapability, DialogueCapability, NavMarkerCapability, AnimationCapability, PlayerVoiceCapability,
        EnvironmentStateCapability, ZoneLightsCapability
    };

    /// <summary>The thirteen execute and observe binding rows, in the order the module declares its own action
    /// bindings. A binding names the canonical capability it implements; the capability row itself is the one
    /// this contract declares above, because no contract module owns it.</summary>
    public static object[] Bindings() => new object[]
    {
        Row(LightingBindingId, LightingCapability, LightingHandler, "execute"),
        Row(LightColorBindingId, LightColorCapability, LightColorHandler, "execute"),
        Row(FogBindingId, FogCapability, FogHandler, "execute"),
        Row(FogCycleBindingId, FogCycleCapability, FogCycleHandler, "execute"),
        Row(AudioBindingId, AudioCapability, AudioHandler, "execute"),
        Row(AudioStopBindingId, AudioStopCapability, AudioStopHandler, "execute"),
        Row(IntelBindingId, IntelCapability, IntelHandler, "execute"),
        Row(DialogueBindingId, DialogueCapability, DialogueHandler, "execute"),
        Row(NavMarkerBindingId, NavMarkerCapability, NavMarkerHandler, "execute"),
        Row(AnimationBindingId, AnimationCapability, AnimationHandler, "execute"),
        Row(PlayerVoiceBindingId, PlayerVoiceCapability, PlayerVoiceHandler, "execute"),
        Row(EnvironmentStateBindingId, EnvironmentStateCapability, EnvironmentStateHandler, "evaluate"),
        Row(ZoneLightsBindingId, ZoneLightsCapability, ZoneLightsHandler, "evaluate")
    };

    /// <summary>The eleven registration support rows, in the same order as <see cref="Bindings"/>: each binding's
    /// own required permission, so the plan's closure has to carry exactly the effects it asks for.</summary>
    public static BindingSupport[] Supports() => new[]
    {
        new BindingSupport(LightingBindingId, "implementation-only", new[] { LightingPermission }),
        new BindingSupport(LightColorBindingId, "implementation-only", new[] { LightingPermission }),
        new BindingSupport(FogBindingId, "implementation-only", new[] { FogPermission }),
        new BindingSupport(FogCycleBindingId, "implementation-only", new[] { FogPermission }),
        new BindingSupport(AudioBindingId, "implementation-only", new[] { AudioPermission }),
        new BindingSupport(AudioStopBindingId, "implementation-only", new[] { AudioPermission }),
        new BindingSupport(IntelBindingId, "implementation-only", new[] { IntelPermission }),
        new BindingSupport(DialogueBindingId, "implementation-only", new[] { DialoguePermission }),
        new BindingSupport(NavMarkerBindingId, "implementation-only", new[] { NavMarkerPermission }),
        new BindingSupport(AnimationBindingId, "implementation-only", new[] { AnimationPermission }),
        new BindingSupport(PlayerVoiceBindingId, "implementation-only", new[] { PlayerVoicePermission }),
        new BindingSupport(EnvironmentStateBindingId, "implementation-only", new[] { EnvironmentReadPermission }),
        new BindingSupport(ZoneLightsBindingId, "implementation-only", new[] { EnvironmentReadPermission })
    };

    /// <summary>The shape of every handler this contract declares, keyed by handler name: command handlers and
    /// the one evaluator alike. The session composes this table with the shapes it already carries.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [LightingHandler] = LightingShape,
        [LightColorHandler] = LightColorShape,
        [FogHandler] = FogShape,
        [FogCycleHandler] = FogCycleShape,
        [AudioHandler] = AudioShape,
        [AudioStopHandler] = AudioStopShape,
        [IntelHandler] = IntelShape,
        [DialogueHandler] = DialogueShape,
        [NavMarkerHandler] = NavMarkerShape,
        [AnimationHandler] = AnimationShape,
        [PlayerVoiceHandler] = PlayerVoiceShape,
        [EnvironmentStateHandler] = EnvironmentStateShape,
        [ZoneLightsHandler] = ZoneLightsShape
    };

    /// <summary>One capability row per canonical id, exactly the row a registration declares. This is the half a
    /// game-independent test compares the handler's own shape against, and the half the website's catalog is
    /// compared with; the binding rows carry no graph of their own.</summary>
    public static IReadOnlyDictionary<string, object> Rows() => new Dictionary<string, object>(StringComparer.Ordinal)
    {
        [LightingCapability] = Lighting(),
        [LightColorCapability] = LightColor(),
        [FogCapability] = Fog(),
        [FogCycleCapability] = FogCycle(),
        [AudioCapability] = Audio(),
        [AudioStopCapability] = AudioStop(),
        [IntelCapability] = Intel(),
        [DialogueCapability] = Dialogue(),
        [NavMarkerCapability] = NavMarker(),
        [AnimationCapability] = Animation(),
        [PlayerVoiceCapability] = PlayerVoice(),
        [EnvironmentStateCapability] = EnvironmentState(),
        [ZoneLightsCapability] = ZoneLights()
    };

    /// <summary>One execute binding row: this provider's own id under its own namespace, the canonical capability
    /// it implements and the handler the native half supplies. No row depends on another binding, so the closure
    /// of a plan that pins one is that row alone.</summary>
    public static object Row(string bindingId, string capabilityId, string handler, string role) => new
    {
        id = bindingId,
        capabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role,
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>One capability row: the canonical id, this provider as its owner, the kind, the label, and the
    /// graph the registry validates and the handler is resolved against.</summary>
    private static object Capability(string capabilityId, string kind, string label, string description, object graph)
        => new
        {
            id = capabilityId,
            owner = ModuleDefinition.ProviderId,
            kind,
            label,
            version = "1.0.0",
            parameters = new { description },
            graph
        };

    /// <summary>`a-lights`: the level-wide and the zone-addressed light entries. `scope=zones` runs the
    /// zone-addressed entry once per named zone and carries the temporary-lighting `transition` and the
    /// sequence's own `position`/`count`; `scope=expedition` runs the level-wide entry once and refuses
    /// `toggle`, which the game's event table does not have. The named zones are the request's recipients, which
    /// the recipient contract requires to be present and non-optional: a level-wide request names the zones it
    /// applies to even though the entry it runs is the level-wide one, and the handler records that difference in
    /// its result rather than hiding it.</summary>
    private static object Lighting() => Capability(LightingCapability, "action", "开关灯光",
        "按游戏自己的灯光条目开、关或翻转一处或全图的灯。", new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                Many("zones", "entity", new[] { "gtfo.zone" }),
                OptionalTicks("transition"),
                Optional("position", "vector3"),
                Optional("count", "integer")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.presentation.lighting") },
            parameters = new object[]
            {
                Enum("scope", LightingScopes, required: true),
                Enum("mode", LightingModes, required: true)
            },
            recipients = new
            {
                input = "zones", target = "entity", cardinality = "many",
                requires = new[] { LightingPermission }, result = "result"
            }
        });

    /// <summary>`a-lights-color`: the colour and intensity transition over a zone's light objects. The scope is
    /// the lighting row's own — `zones` is the places the request names and `expedition` is every zone the level
    /// holds — and the named zones are the request's recipients for the same reason: an action with no target is
    /// not an action. `color` is the three unit-range channels of the game's own colour, `brightness` is a
    /// multiplier of each light's current intensity, `transition` is the ticks the write is spread over and
    /// `category` narrows it to one of the seven kinds of light. `viewers` is deliberately not a port: what this
    /// row writes is the light objects of the process it runs in, and it makes no claim about any other one.</summary>
    private static object LightColor() => Capability(LightColorCapability, "action", "设置区域灯颜色与亮度",
        "把指定区域的灯在若干 tick 内过渡到新的颜色和亮度倍率，可只改某一类灯。", new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                Many("zones", "entity", new[] { "gtfo.zone" }),
                Optional("color", "vector3"),
                Optional("brightness", "number"),
                OptionalTicks("transition")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.presentation.light_color") },
            parameters = new object[]
            {
                Enum("scope", LightingScopes, required: true),
                Enum("category", LightCategories)
            },
            recipients = new
            {
                input = "zones", target = "entity", cardinality = "many",
                requires = new[] { LightingPermission }, result = "result"
            }
        });

    /// <summary>`a-fog`: the game's own fog transition. The fog preset is the data block's persistent id and the
    /// dimension is the one of the zone the request names, which is exactly the two arguments
    /// `AttemptStartFogTransition` takes beside the duration.</summary>
    private static object Fog() => Capability(FogCapability, "action", "切换雾",
        "按雾配置块的 id 和过渡 tick 数切换某个维度当前显示的雾。", new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                Port("zone", "entity"),
                Port("fog", "integer"),
                OptionalTicks("transition")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.presentation.fog") },
            parameters = Array.Empty<object>(),
            recipients = new
            {
                input = "zone", target = "entity", cardinality = "one",
                requires = new[] { FogPermission }, result = "result"
            }
        });

    /// <summary>`a-fog-repeat`: the game's only sustained event, and the only pair of entries that starts and
    /// stops one. `mode=start` fills the slot's own state count, state duration, start delay and fog parameters;
    /// `mode=stop` clears the slot and reads none of them. The slot index is what both modes share, which is why
    /// they are one author node.</summary>
    private static object FogCycle() => Capability(FogCycleCapability, "action", "开始或停止循环雾",
        "按槽位启动一个循环的雾事件，或按槽位停掉它。", new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                Port("zone", "entity", new[] { "gtfo.zone" }),
                Optional("fog", "integer"),
                OptionalTicks("transition"),
                OptionalTicks("state_duration"),
                OptionalTicks("start_delay"),
                Optional("sound", "integer")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.presentation.fog_cycle") },
            parameters = new object[]
            {
                Enum("mode", FogCycleModes, required: true),
                Integer("slot", required: true),
                Integer("states")
            },
            recipients = new
            {
                input = "zone", target = "entity", cardinality = "one",
                requires = new[] { FogPermission }, result = "result"
            }
        });

    /// <summary>`a-sound`: the game's own sound event, played on each addressed client. The subtitle is free
    /// text the plan computes, which is what lets a line name a door or count the generators that are live; the
    /// optional filter names the world event object the sound is placed on.</summary>
    private static object Audio() => Capability(AudioCapability, "action", "播放音效",
        "在每台被寻址的客户端上播放一条游戏音效，可带一行自由文本字幕。",
        PrimitiveGraphSource.Get(AudioCapability));

    /// <summary>`a-sound-stop`: the loop the author picked, stopped on each addressed client. The game's own
    /// entry for a stop is the `PlaySound` member carrying the stop event the audio bank pairs with that loop, so
    /// the row takes that id and addresses the same listeners the play row addresses. It publishes no handle:
    /// once the loop is stopped there is nothing left to name.</summary>
    private static object AudioStop() => Capability(AudioStopCapability, "action", "停止播放中的声音事件",
        "在每台被寻址的客户端上停止一条正在循环的声音：作者选的是那条循环音频，执行的是它在音频库里配对的停止事件。", new
        {
            domains = Domains,
            execution = "presentation",
            inputs = new object[]
            {
                Port("in", "execution"),
                Many("viewers", "entity", new[] { "gtfo.player" }),
                Port("sound", "integer"),
                Optional("filter", "string")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.presentation.audio_stop") },
            parameters = Array.Empty<object>(),
            recipients = new
            {
                input = "viewers", target = "entity", cardinality = "many",
                requires = new[] { AudioPermission }, result = "result"
            }
        });

    /// <summary>`a-intel`: the game's warden-intel line. The entry carries free text, not only a localisation id
    /// — `WardenIntel` is a `LocalizedText` and its own string constructor is what this row writes — so a plan
    /// can broadcast computed text such as a door name or "5 / 7 connected".</summary>
    private static object Intel() => Capability(IntelCapability, "action", "播报情报",
        "向被寻址的玩家播报一行情报文本，文本由计划在运行时计算。",
        PrimitiveGraphSource.Get(IntelCapability));

    private static object Dialogue() => Capability(DialogueCapability, "action", "最近的玩家说一句台词",
        "让离指定世界对象最近的玩家播一条语音，可选指向该对象的过滤器。", new
        {
            domains = Domains,
            execution = "presentation",
            inputs = new object[]
            {
                Port("in", "execution"),
                Many("viewers", "entity", new[] { "gtfo.player" }),
                Port("dialogue", "integer"),
                Optional("filter", "string")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.presentation.dialogue") },
            parameters = Array.Empty<object>(),
            recipients = new
            {
                input = "viewers", target = "entity", cardinality = "many",
                requires = new[] { DialoguePermission }, result = "result"
            }
        });

    /// <summary>`a-navmarker`: the game's own navigation marker, placed on the world event object the filter
    /// names and shown or hidden by the same entry the level data uses. The marker's own appearance belongs to
    /// the object it is placed on, so no icon, label or lifetime port exists here.</summary>
    private static object NavMarker() => Capability(NavMarkerCapability, "action", "显示或隐藏导航标记",
        "在具名世界事件对象上显示或隐藏游戏自己的导航标记。", new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                Port("zone", "entity"),
                Port("filter", "string"),
                Port("enabled", "boolean")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.presentation.hud_marker") },
            parameters = Array.Empty<object>(),
            recipients = new
            {
                input = "zone", target = "entity", cardinality = "one",
                requires = new[] { NavMarkerPermission }, result = "result"
            }
        });

    /// <summary>`a-anim`: the game's scene animation trigger, which plays the animation components and toggles
    /// the objects the named world event object was set up with, and resets them with the same entry. The clip,
    /// layer, blend and speed the catalog's animation row declares do not exist on this entry: what plays is
    /// whatever the level's own object carries.</summary>
    private static object Animation() => Capability(AnimationCapability, "action", "触发场景动画或对象显隐",
        "按名字触发关卡里世界事件对象自带的动画与显隐设置，或用同一条目复位。", new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                Port("zone", "entity", new[] { "gtfo.zone" }),
                Port("filter", "string"),
                Port("enabled", "boolean")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.presentation.animation_trigger") },
            parameters = Array.Empty<object>(),
            recipients = new
            {
                input = "zone", target = "entity", cardinality = "one",
                requires = new[] { AnimationPermission }, result = "result"
            }
        });

    /// <summary>`a-p-say`: a player line, played through the game's own voice manager. The speaker is the player
    /// entity the plan wired — the same identity every other row of this graph speaks in — and the handler converts
    /// it to the slot index the game's own entry takes. The recipients are the audience the line is played for,
    /// which is the presentation tier's own routing, so `viewers` is the request's declared audience and not a
    /// second spelling of the speaker.</summary>
    private static object PlayerVoice() => Capability(PlayerVoiceCapability, "action", "玩家说一句台词",
        "让指定玩家播一条语音事件，说话人由计划里的玩家实体给出。", new
        {
            // This row's own domain list, not the package's: the catalog places a player line in the map, the
            // player column and the logic graph only.
            domains = VoiceDomains,
            execution = "presentation",
            inputs = new object[]
            {
                Port("in", "execution"),
                Many("viewers", "entity", new[] { "gtfo.player" }),
                Port("speaker", "entity", new[] { "gtfo.player" }),
                Port("voice", "integer")
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.player.voice") },
            parameters = Array.Empty<object>(),
            recipients = new
            {
                input = "viewers", target = "entity", cardinality = "many",
                requires = new[] { PlayerVoicePermission }, result = "result"
            }
        });

    /// <summary>`v-env`: the read-only environment row. It answers the state the environment manager holds on
    /// the host: the fog preset the dimension currently shows, and whether the zone's lights are on. It is an
    /// observation, so it declares the world read it performs and neither writes nor publishes.</summary>
    private static object EnvironmentState() => Capability(EnvironmentStateCapability, "state", "读当前环境状态",
        "读出某个维度当前的雾配置块 id，和某个区域的灯是否开着。", new
        {
            domains = Domains,
            execution = "query",
            inputs = Array.Empty<object>(),
            outputs = new object[] { Port("fog", "integer"), Port("light", "boolean") },
            parameters = new object[]
            {
                Integer("dimension", required: true),
                Enum("layer", Layers, required: true),
                Integer("zone", required: true)
            },
            reads = new[] { "world" }
        });

    /// <summary>`v-zone-lights`: how many native light objects the named zone holds and whether that zone's lights
    /// are on. `count` is the length of `LG_Zone.m_lightsInZone` — the zone's own light list, which is the one
    /// place the level records how many lights a zone has — and `on` is `EnvironmentStateManager.GetLightMode` for
    /// the same zone. The zone is addressed by the level's own `gtfo.zone` reference, which is the address the
    /// `zone` resource kind and the `a-lights` recipient already speak, so no second addressing model is invented
    /// for the read.</summary>
    private static object ZoneLights() => Capability(ZoneLightsCapability, "state", "区域内灯光数量与开关",
        "读一个区域里有多少盏灯，以及这个区域的灯是不是开着的。", new
        {
            domains = Domains,
            execution = "query",
            inputs = new object[] { Port("zone", "entity", new[] { "gtfo.zone" }) },
            outputs = new object[] { Port("on", "boolean"), Port("count", "integer") },
            parameters = Array.Empty<object>(),
            reads = new[] { "world" }
        });

    /// <summary>The catalog's domain list for the presentation rows, unchanged.</summary>
    private static readonly string[] Domains =
        { "map", "room", "enemy", "weapon", "tool", "consumable", "player" };

    /// <summary>The player voice line's own domain list: the catalog places that row in the map, the player
    /// column and the logic graph, and the other presentation rows in the wider set above.</summary>
    private static readonly string[] VoiceDomains = { "map", "player", "logic" };

    /// <summary>The result row the catalog declares for an action: the four shared columns in their own order,
    /// and nothing this provider cannot answer.</summary>
    private static object Result(string schema) => new
    {
        id = "result", type = "result", schema,
        fields = new object[]
        {
            new { id = "target", type = "entity" },
            new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" },
            new { id = "code", type = "string" }
        }
    };

    private static object Port(string id, string type) => new { id, type };
    private static object Optional(string id, string type) => new { id, type, optional = true };
    /// <summary>An optional port carrying the runtime's own tick unit: every time an author writes on a row is
    /// ticks, and the handler converts to the seconds the native field takes.</summary>
    private static object OptionalTicks(string id) => new { id, type = "number", unit = "tick", optional = true };
    private static object Many(string id, string type) => new { id, type, cardinality = "many" };
    private static object Port(string id, string type, string[] entityKinds) => new { id, type, entityKinds };
    private static object Many(string id, string type, string[] entityKinds) => new { id, type, cardinality = "many", entityKinds };

    /// <summary>A structural enum that inlines its own members: the runtime accepts a structural enum with no
    /// shared set, which is what these vocabularies are — no shared set spells them.</summary>
    private static object Enum(string id, string[] values, bool required = false)
        => new { id, type = "enum", role = "structural", required, values };

    private static object Integer(string id, bool required = false)
        => new { id, type = "integer", role = "structural", required };
}
