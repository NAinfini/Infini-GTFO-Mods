using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

/// <summary>
/// Forge Standard v0.2 graph metadata: validation, variable port expansion and the dense
/// slot layout a compiled plan carries. It neither binds nor executes nodes. Every table
/// mirrors site/forge/contracts.ts; array order is part of the wire because compiled
/// layouts store indices into it.
/// </summary>
internal static class RuntimeGraphContracts
{
    internal const int MaximumVariadicPorts = 32;
    /// <summary>Mirrors site/forge/graph-schema.ts DOMAIN_REASON_CODE_MAX_LENGTH / DOMAIN_REASON_CODE_PATTERN.</summary>
    internal const int DomainReasonCodeMaxLength = 64;
    private static readonly Regex DomainReasonCodePattern = new(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    internal static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum",
        "vector3", "entity", "resource", "handle", "event", "result", "policy" };
    internal static readonly string[] Cardinalities = { "one", "many" };
    /// <summary>I-CATALOG execution tiers. `pure` is zero world access, `query` reads the world on demand through
    /// the kernel's budgeted query interface without producing commands or writing the world; the other three are
    /// side-effecting tiers whose handler runs on the owning authority.</summary>
    internal static readonly string[] ExecutionTiers = { "pure", "query", "host", "owner", "presentation" };
    /// <summary>I-PLAN `attachments[].kind`, in the ordinal order the plan file must sort by. Every one of them is
    /// matched by the provider that registered that kind's matcher; the plan vocabulary itself says nothing about
    /// which of them name an event subject.</summary>
    internal static readonly string[] AttachmentKinds = { "level", "map-object", "gear-block", "enemy-type" };
    /// <summary>The four columns every result schema starts with, in this order and with these exact types.
    /// `status` uses a subset of execution_outcome; `committed` uses the commit_state set.</summary>
    internal static readonly string[] FixedResultFields = { "target", "status", "committed", "code" };
    private static readonly string[] ResultFieldTypes = { "boolean", "integer", "number", "string", "vector3", "entity", "enum" };
    internal const int MaximumResultFields = 32;
    /// <summary>Appended, never inserted: `room` is still spelled by catalog rows that a later batch reshapes to
    /// `zone`, and a member may not be dropped while a row still names it. `generator` is the level-object
    /// family's own kind: a power generator is an instance the level places, so the value row that reads one
    /// (`forge.condition.predicate.power`) addresses it as a resource and the Map provider is its owner.</summary>
    internal static readonly string[] ResourceKinds = { "map", "room", "enemy", "weapon", "tool", "consumable", "item",
        "objective", "encounter", "wave", "spawn_pool", "behavior_graph", "ability", "path", "area_field", "animation",
        "audio", "effect", "material", "model", "profile", "pool", "chained-puzzle", "zone", "generator" };
    /// <summary>Appended, never inserted: a member is removed in the same batch that reshapes every row still
    /// spelling it, so the catalog never names a kind this table does not carry. `reservation`, `lease` and
    /// `transaction` were removed that way — none of them is a native object this runtime can hand out, so every
    /// row that spelled one now names a real recipient or drops the port (ruling 160.3).</summary>
    internal static readonly string[] HandleKinds = { "timer", "subscription", "status", "effect", "audio", "animation",
        "deployment", "cooldown", "charge", "pool_membership", "pool", "request" };
    internal static readonly string[] HandleLifetimes = { "invocation", "resource_instance", "entity_life", "encounter",
        "expedition", "session" };
    /// <summary>The entity kinds a port may name, in the order the runtime observes them. An entity port that
    /// declares none accepts every kind, which is what keeps the generic ports — a selector's target, a
    /// comparison's operand, a `for_each` item — wired to every source; a declaring port accepts only the kinds
    /// its own list carries. Mirrors site/forge/contracts.ts graphEntityKinds, which is the one source of the
    /// order. The spelling is `RuntimeEntitySnapshot.Kind`'s own, so the kind a port promises is the kind the
    /// snapshot publishes and neither side needs a translation table.</summary>
    internal static readonly string[] EntityKinds = { "gtfo.player", "gtfo.enemy", "gtfo.map_object",
        "gtfo.level_object", "gtfo.level", "gtfo.zone", "gtfo.equipment" };
    /// <summary>Declaration order is the compiled valueSet index of an enum port. The website's
    /// `graphEnumSets` is the one source of that order; this table declares the same sets in the same
    /// order, and the website's enum test reads this source back to prove it.</summary>
    private static readonly (string Name, string[] Members)[] EnumSetTable = {
        ("compare_operator", new[] { "eq", "ne", "lt", "lte", "gt", "gte" }),
        ("boundary_mode", new[] { "inclusive", "exclusive" }),
        ("rounding_mode", new[] { "floor", "ceil", "nearest", "truncate" }),
        ("command_phase", new[] { "requested", "accepted", "committed", "rejected", "cancelled" }),
        ("execution_outcome", new[] { "succeeded", "partial", "rejected", "failed", "cancelled", "expired" }),
        ("interaction_phase", new[] { "requested", "started", "completed", "cancelled", "failed" }),
        ("damage_kind", new[] { "direct", "melee", "explosion", "dot", "shrapnel", "collision", "fall", "environment", "reflection" }),
        ("ai_state", new[] { "sleeping", "waking", "idle", "investigating", "alerted", "pursuing", "attacking", "recovering", "disabled", "dead",
            "patrolling", "hibernating" }),
        ("status_kind", new[] { "slow", "haste", "root", "stun", "paralysis", "freeze", "foam", "blind", "deaf", "silence", "disarm",
            "weaken", "vulnerability", "burn", "bleed", "poison", "corrosion", "infection", "regeneration", "shield", "cloak", "taunt",
            "fear", "reveal", "mark" }),
        ("stack_policy", new[] { "refresh", "extend", "add", "replace", "strongest", "independent-per-source" }),
        ("query_shape", new[] { "room", "area", "zone", "sphere", "cone", "box", "cylinder", "capsule", "path" }),
        ("empty_policy", new[] { "emit-empty", "skip", "fail" }),
        ("equipment_action", new[] { "primary", "secondary", "reload", "interact", "recall", "alternate", "custom-binding" }),
        ("lifetime_scope", HandleLifetimes),
        ("variable_value_type", new[] { "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle" }),
        ("recipient_sort", new[] { "stable-id", "nearest", "farthest" }),
        // The anchor enum and the delivered context are the same five roles, so the table is read rather than
        // restated: one place spells the role names, the other spells the payload ports they are read from.
        ("recipient_anchor", RuntimeActorRoles.Roles.ToArray()),
        ("recipient_relation", new[] { "self", "ally", "hostile", "neutral", "unknown" }),
        ("recipient_life_state", new[] { "alive", "downed", "dead" }),
        ("value_operation", new[] { "set", "add", "subtract", "multiply", "minimum", "maximum" }),
        ("coordinate_space", new[] { "world", "local", "view" }),
        ("pulse_start", new[] { "immediate", "after_interval" }),
        ("commit_state", new[] { "none", "confirmed", "unknown", "partial" }),
        // Native `AgentModifier`: every member name lower-cased, an underscore or a case boundary becoming a hyphen.
        ("agent_modifier", new[] { "none", "regeneration-cap", "regeneration-speed", "heal-support", "revive-speed-support",
            "revive-start-health-support", "melee-resistance", "projectile-resistance", "infection-resistance", "damage-over-time",
            "nanoswarm-shield", "nanoswarm-weakness", "explosion-resistance", "pistol-damage", "smg-damage", "dmr-damage",
            "assault-rifle-damage", "carbine-damage", "auto-pistol-damage", "hel-damage", "shotgun-damage", "revolver-damage",
            "sniper-damage", "burst-cannon-damage", "machine-gun-damage", "machine-pistol-damage", "rifle-damage",
            "burst-rifle-damage", "double-tap-rifle", "bullpup-rifle-damage", "combat-shotgun-damage", "choke-mod-shotgun-damage",
            "standard-weapon-damage", "special-weapon-damage", "glue-strength", "glue-efficiency", "sentry-gun-speed",
            "sentry-gun-damage", "sentry-gun-long-range-damage", "sentry-gun-short-range-damage", "trip-mine-damage",
            "scanner-recharge-speed", "ammo-support", "hacking-proficiency", "computer-processing-speed", "initial-ammo-standard",
            "initial-ammo-special", "initial-ammo-tool", "fog-repeller-effect", "glowstick-effect", "bioscan-speed",
            "melee-damage", "movement-speed", "movement-acceleration" }),
        // The five tiers a level instance is placed in, plus the third layer's own name. `A`..`E` are `eRundownTier`
        // 1..5; the sixth member is the layer spelling, not the native enum's `Surface` member.
        ("rundown_tier", new[] { "A", "B", "C", "D", "E", "overload" }),
        // Native `eDoorStatus` as the door reader already publishes it: lower-cased member names.
        ("door_state", new[] { "none", "closed", "closed_broken_cant_open", "closed_locked_with_key_item",
            "closed_locked_with_chained_puzzle_alarm", "closed_locked_with_chained_puzzle", "closed_locked_with_power_generator",
            "closed_locked_with_no_key", "chained_puzzle_activated", "unlocked", "open", "destroyed", "glued_max",
            "try_open_stuck_in_glue", "try_open_stuck_broken", "closed_locked_with_bulkhead_dc", "opening" }),
        // The author's five-state question about a door, derived from the native status (`Unlocked` is an
        // unlocked but still shut door, so it reads as closed). The exact native value stays readable as
        // `door_state`, which is the reader's own spelling of `eDoorStatus`.
        ("door_query_state", new[] { "closed", "open", "locked", "needs_scan", "broken" }),
        // The two phases a weak door reports: the hit that did not break it yet, and the break. A door's
        // identity is the map object's identity, so it travels on the same rows a door action carries rather
        // than on a namespace of its own.
        ("door_phase", new[] { "attacked", "broken" }),
        // The shells a deployed-device or tool fact can name: the two devices the equipment domain places, plus
        // the hand-held launcher whose shot is the same kind of fact. Members are added with the facts that
        // publish them, so every member here is one some hook actually reads.
        ("equipment_kind", new[] { "sentry_gun", "mine", "glue_gun" }),
        // The two player-event vocabularies: the supplies a player can apply, and every pickup the game posts
        // (a commodity's three sizes kept apart because the game keeps them apart). Declaration order is the
        // index a published fact carries.
        ("supply_kind", new[] { "medikit", "ammokit", "disinfection", "tool_refill" }),
        ("pickup_kind", new[] { "medikit", "ammokit", "tool_refill", "artifact", "commodity_small", "commodity_medium",
            "commodity_large", "consumable", "keycard" }),
        // The two level-object readings that are a closed enum rather than a value: a scan's own state and a
        // container's own status, both lower-cased native member names.
        ("scan_state", new[] { "disabled", "active", "solved", "timed-out" }),
        ("container_state", new[] { "not-setup", "locked", "closed", "open", "player-close", "player-far" }),
        // The weak lock's own cause of opening. A door's identity is the map object's identity, so it travels
        // on the same rows a lock action carries rather than on a namespace of its own.
        ("door_lock_cause", new[] { "unlocked", "hacked", "smashed" }),
    };
    internal static readonly IReadOnlyDictionary<string, string[]> EnumSets =
        EnumSetTable.ToDictionary(x => x.Name, x => x.Members, StringComparer.Ordinal);
    /// <summary>The set names in declaration order: the members a structural `enum_set` parameter chooses from, so a
    /// row that covers every shared set — `forge.condition.enum.compare`, `forge.control.flow.enum_switch` — reads
    /// this list instead of restating it. Published through <see cref="RuntimeEnumSets"/>.</summary>
    internal static readonly string[] EnumSetNames = EnumSetTable.Select(set => set.Name).ToArray();
    /// <summary>Whether a name is a member of one of the shared sets, asked by name so a caller that reads a
    /// member from outside a port (an observation field) can check it without a port to index the set through.</summary>
    internal static bool IsEnumMember(string set, string? member)
        => member != null && EnumSets.TryGetValue(set, out var members) && Array.IndexOf(members, member) >= 0;
    /// <summary>Q3: an enum's runtime/wire value is a member-set index, never its name. A structural parameter's
    /// inline `values` narrows the index basis to that list; everything else (promoted parameters, ports) indexes
    /// the full named set.</summary>
    internal static string[] EnumMembers(JsonElement enumDefinition) => enumDefinition.TryGetProperty("values", out var inline)
        ? RuntimeJson.Strings(inline) : EnumSets[RuntimeJson.Text(enumDefinition, enumDefinition.TryGetProperty("set", out _) ? "set" : "schema")];
    internal static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "session", "logic", "editor" };
    /// <summary>A structural parameter's own type set. `resource` is a member because a resource structural
    /// parameter is a compile-time constant reference — the author selects which authored puzzle, wave or group
    /// the row subscribes to, and nothing about that reference is read at evaluation time — which is exactly
    /// what a value parameter may carry, so it is declared as one and not given a second parameter class.
    /// `object-address` is the same kind of constant for a different thing: the map object an entrypoint's trigger
    /// is attached to (ruling 143.11), written as the canonical address the provider's own address record parses.
    /// It is a parameter type and never a port type: an address is authored once and read once at load, so there
    /// is no wire frame that could carry it and nothing to promote it to.</summary>
    private static readonly string[] ParameterTypes = { "boolean", "integer", "number", "string", "enum", "vector3",
        "resource", "recipient-policy", "object-address" };
    private static readonly string[] RecipientTargets = { "entity", "resource", "handle" };
    /// <summary>Value types with an implemented runtime validator, in the order <see cref="ValueKind"/> mirrors:
    /// entry <c>i</c> is <c>ValueKind</c> <c>i + 2</c>, which is asserted at every plan load. Appending a type is
    /// appending a kind, and only there. `policy` is the one wire port type with no frame form yet.</summary>
    internal static readonly string[] RuntimeValueTypes = { "boolean", "integer", "number", "string", "vector3", "entity",
        "enum", "handle", "resource", "event" };
    /// <summary>The closed list a `valueTypeParameter` member may name: the value classes a port can be typed as,
    /// in the order that types the resolved port. `execution` is control flow, `event` a dispatch identity and
    /// `result` a row identity, so none of them is a value class; `policy` is a value the website may declare but
    /// this runtime has no frame form for, so it is not one here either.
    /// Mirrors site/forge/graph-schema.ts valuePortTypes.</summary>
    internal static readonly string[] ValueTypeParameterTypes = { "boolean", "integer", "number", "string", "enum",
        "vector3", "entity", "resource", "handle" };
    /// <summary>The handle kinds a structural `value_type` member may name, and the lifetime each resolves to.
    /// A handle's contract is two words — its kind and its lifetime (rule 142.1) — so the member that types a port
    /// as a handle spells both halves, `handle:<kind>`, and the second half comes from here. The members are the
    /// kinds a value may be missing in, which is what a when-present step guards (rule 142.3, ruling 158.4).
    /// Mirrors site/forge/contracts.ts graphGuardedHandleLifetimes in the same order, which is the compiled order
    /// of the parameter's own member list.</summary>
    internal static readonly IReadOnlyDictionary<string, string> GuardedHandleLifetimes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["timer"] = "encounter", ["subscription"] = "encounter", ["effect"] = "encounter"
        };
    /// <summary>The handle kind a structural member names, or null for a member that is a plain value class. A
    /// member outside both lists is not one this vocabulary offers.</summary>
    internal static string? GuardedHandleKind(string member)
    {
        if (!member.StartsWith("handle:", StringComparison.Ordinal)) return null;
        var kind = member.Substring("handle:".Length);
        return GuardedHandleLifetimes.ContainsKey(kind) && HandleKinds.Contains(kind) ? kind : null;
    }
    /// <summary>Every member a `value_type` parameter may offer: the value classes, plus the guardable handle
    /// kinds under their two-word spelling.</summary>
    internal static bool IsValueTypeMember(string member)
        => ValueTypeParameterTypes.Contains(member) || GuardedHandleKind(member) != null;
    /// <summary>The closed list of read sources a capability contract may declare: `world` is live game state,
    /// `kernel` a runtime service and `telemetry` the recorded diagnostics stream. Mirrors
    /// site/forge/contracts.ts graphReadSources.</summary>
    internal static readonly string[] ReadSources = { "world", "kernel", "telemetry" };

    /// <summary>One port's physical form inside a frame: a kind it never occupies has width 0 and no slot.
    /// "execution" carries no value. A "many" port is one head slot plus <see cref="RuntimeFrames.MaxSetWidth"/>
    /// elements at the element type's own width. A resource is its kind index in the head slot and its id in the
    /// next one; an event is the row index of the dispatch's own event; a result row is the sum of its declared
    /// fields and has no head slot of its own.</summary>
    internal static (ValueKind Kind, int Width) FramePort(JsonElement port)
    {
        var type = RuntimeJson.Text(port, "type");
        if (type == "execution") return (ValueKind.Missing, 0);
        if (type == "result")
        {
            // A row is one row: it is already a region of its own declared fields, and there is no such thing as a
            // collection of rows a port could name.
            RuntimeJson.Require(!Many(port), "unsupported-port", RuntimeJson.Text(port, "id") + " is declared as a collection of result");
            return (ValueKind.Result, ResultRowWidth(port));
        }
        var (kind, width) = type switch {
            "handle" => (ValueKind.Handle, 1),
            "resource" => (ValueKind.Resource, RuntimeFrames.ResourceWidth),
            "event" => (ValueKind.Event, 1),
            "policy" => (ValueKind.Missing, 0),
            _ => RuntimeValueTypes.Contains(type) ? (RuntimeFrames.KindOf(type), RuntimeFrames.ValueWidth(type)) : (ValueKind.Missing, 0)
        };
        if (!Many(port)) return (kind, width);
        // Only a kind with an element slot has a collection form. The reference and row kinds are refused here
        // rather than reserved a width nothing could fill; the plan loader refuses them one step earlier, where
        // it knows which side of the boundary the port sits on.
        RuntimeJson.Require(RuntimeFrames.Segmented(kind),
            "unsupported-port", RuntimeJson.Text(port, "id") + " is declared as a collection of " + type);
        return (kind, RuntimeFrames.SegmentWidth(kind));
    }

    /// <summary>The slot width of one result row: its declared fields at their own widths, in declaration order.
    /// There is no head slot, so the four fixed columns (entity, enum, enum, string) make the row at least four
    /// slots wide and a later vector3 column widens it by three.</summary>
    internal static int ResultRowWidth(JsonElement port)
    {
        var width = 0;
        foreach (var field in ResultFields(port)) width += RuntimeFrames.ValueWidth(RuntimeJson.Text(field, "type"));
        return width;
    }
    /// <summary>The slot offset of every declared field inside its row, so a reader addresses a column the way a
    /// step addresses a port: by an offset the load-time descriptor already knows.</summary>
    internal static int[] ResultFieldOffsets(JsonElement port)
    {
        var fields = ResultFields(port); var offsets = new int[fields.Length]; var offset = 0;
        for (var index = 0; index < fields.Length; index++)
        { offsets[index] = offset; offset += RuntimeFrames.ValueWidth(RuntimeJson.Text(fields[index], "type")); }
        return offsets;
    }
    private static JsonElement[] ResultFields(JsonElement port)
    {
        RuntimeJson.Require(port.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array,
            RuntimeAbiCodes.ResultSchema, RuntimeJson.Text(port, "id"));
        return fields.EnumerateArray().ToArray();
    }
    /// <summary>True for the result-row port of a capability: the row frame's own port, never a value in a step frame.</summary>
    internal static bool IsResult(JsonElement port) => RuntimeJson.Text(port, "type") == "result";

    private static bool IsName(string value) => Regex.IsMatch(value, @"^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
    private static string? Optional(JsonElement value, string key) => value.TryGetProperty(key, out var field) ? field.GetString() : null;
    internal static string Cardinality(JsonElement port) => Optional(port, "cardinality") ?? "one";
    internal static bool Many(JsonElement port) => Cardinality(port) == "many";
    /// <summary>Full value contract equality, used by repeated ports and by plan input wiring.</summary>
    internal static bool SameValue(JsonElement a, JsonElement b)
        => ValueTypeMatches(a, b) && Cardinality(a) == Cardinality(b);
    /// <summary>Value contract equality ignoring cardinality: a wired plan input may pair a
    /// non-nullable "one" output with a "many" input, wrapped into a one-element collection at dispatch.
    /// Every other dimension must still match exactly — except entity kinds, which narrow: see
    /// <see cref="EntityKindsNarrow"/>.</summary>
    internal static bool ValueTypeMatches(JsonElement a, JsonElement b)
        => RuntimeJson.Text(a, "type") == RuntimeJson.Text(b, "type")
           && new[] { "schema", "unit", "resourceKind", "handleKind", "lifetime" }.All(key => Optional(a, key) == Optional(b, key))
           && EntityKindsNarrow(a, b);
    /// <summary>The kinds one port declares, or null for a port that declares none. A declaration is the one
    /// place kinds come from: nothing is derived from the value a wire happens to carry at run time.</summary>
    private static string[]? DeclaredEntityKinds(JsonElement port)
        => port.TryGetProperty("entityKinds", out var kinds) && kinds.ValueKind == JsonValueKind.Array
            ? kinds.EnumerateArray().Select(kind => kind.GetString() ?? string.Empty).ToArray() : null;
    /// <summary>Kind compatibility between a producer (`output`) and a consumer (`input`): every kind the
    /// output may carry has to be one the input accepts, so a narrow port wires into a wide one and never the
    /// other way. An input that declares none accepts them all, which is the one direction that makes the
    /// generic ports universal. Mirrors site/forge/graph-schema.ts assignableGraphPort.</summary>
    internal static bool EntityKindsNarrow(JsonElement output, JsonElement input)
    {
        var carried = DeclaredEntityKinds(output);
        if (carried == null) return true;
        var accepted = DeclaredEntityKinds(input);
        return accepted == null || carried.All(kind => accepted.Contains(kind, StringComparer.Ordinal));
    }

    internal static void ValidatePort(JsonElement port, string id)
    {
        RuntimeJson.Shape(port, "id type", "cardinality schema resourceKind handleKind lifetime unit nullable optional codes fields valueTypeParameter schemaParameter entityKinds");
        RuntimeJson.Require(IsName(RuntimeJson.Text(port, "id")), "port-name", id);
        var type = RuntimeJson.Text(port, "type");
        RuntimeJson.Require(PortTypes.Contains(type), "port-type", id);
        foreach (var flag in new[] { "nullable", "optional" })
            if (port.TryGetProperty(flag, out var value))
                RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "port-flag", id);
        foreach (var key in new[] { "cardinality", "schema", "resourceKind", "handleKind", "lifetime", "unit" })
            if (port.TryGetProperty(key, out var text)) RuntimeJson.Text(text);
        if (port.TryGetProperty("cardinality", out _)) RuntimeJson.Require(Cardinalities.Contains(Cardinality(port)), "port-cardinality", id);
        // Kinds belong to an entity port alone: any other port promising them would be naming a kind it cannot
        // deliver. An empty or repeating list is the same authoring mistake, and a member outside the table is
        // not a kind this runtime observes.
        if (port.TryGetProperty("entityKinds", out var entityKinds))
        {
            RuntimeJson.Require(type == "entity" && entityKinds.ValueKind == JsonValueKind.Array, "port-entity-kinds", id);
            var kinds = entityKinds.EnumerateArray().Select(kind => kind.ValueKind == JsonValueKind.String ? kind.GetString() : null).ToArray();
            RuntimeJson.Require(kinds.Length > 0 && kinds.All(kind => kind != null && EntityKinds.Contains(kind)), "port-entity-kinds", id);
            RuntimeJson.Require(kinds.Distinct(StringComparer.Ordinal).Count() == kinds.Length, "port-entity-kinds", id);
        }
        // A typed port declares only its class and defers its concrete type to the structural member an author
        // chooses: the member's own rules are applied where it resolves (`TypedPort`), because the declaration
        // cannot know which of them will be the port's. The class itself is still refused here when no member
        // could ever be one.
        // A schema parameter is the same deferral for the port's identity: the enum set arrives from the member the
        // plan compiles, so the declaration carries the parameter and never a set of its own beside it.
        var deferredSchema = port.TryGetProperty("schemaParameter", out var schemaParameter);
        if (deferredSchema)
            RuntimeJson.Require(IsName(RuntimeJson.Text(schemaParameter)) && !port.TryGetProperty("schema", out _), "port-type", id);
        if (port.TryGetProperty("valueTypeParameter", out var parameter))
        {
            RuntimeJson.Require(IsName(RuntimeJson.Text(parameter)) && ValueTypeParameterTypes.Contains(type),
                "port-type", id);
            return;
        }
        if (deferredSchema) return;
        RequirePortTypeContract(port, type, id);
    }

    /// <summary>The rules a port's own type carries, in one body because a port and the port a typed declaration
    /// resolves to are the same port: the schema its carrier needs, the kind a resource or a handle names, the unit
    /// only a numeric carries, the codes of a result row. Mirrors site/forge/graph-schema.ts requirePortTypeContract.</summary>
    private static void RequirePortTypeContract(JsonElement port, string type, string id)
    {
        var schema = Optional(port, "schema");
        if (type is "event" or "result" or "resource" or "policy") RuntimeJson.Require(schema != null, "port-schema", id);
        if (type == "enum") RuntimeJson.Require(schema != null && EnumSets.ContainsKey(schema), "port-enum-set", id);
        var resourceKind = Optional(port, "resourceKind");
        RuntimeJson.Require(type == "resource" ? resourceKind != null && ResourceKinds.Contains(resourceKind) : resourceKind == null, "port-resource-kind", id);
        var handleKind = Optional(port, "handleKind"); var lifetime = Optional(port, "lifetime");
        RuntimeJson.Require(type == "handle"
            ? handleKind != null && HandleKinds.Contains(handleKind) && lifetime != null && HandleLifetimes.Contains(lifetime)
            : handleKind == null && lifetime == null, "port-handle-kind", id);
        if (type == "execution")
            RuntimeJson.Require(!port.TryGetProperty("unit", out _) && schema == null && !RuntimeJson.Flag(port, "nullable")
                && !port.TryGetProperty("cardinality", out _), "execution-port", id);
        if (port.TryGetProperty("unit", out _)) RuntimeJson.Require(type is "number" or "integer" or "vector3", "port-unit", id);
        if (port.TryGetProperty("codes", out var codes))
        {
            RuntimeJson.Require(type == "result", "port-codes", id);
            RuntimeJson.Require(codes.ValueKind == JsonValueKind.Array, "port-codes", id);
            var items = codes.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null).ToArray();
            RuntimeJson.Require(items.All(c => c != null && c.Length <= DomainReasonCodeMaxLength && DomainReasonCodePattern.IsMatch(c)), "port-codes", id);
            RuntimeJson.Require(items.Distinct(StringComparer.Ordinal).Count() == items.Length, "port-codes", id);
        }
        if (type == "result") ValidateResultFields(port, id);
    }

    /// <summary>The row shape of one result schema. Rows are declared here, on the result port, and never in a
    /// plan: the row is a property of the schema (several actions reuse one schema), the four shared columns come
    /// first in a fixed order, and every later column is a value the row may carry. Only the shape is checked
    /// here — nothing in this task writes a row.</summary>
    private static void ValidateResultFields(JsonElement port, string id)
    {
        RuntimeJson.Require(port.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array, RuntimeAbiCodes.ResultSchema, id);
        var rows = fields.EnumerateArray().ToArray();
        RuntimeJson.Require(rows.Length is >= 4 and <= MaximumResultFields, RuntimeAbiCodes.ResultSchema, id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in rows)
        {
            RuntimeJson.Shape(field, "id type", "unit schema nullable");
            var name = RuntimeJson.Text(field, "id");
            RuntimeJson.Require(IsName(name) && seen.Add(name), RuntimeAbiCodes.ResultSchema, id + "." + name);
            var type = RuntimeJson.Text(field, "type");
            RuntimeJson.Require(ResultFieldTypes.Contains(type), RuntimeAbiCodes.ResultSchema, id + "." + name);
            if (field.TryGetProperty("nullable", out var nullable))
                RuntimeJson.Require(nullable.ValueKind is JsonValueKind.True or JsonValueKind.False, RuntimeAbiCodes.ResultSchema, id + "." + name);
            if (field.TryGetProperty("unit", out var unit))
            {
                RuntimeJson.Text(unit);
                RuntimeJson.Require(type is "number" or "integer" or "vector3", RuntimeAbiCodes.ResultSchema, id + "." + name);
            }
            var schema = Optional(field, "schema");
            RuntimeJson.Require(type == "enum" ? schema != null && EnumSets.ContainsKey(schema) : schema == null, RuntimeAbiCodes.ResultSchema, id + "." + name);
        }
        // target | status | committed | code: fixed ids, fixed types, fixed sets, never nullable, always first.
        var first = rows.Take(FixedResultFields.Length).Select(f => RuntimeJson.Text(f, "id")).ToArray();
        RuntimeJson.Require(first.SequenceEqual(FixedResultFields), RuntimeAbiCodes.ResultSchema, id);
        RequireField(rows[0], "entity", null, id); RequireField(rows[1], "enum", "execution_outcome", id);
        RequireField(rows[2], "enum", "commit_state", id); RequireField(rows[3], "string", null, id);
    }
    private static void RequireField(JsonElement field, string type, string? schema, string id)
        => RuntimeJson.Require(RuntimeJson.Text(field, "type") == type && Optional(field, "schema") == schema && !RuntimeJson.Flag(field, "nullable"),
            RuntimeAbiCodes.ResultSchema, id + "." + RuntimeJson.Text(field, "id"));

    /// <summary>The name of the structural enum parameter that types this port, or null on a port that carries its
    /// own type. A typed port is resolved per node instance, so the concrete type is known only once a plan names a
    /// member — and is then the contract's own type, not the class this port declares.</summary>
    internal static string? ValueTypeParameter(JsonElement port)
        => Optional(port, "valueTypeParameter");

    /// <summary>The structural enum a typed port defers to, checked against the member list the port could ever
    /// resolve to: a missing, promotable or non-enum parameter is refused, and so is a member that is not a value
    /// class, because the row would otherwise promise a type the contract cannot accept. A `pure` capability has no
    /// world port in its frame at all, so no member it offers may be one either. Mirrors
    /// site/forge/graph-schema.ts portTypeParameter.</summary>
    private static void ValidateTypeParameter(JsonElement port, JsonElement[] parameters, string execution, string id)
    {
        var name = ValueTypeParameter(port);
        if (name == null) return;
        var parameter = parameters.FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        var detail = id + "." + RuntimeJson.Text(port, "id") + "." + name;
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural", "port-type", detail);
        var members = EnumMembers(parameter);
        RuntimeJson.Require(members.Length > 0, "port-type", detail);
        foreach (var member in members)
        {
            RuntimeJson.Require(IsValueTypeMember(member), "port-type", detail + "." + member);
            RuntimeJson.Require(execution != "pure" || !(WorldPort(member) || GuardedHandleKind(member) != null),
                RuntimeAbiCodes.PureWorldPort, detail + "." + member);
        }
    }

    /// <summary>The structural enum a port's `schemaParameter` defers to, checked against the members it could ever
    /// resolve to: every member names a shared member set, because the resolved port's schema is an identity both
    /// sides compare and a member without one would compile a port nothing could read. Mirrors
    /// site/forge/graph-schema.ts portSchemaParameter.</summary>
    private static void ValidateSchemaParameter(JsonElement port, JsonElement[] parameters, string id)
    {
        var name = Optional(port, "schemaParameter");
        if (name == null) return;
        var parameter = parameters.FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        var detail = id + "." + RuntimeJson.Text(port, "id") + "." + name;
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural", "port-type", detail);
        var members = EnumMembers(parameter);
        RuntimeJson.Require(members.Length > 0, "port-type", detail);
        foreach (var member in members) RuntimeJson.Require(EnumSets.ContainsKey(member), "port-type", detail + "." + member);
    }

    /// <summary>Every port a contract declares, the variable templates and port-group slots included: a typed port
    /// is checked against its parameter wherever it is declared, not only on the two fixed sides.</summary>
    private static JsonElement[] DeclaredPorts(JsonElement graph)
    {
        var ports = RuntimeJson.Rows(graph, "inputs").Concat(RuntimeJson.Rows(graph, "outputs")).ToList();
        if (graph.TryGetProperty("variadic", out var variadic) && variadic.ValueKind == JsonValueKind.Object
            && variadic.TryGetProperty("port", out var template)) ports.Add(template);
        if (graph.TryGetProperty("portGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var group in groups.EnumerateArray())
                if (group.ValueKind == JsonValueKind.Object) ports.AddRange(RuntimeJson.Rows(group, "slots"));
        return ports.ToArray();
    }

    /// <summary>The reads a contract declares, in declaration order: empty on a contract that declares none.</summary>
    internal static string[] ReadDeclarations(JsonElement graph)
        => graph.TryGetProperty("reads", out var reads) ? RuntimeJson.Strings(reads, unique: false) : Array.Empty<string>();

    /// <summary>The closed list, checked where the contract is registered: an unknown source or a repeated one is a
    /// contract error, not a capability that quietly reads less than it says. `reads` describes an observation, so
    /// only the two evaluated tiers may declare it — an executed capability that reads the world does so through
    /// its handler, and would otherwise be able to claim an observation's budget.</summary>
    private static void ValidateReadDeclarations(JsonElement graph, string execution, string id)
    {
        var reads = ReadDeclarations(graph);
        if (reads.Length == 0 && !graph.TryGetProperty("reads", out _)) return;
        RuntimeJson.Require(reads.Length > 0, "read-declaration", id);
        foreach (var source in reads) RuntimeJson.Require(ReadSources.Contains(source), "read-source", id + "." + source);
        RuntimeJson.Require(reads.Distinct(StringComparer.Ordinal).Count() == reads.Length, "read-source", id);
        RuntimeJson.Require(execution is "pure" or "query", "read-declaration", id);
        // A pure evaluation reads no world state at all, and a declaration is a read even when no port carries one.
        RuntimeJson.Require(!(execution == "pure" && reads.Length > 0), RuntimeAbiCodes.PureWorldRead, id);
    }

    /// <summary>True for the port types that exist only in a live world: an entity, a resource reference or a
    /// handle. A selector, condition or modifier that declares one — or that declares a read — is an observation.</summary>
    internal static bool WorldPort(string type) => type is "entity" or "resource" or "handle";

    /// <summary>True for the port types a collection may carry: the value types with an element form plus a handle.
    /// A resource, an event and a result row are a reference or a row rather than a value, so none of them has a
    /// collection form — the fact itself is <see cref="RuntimeFrames.Segmented"/>, asked here by port type.</summary>
    internal static bool Segmented(string type)
        => RuntimeValueTypes.Contains(type) && RuntimeFrames.Segmented(RuntimeFrames.KindOf(type));

    /// <summary>True when a contract reads the world: a world port on either side, a world-typed variable template
    /// or port-group slot, or any declared read. Mirrors site/forge/graph-schema.ts readsWorld.</summary>
    internal static bool ReadsWorld(JsonElement graph)
        => DeclaredPorts(graph).Any(p => WorldPort(RuntimeJson.Text(p, "type"))) || ReadDeclarations(graph).Length > 0;

    /// <summary>One port of a capability graph by side (`inputs`/`outputs`) and id, for the consumers that address a
    /// declared port by name rather than by slot.</summary>
    internal static JsonElement? Port(JsonElement capability, string side, string id)
    {
        if (!capability.TryGetProperty("graph", out var graph)) return null;
        foreach (var port in RuntimeJson.Rows(graph, side))
            if (RuntimeJson.Text(port, "id") == id) return port;
        return null;
    }

    /// <summary>Forge Standard v0.2 metadata plus the capability-kind rules every registry applies.</summary>
    internal static void ValidateCapability(string kind, JsonElement graph, string id)
    {
        RuntimeJson.Shape(graph, "domains execution inputs outputs parameters", "reads recipients variadic portGroups");
        var domains = RuntimeJson.Strings(graph.GetProperty("domains"));
        RuntimeJson.Require(domains.Length > 0 && domains.All(Domains.Contains), "graph-domain", id);
        var execution = RuntimeJson.Text(graph, "execution");
        RuntimeJson.Require(ExecutionTiers.Contains(execution), "graph-authority", id);
        var inputs = RuntimeJson.Rows(graph, "inputs"); var outputs = RuntimeJson.Rows(graph, "outputs");
        foreach (var ports in new[] { inputs, outputs })
        {
            foreach (var port in ports) ValidatePort(port, id);
            RuntimeJson.Require(ports.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == ports.Length, "duplicate-port", id);
        }
        // A context role may only be left unwired where the row's own contract says so (rule 146.4g makes
        // `combat.damage.source` optional: environmental damage has no dealer, and leaving the port out is the
        // author's own statement of that). Nullable stays refused on every row — a wire that may hand the row
        // nobody is ambiguous, and nothing may fill a context role in implicitly. Which ports are roles, and that
        // one rule about them, both live in the role table: no implicit source/owner fallback, one criterion.
        foreach (var port in inputs.Where(p => RuntimeActorRoles.IsRolePort(RuntimeJson.Text(p, "id"))))
            RuntimeActorRoles.RequireRolePort(port, id);
        if (graph.TryGetProperty("recipients", out var recipients)) ValidateRecipients(recipients, inputs, outputs, id);
        var parameters = RuntimeJson.Rows(graph, "parameters");
        RuntimeJson.Require(parameters.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == parameters.Length, "duplicate-parameter", id);
        foreach (var parameter in parameters) ValidateParameter(parameter, inputs, id);
        // A typed port hands its concrete type rules to the member it resolves to, so what is checked here is the
        // parameter itself and every member it offers — a variable template and a port-group slot included.
        foreach (var port in DeclaredPorts(graph)) { ValidateTypeParameter(port, parameters, execution, id); ValidateSchemaParameter(port, parameters, id); }
        ValidateVariadic(graph, id);
        ValidatePortGroups(graph, id);
        ValidateReadDeclarations(graph, execution, id);
        // I-CATALOG: an observation that touches the world is a `query`; a value-only observation stays `pure`
        // and is refused the moment it declares an entity, resource or handle port, a world-typed variable port, or
        // any read at all. Executable kinds are never either tier — they run on the host and are dispatched, not
        // evaluated on demand. A `pure` capability that reads is refused as `pure-world-port` before the tier rule
        // runs: that is the more specific fact.
        var worldPorts = DeclaredPorts(graph).Any(p => WorldPort(RuntimeJson.Text(p, "type")));
        if (execution == "pure")
        {
            RuntimeJson.Require(!inputs.Concat(outputs).Any(p => RuntimeJson.Text(p, "type") == "execution"), "pure-execution", id);
            RuntimeJson.Require(!worldPorts, RuntimeAbiCodes.PureWorldPort, id);
        }
        // The evaluated families. `state` is the value row's own kind: a read-only answer about the world that is
        // asked for on demand and never published, which is the same authority rule the three older families
        // follow — a row that names a world port or declares a read is a query, the rest are pure.
        if (kind is "selector" or "condition" or "modifier" or "state")
            RuntimeJson.Require(execution == (worldPorts || ReadDeclarations(graph).Length > 0 ? "query" : "pure"), RuntimeAbiCodes.QueryAuthority, id);
        if (kind is "trigger" or "action" or "control") RuntimeJson.Require(execution is "host" or "owner" or "presentation", "executable-authority", id);
        if (kind == "trigger") RuntimeJson.Require(!inputs.Any(p => RuntimeJson.Text(p, "type") == "execution"), "trigger-input", id);
        RuntimeJson.Require(kind == "action" || recipients.ValueKind == JsonValueKind.Undefined, "recipient-owner", id);
        // Every action, not only the ones that happen to take an entity.
        if (kind == "action") RuntimeJson.Require(recipients.ValueKind != JsonValueKind.Undefined, "recipient-contract", id);
    }
    private static void ValidateRecipients(JsonElement spec, JsonElement[] inputs, JsonElement[] outputs, string id)
    {
        RuntimeJson.Shape(spec, "input target cardinality requires result", "handle");
        var target = RuntimeJson.Text(spec, "target"); var cardinality = RuntimeJson.Text(spec, "cardinality");
        RuntimeJson.Require(RecipientTargets.Contains(target), "recipient-target", id);
        RuntimeJson.Require(Cardinalities.Contains(cardinality), "recipient-cardinality", id);
        var inputName = RuntimeJson.Text(spec, "input");
        var input = inputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == inputName);
        RuntimeJson.Require(input.ValueKind == JsonValueKind.Object && RuntimeJson.Text(input, "type") == target
            && !RuntimeJson.Flag(input, "optional") && !RuntimeJson.Flag(input, "nullable"), "recipient-port", id);
        RuntimeJson.Require(Cardinality(input) == cardinality, "recipient-cardinality", id);
        var requirements = RuntimeJson.Strings(spec.GetProperty("requires"));
        RuntimeJson.Require(requirements.Length <= 128 && requirements.All(RuntimeJson.IsId), "recipient-requirements", id);
        var resultName = RuntimeJson.Text(spec, "result");
        var result = outputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == resultName);
        RuntimeJson.Require(result.ValueKind == JsonValueKind.Object && RuntimeJson.Text(result, "type") == "result"
            && !RuntimeJson.Flag(result, "optional") && !RuntimeJson.Flag(result, "nullable"), "recipient-result", id);
        if (!spec.TryGetProperty("handle", out _)) return;
        var handleName = RuntimeJson.Text(spec, "handle");
        RuntimeJson.Require(outputs.Any(p => RuntimeJson.Text(p, "id") == handleName && RuntimeJson.Text(p, "type") == "handle"), "recipient-handle", id);
    }
    private static void ValidateParameter(JsonElement parameter, JsonElement[] inputs, string id)
    {
        RuntimeJson.Shape(parameter, "id type role required", "minimum maximum values set unit resourceKind");
        var name = RuntimeJson.Text(parameter, "id"); var type = RuntimeJson.Text(parameter, "type"); var role = RuntimeJson.Text(parameter, "role");
        RuntimeJson.Require(IsName(name), "parameter-name", id);
        RuntimeJson.Require(ParameterTypes.Contains(type), "parameter-type", id);
        RuntimeJson.Require(role is "value" or "structural", "parameter-role", id);
        // A resource parameter is the one compile-time reference a plan may write into a step's constant frame, so
        // it names the kind that reference is read through: the frame encoder indexes the kind table by it and the
        // value validator checks the reference against it, so a reference that named no kind could not compile at
        // all. A parameter of any other type names no kind.
        var resourceKind = Optional(parameter, "resourceKind");
        RuntimeJson.Require(type == "resource" ? resourceKind != null && ResourceKinds.Contains(resourceKind) : resourceKind == null,
            "parameter-resource-kind", id);
        RuntimeJson.Require(parameter.GetProperty("required").ValueKind is JsonValueKind.True or JsonValueKind.False, "parameter-required", id);
        foreach (var bound in new[] { "minimum", "maximum" }) if (parameter.TryGetProperty(bound, out var value))
        {
            RuntimeJson.Require(type is "number" or "integer", "parameter-bound-type", id);
            RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number), "parameter-bound", id);
            if (type == "integer") RuntimeJson.Integer(value, -RuntimeJson.MaxSafeInteger);
        }
        if (parameter.TryGetProperty("minimum", out var minimum) && parameter.TryGetProperty("maximum", out var maximum))
            RuntimeJson.Require(minimum.GetDouble() <= maximum.GetDouble(), "parameter-bounds", id);
        var hasValues = parameter.TryGetProperty("values", out var values); var set = Optional(parameter, "set");
        if (type != "enum") RuntimeJson.Require(!hasValues && set == null, "parameter-values", id);
        else
        {
            // A promotable enum names a whole shared set (a promoted port carries the set, not a subset).
            // A structural enum inlines its members, names a whole set, or narrows one to a proper subset in set order.
            RuntimeJson.Require(set == null ? role == "structural" : EnumSets.ContainsKey(set), "parameter-set", id);
            RuntimeJson.Require(!hasValues || role == "structural", "parameter-set", id);
            if (hasValues)
            {
                var members = RuntimeJson.Strings(values);
                // A member may carry one `:` segment for the members whose value class is two words: a guarded
                // handle writes its kind after the class (`handle:effect`, ruling 158.4). Mirrors site/forge/
                // graph-schema.ts MEMBER, which allows the same single segment.
                RuntimeJson.Require(members.Length > 0 && members.Distinct(StringComparer.Ordinal).Count() == members.Length
                    && members.All(m => Regex.IsMatch(m, @"^[a-z][a-z0-9_-]*(?::[a-z][a-z0-9_]*)?$", RegexOptions.CultureInvariant)), "enum-values", id);
                if (set != null)
                {
                    var positions = members.Select(m => Array.IndexOf(EnumSets[set], m)).ToArray();
                    RuntimeJson.Require(positions.All(p => p >= 0) && positions.Zip(positions.Skip(1)).All(p => p.First < p.Second)
                        && positions.Length < EnumSets[set].Length, "enum-values", id);
                }
            }
        }
        if (parameter.TryGetProperty("unit", out var unit))
        { RuntimeJson.Text(unit); RuntimeJson.Require(type is "integer" or "number" or "vector3", "parameter-unit", id); }
        // A promoted value parameter becomes an input port with the same id.
        if (role == "value") RuntimeJson.Require(!inputs.Any(p => RuntimeJson.Text(p, "id") == name), "parameter-collision", id);
    }
    private static (long Minimum, long Maximum) CountParameter(JsonElement graph, string name, string id)
    {
        var count = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        RuntimeJson.Require(count.ValueKind == JsonValueKind.Object && RuntimeJson.Text(count, "type") == "integer"
            && RuntimeJson.Text(count, "role") == "structural" && !RuntimeJson.Flag(count, "required"), "variadic-count-parameter", id);
        RuntimeJson.Require(count.TryGetProperty("minimum", out _) && count.TryGetProperty("maximum", out _), "variadic-count-bounds", id);
        var lower = RuntimeJson.Integer(count.GetProperty("minimum"), 2, MaximumVariadicPorts - 1);
        return (lower, RuntimeJson.Integer(count.GetProperty("maximum"), lower + 1, MaximumVariadicPorts));
    }
    private static void CheckTemplate(JsonElement template, string executionAuthority, string id)
    {
        ValidatePort(template, id);
        RuntimeJson.Require(!RuntimeJson.Flag(template, "optional") && !RuntimeJson.Flag(template, "nullable"), "variadic-template", id);
        RuntimeJson.Require(executionAuthority != "pure" || RuntimeJson.Text(template, "type") != "execution", "pure-execution", id);
    }
    private static void CheckAdded(HashSet<string> ids, string added, string id)
    {
        RuntimeJson.Require(added.Length <= 256, "variadic-port-name-budget", id);
        RuntimeJson.Require(!ids.Contains(added), "variadic-port-conflict", id);
    }
    private static string PortId(string template, long index) => template + "_" + index.ToString(CultureInfo.InvariantCulture);
    private static void ValidateVariadic(JsonElement graph, string id)
    {
        if (!graph.TryGetProperty("variadic", out var spec)) return;
        RuntimeJson.Shape(spec, "side parameter port");
        var side = RuntimeJson.Text(spec, "side");
        RuntimeJson.Require(side is "inputs" or "outputs", "variadic-side", id);
        var template = spec.GetProperty("port"); CheckTemplate(template, RuntimeJson.Text(graph, "execution"), id);
        var (lower, upper) = CountParameter(graph, RuntimeJson.Text(spec, "parameter"), id);
        var ports = RuntimeJson.Rows(graph, side);
        RuntimeJson.Require(ports.Length == lower, "variadic-base-count", id);
        foreach (var port in ports)
            RuntimeJson.Require(SameValue(port, template) && !RuntimeJson.Flag(port, "nullable") && !RuntimeJson.Flag(port, "optional"), "variadic-port-contract", id);
        var ids = ports.Select(p => RuntimeJson.Text(p, "id")).ToHashSet(StringComparer.Ordinal);
        for (var i = lower + 1; i <= upper; i++) CheckAdded(ids, PortId(RuntimeJson.Text(template, "id"), i), id);
    }
    /// <summary>A typed tuple repeated as a block; the whole block exists at minimum and is declared, not implied.</summary>
    private static void ValidatePortGroups(JsonElement graph, string id)
    {
        if (!graph.TryGetProperty("portGroups", out _)) return;
        var groups = RuntimeJson.Rows(graph, "portGroups", 2);
        RuntimeJson.Require(groups.Length > 0, "port-groups", id);
        var execution = RuntimeJson.Text(graph, "execution");
        var variadicSide = graph.TryGetProperty("variadic", out var variadic) ? RuntimeJson.Text(variadic, "side") : null;
        var sides = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in groups)
        {
            RuntimeJson.Shape(spec, "id side parameter minimum maximum slots");
            RuntimeJson.Require(IsName(RuntimeJson.Text(spec, "id")) && names.Add(RuntimeJson.Text(spec, "id")), "port-group-name", id);
            var side = RuntimeJson.Text(spec, "side");
            RuntimeJson.Require(side is "inputs" or "outputs", "variadic-side", id);
            RuntimeJson.Require(side != variadicSide && sides.Add(side), "port-group-side", id);
            var slots = RuntimeJson.Rows(spec, "slots", 8);
            RuntimeJson.Require(slots.Length > 0 && slots.Select(s => RuntimeJson.Text(s, "id")).Distinct(StringComparer.Ordinal).Count() == slots.Length, "port-group-slots", id);
            foreach (var slot in slots) CheckTemplate(slot, execution, id);
            var (lower, upper) = CountParameter(graph, RuntimeJson.Text(spec, "parameter"), id);
            RuntimeJson.Require(RuntimeJson.Integer(spec.GetProperty("minimum"), 0, MaximumVariadicPorts) == lower
                && RuntimeJson.Integer(spec.GetProperty("maximum"), 0, MaximumVariadicPorts) == upper, "variadic-count-bounds", id);
            var ports = RuntimeJson.Rows(graph, side);
            var start = Array.FindIndex(ports, p => RuntimeJson.Text(p, "id") == PortId(RuntimeJson.Text(slots[0], "id"), 1));
            RuntimeJson.Require(start >= 0 && start + lower * slots.Length <= ports.Length, "port-group-base", id);
            for (var i = 1; i <= lower; i++)
                for (var j = 0; j < slots.Length; j++)
                {
                    var port = ports[start + (i - 1) * slots.Length + j];
                    RuntimeJson.Require(RuntimeJson.Text(port, "id") == PortId(RuntimeJson.Text(slots[j], "id"), i), "port-group-base", id);
                    RuntimeJson.Require(SameValue(port, slots[j]) && !RuntimeJson.Flag(port, "optional") && !RuntimeJson.Flag(port, "nullable"), "variadic-port-contract", id);
                }
            var ids = ports.Select(p => RuntimeJson.Text(p, "id")).ToHashSet(StringComparer.Ordinal);
            for (var i = lower + 1; i <= upper; i++)
                foreach (var slot in slots) CheckAdded(ids, PortId(RuntimeJson.Text(slot, "id"), i), id);
        }
    }

    /// <summary>Expands variable ports and port groups, then appends promoted value parameters as inputs; metadata and port order are preserved.</summary>
    internal static JsonElement Resolve(JsonElement graph, JsonElement parameters, IReadOnlySet<string>? promoted = null)
    {
        var resolved = graph.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        long Size(string name, long minimum)
        {
            var definition = RuntimeJson.Rows(graph, "parameters").Single(p => RuntimeJson.Text(p, "id") == name);
            return parameters.TryGetProperty(name, out var value)
                ? RuntimeJson.Integer(value, minimum, RuntimeJson.Integer(definition.GetProperty("maximum"))) : minimum;
        }
        JsonElement Port(JsonElement template, long index)
        {
            var fields = template.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            fields["id"] = RuntimeJson.From(PortId(RuntimeJson.Text(template, "id"), index));
            return RuntimeJson.From(fields);
        }
        if (graph.TryGetProperty("variadic", out var spec))
        {
            var side = RuntimeJson.Text(spec, "side"); var ports = RuntimeJson.Rows(graph, side).ToList();
            var count = Size(RuntimeJson.Text(spec, "parameter"), ports.Count);
            for (long i = ports.Count + 1; i <= count; i++) ports.Add(Port(spec.GetProperty("port"), i));
            resolved[side] = RuntimeJson.From(ports);
        }
        if (graph.TryGetProperty("portGroups", out var groups))
            foreach (var group in groups.EnumerateArray())
            {
                var side = RuntimeJson.Text(group, "side"); var slots = RuntimeJson.Rows(group, "slots");
                var lower = RuntimeJson.Integer(group.GetProperty("minimum"));
                var count = Size(RuntimeJson.Text(group, "parameter"), lower);
                var ports = resolved[side].EnumerateArray().ToList();
                var end = ports.FindIndex(p => RuntimeJson.Text(p, "id") == PortId(RuntimeJson.Text(slots[0], "id"), 1)) + (int)lower * slots.Length;
                var added = new List<JsonElement>();
                for (var i = lower + 1; i <= count; i++) added.AddRange(slots.Select(slot => Port(slot, i)));
                ports.InsertRange(end, added);
                resolved[side] = RuntimeJson.From(ports);
            }
        if (promoted is { Count: > 0 })
        {
            var definitions = RuntimeJson.Rows(graph, "parameters");
            // Declaration order, not authoring order: the compiled frame must not depend on click order.
            var moved = definitions.Where(p => promoted.Contains(RuntimeJson.Text(p, "id"))).ToArray();
            var names = string.Join(" ", moved.Select(p => RuntimeJson.Text(p, "id")));
            RuntimeJson.Require(moved.Length == promoted.Count && moved.All(p => RuntimeJson.Text(p, "role") == "value"), "promotion-role", names);
            var inputs = resolved["inputs"].EnumerateArray().Concat(moved.Select(PromotedPort)).ToArray();
            RuntimeJson.Require(inputs.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == inputs.Length, "promotion-collision", names);
            resolved["inputs"] = RuntimeJson.From(inputs);
            resolved["parameters"] = RuntimeJson.From(definitions.Where(p => !promoted.Contains(RuntimeJson.Text(p, "id"))).ToArray());
        }
        // Typed ports come last, on both sides and in declaration order: every port a node instance declares is
        // already expanded by then, so the contract a plan is compiled against carries concrete types throughout.
        foreach (var side in new[] { "inputs", "outputs" })
            resolved[side] = RuntimeJson.From(resolved[side].EnumerateArray().Select(port => TypedPort(graph, port, parameters)).ToArray());
        return RuntimeJson.From(resolved);
    }
    private static JsonElement TypedPort(JsonElement graph, JsonElement port, JsonElement parameters)
    {
        port = ValueTypePort(graph, port, parameters);
        return SchemaTypePort(graph, port, parameters);
    }

    /// <summary>The same resolution for the schema dimension: the member the author chose becomes the port's
    /// concrete enum set, and the port's own type rules then apply to it exactly as if the set had been written on
    /// the row. This is where a row that covers every shared set becomes a contract naming one, long before a plan
    /// exists. Mirrors site/forge/graph-schema.ts schemaTypePort.</summary>
    private static JsonElement SchemaTypePort(JsonElement graph, JsonElement port, JsonElement parameters)
    {
        var name = Optional(port, "schemaParameter");
        if (name == null) return port;
        var detail = RuntimeJson.Text(port, "id") + "." + name;
        var parameter = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural", "port-type", detail);
        var written = parameters.TryGetProperty(name, out var given);
        RuntimeJson.Require(written || !RuntimeJson.Flag(parameter, "required"), "port-type", detail);
        var member = EnumMember(parameter, written ? given : RuntimeJson.From(EnumMembers(parameter)[0]));
        RuntimeJson.Require(EnumSets.ContainsKey(member), "port-type", detail + "." + member);
        var fields = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields["schema"] = RuntimeJson.From(member);
        var resolved = RuntimeJson.From(fields);
        RequirePortTypeContract(resolved, RuntimeJson.Text(resolved, "type"), RuntimeJson.Text(port, "id"));
        return resolved;
    }

    /// <summary>The port a typed contract resolves to: the class the port declares is replaced by the member its
    /// structural parameter names, and that member's own type is then the port's — its metadata rules included, so a
    /// member the declared metadata cannot carry is refused here rather than promised by the row. An unknown member, a
    /// member the parameter does not offer and a required parameter left unwritten are all the same authoring mistake.
    /// An optional parameter the author left unwritten is not: it resolves to the first member of its own list, which
    /// is the member the declaration order names as the default — that is what lets a glue row like `present` be
    /// dropped on a wire without a second answer to a question the wire already answered. Mirrors
    /// site/forge/graph-schema.ts valueTypePort.</summary>
    private static JsonElement ValueTypePort(JsonElement graph, JsonElement port, JsonElement parameters)
    {
        var name = ValueTypeParameter(port);
        if (name == null) return port;
        var detail = RuntimeJson.Text(port, "id") + "." + name;
        var parameter = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        RuntimeJson.Require(parameter.ValueKind == JsonValueKind.Object && RuntimeJson.Text(parameter, "type") == "enum"
            && RuntimeJson.Text(parameter, "role") == "structural", "port-type", detail);
        var written = parameters.TryGetProperty(name, out var given);
        RuntimeJson.Require(written || !RuntimeJson.Flag(parameter, "required"), "port-type", detail);
        var member = EnumMember(parameter, written ? given : RuntimeJson.From(EnumMembers(parameter)[0]));
        RuntimeJson.Require(IsValueTypeMember(member) && EnumMembers(parameter).Contains(member, StringComparer.Ordinal),
            "port-type", detail + "." + member);
        var handleKind = GuardedHandleKind(member);
        var fields = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields["type"] = RuntimeJson.From(handleKind != null ? "handle" : member);
        if (handleKind != null)
        {
            // Both halves of the handle's contract travel with the resolved port, or the port would name a kind
            // the value it carries does not have.
            fields["handleKind"] = RuntimeJson.From(handleKind);
            fields["lifetime"] = RuntimeJson.From(GuardedHandleLifetimes[handleKind]);
        }
        // A typed port that resolves to an entity or a handle is the one shape that may be absent: both are world
        // identities a read publishes as "not there yet" (ruling 158.5), which is what a when-present step guards.
        if (handleKind != null || member == "entity") fields["nullable"] = RuntimeJson.From(true);
        var resolved = RuntimeJson.From(fields);
        RequirePortTypeContract(resolved, handleKind != null ? "handle" : member, RuntimeJson.Text(port, "id"));
        return resolved;
    }

    /// <summary>One enum parameter value in either of the two spellings it has on the wire: the member name an
    /// authored call passes, and the member-set index a compiled plan carries in `layout.constants`. The index basis
    /// is the parameter's own member list — its inline `values` when it narrows, its named set otherwise — which is
    /// the basis the frame encoder validates the same constant against and the website compiles it with, so a
    /// narrowed parameter can never be read through the whole set it came from.
    /// Mirrors site/forge/graph-schema.ts enumMembersOf.</summary>
    private static string EnumMember(JsonElement parameter, JsonElement value)
    {
        var id = RuntimeJson.Text(parameter, "id");
        if (value.ValueKind == JsonValueKind.String) return RuntimeJson.Text(value);
        var members = EnumMembers(parameter);
        var index = -1d;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out index)
            && double.IsFinite(index) && index == Math.Truncate(index) && index >= 0 && index < members.Length,
            "invalid-enum", id);
        return members[(int)index];
    }
    /// <summary>A literal moved onto an input keeps its value contract; bounds and members are rechecked per dispatch.</summary>
    private static JsonElement PromotedPort(JsonElement parameter)
    {
        var type = RuntimeJson.Text(parameter, "type");
        var port = new Dictionary<string, JsonElement>(StringComparer.Ordinal) {
            ["id"] = parameter.GetProperty("id").Clone(), ["type"] = RuntimeJson.From(type == "recipient-policy" ? "policy" : type)
        };
        if (type == "recipient-policy") port["schema"] = RuntimeJson.From("forge.policy.recipient");
        else if (Optional(parameter, "set") is { } set) port["schema"] = RuntimeJson.From(set);
        if (parameter.TryGetProperty("unit", out var unit)) port["unit"] = unit.Clone();
        if (!RuntimeJson.Flag(parameter, "required")) port["optional"] = RuntimeJson.From(true);
        return RuntimeJson.From(port);
    }
    /// <summary>Dense slot frame of one resolved side, identical to the website's compiledNodeLayout.</summary>
    internal static JsonElement Layout(JsonElement resolved, string side)
        => RuntimeJson.From(RuntimeJson.Rows(resolved, side).Select((port, index) => new {
            index, type = Array.IndexOf(PortTypes, RuntimeJson.Text(port, "type")),
            cardinality = Array.IndexOf(Cardinalities, Cardinality(port)), valueSet = ValueSet(port),
            lifetime = Optional(port, "lifetime") is { } lifetime ? Array.IndexOf(HandleLifetimes, lifetime) : -1,
            optional = RuntimeJson.Flag(port, "optional"), nullable = RuntimeJson.Flag(port, "nullable")
        }).ToArray());
    private static int ValueSet(JsonElement port) => RuntimeJson.Text(port, "type") switch {
        // `FindIndex` answers 0 for a member that is not there, which is a valid index: a name outside the table
        // has to read as absent, or the plan's own layout and the re-derived one would disagree on an index both
        // sides called "no set".
        "enum" => Math.Max(-1, Array.FindIndex(EnumSetTable, set => set.Name == Optional(port, "schema"))),
        "resource" => Array.IndexOf(ResourceKinds, Optional(port, "resourceKind")),
        "handle" => Array.IndexOf(HandleKinds, Optional(port, "handleKind")),
        _ => -1
    };
}

/// <summary>The shared enum vocabulary as a package reads it: the names of the member sets the runtime knows, in the
/// declaration order that is every set's compiled index basis. A row that covers all of them at once — the glue
/// rows whose `enum_set` parameter chooses one set per node instance — has to spell that list in its own contract,
/// and spelling it a second time in a package is how the two copies drift. Mirrors the website's `graphEnumSets`
/// key order, which the C# table declares and an SDK test reads back.</summary>
public static class RuntimeEnumSets
{
    public static IReadOnlyList<string> Names => RuntimeGraphContracts.EnumSetNames;
    /// <summary>One set's members, in the order its compiled values index.</summary>
    public static IReadOnlyList<string> Members(string set)
        => RuntimeGraphContracts.EnumSets.TryGetValue(set, out var members)
            ? members : throw new RuntimeContractException("port-enum-set", "Unknown enum set: " + set);
}
