using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeEnemy.Profile;

/// <summary>The enemy profile family: what a website-exported `forge/enemies/*.json` document may say, and what
/// the game side is allowed to conclude from a set of them. This file is the whole derivation and it is
/// game-independent on purpose — the same entry is called from `Plugin.Load` (through the filesystem
/// discovery) and from the tests' fixtures, so the accepted shape and the rejection codes cannot drift from
/// what is shipped.
///
/// Why this is data and not a capability row. The facts here are per-enemy constants: which limbs a type has,
/// how tough they are, how far it detects, what colour it glows, how many children a birther's ability produces.
/// They do not change while a level runs, so
/// there is nothing for a plan step to decide, nothing to replicate and no host/client divergence to resolve:
/// every peer applies the identical document to the identical native setup and lands on the identical numbers.
/// §3.3 of the framework plan puts static values in data for exactly this reason, which is also why the
/// runtime `limb_profile` / `perception_profile` rows stay unimplemented — those mutate a live enemy and would
/// need a replication channel this build does not have.
///
/// The document shape. The runtime document is the values the website's enemy editor already expanded: one
/// object per enemy type, `{ schemaVersion, enemies: [ { enemyType, limbs?, detection?, appearance?, birthing? } ] }`,
/// with `enemyType` the native data block's `persistentID`. Author groups, per-entry switches and entry ids are
/// the editor's own experience and live in the editor, not in the file the game reads; there is nothing here to
/// resolve between entries.
///
/// One enemy type, one document. The same `enemyType` twice in the loaded documents is a data error rather than
/// a precedence question: that enemy is refused whole, with both file names named, because letting load order
/// decide would make the same file mean different things after an unrelated edit. An attribute no document sets
/// is left exactly as the prefab authored it, so a profile that only changes one limb cannot change anything
/// else.</summary>
internal static class EnemyProfileSchema
{
    /// <summary>The only schema this build reads. A document written for another version is refused rather
    /// than read with today's assumptions.</summary>
    internal const int Version = 1;

    internal const int MaximumDocuments = 64;
    internal const int MaximumDocumentBytes = 1024 * 1024;
    internal const int MaximumEnemiesPerDocument = 256;
    internal const int MaximumLimbsPerEnemy = 64;
}

/// <summary>Which native limb damage type a limb is. The names are the interop enum's, spelled out so a
/// document never carries a raw integer whose meaning only the current build knows.</summary>
internal enum EnemyLimbKind
{
    Normal = 0,
    Weakspot = 1,
    Armor = 2
}

/// <summary>One limb's overrides. A null member means "this document says nothing about that attribute", which
/// is different from a zero: a zero weakspot multiplier is a deliberate value.</summary>
internal sealed class EnemyLimbProfile
{
    internal EnemyLimbProfile(int limbId) { LimbId = limbId; }

    internal int LimbId { get; }
    internal float? Health { get; set; }
    internal float? WeakspotMultiplier { get; set; }
    internal float? ArmorMultiplier { get; set; }
    internal EnemyLimbKind? Kind { get; set; }
}

/// <summary>The detection overrides. These are the four fields `EnemyDetection` reads while it builds up and
/// cools down detection; every one of them is a plain instance field with no replication channel, which is
/// acceptable only because the value is identical on every peer.</summary>
internal sealed class EnemyDetectionProfile
{
    internal float? MovementDistance { get; set; }
    internal float? BuildupSpeed { get; set; }
    internal float? CooldownSpeed { get; set; }
    internal float? NoiseRange { get; set; }
}

/// <summary>The glow override: the colour the enemy's own glow shader value animates to, plus the transition
/// the native interpolator takes. Three or four components, linear 0..1, no alpha guessing.</summary>
internal sealed class EnemyAppearanceProfile
{
    internal float[]? GlowColor { get; set; }
    internal float? GlowTransition { get; set; }
}

/// <summary>The birthing ability's own numbers: how many children one birth produces, the smallest number a birth
/// may produce, the most children allowed to live at once, and the two ends of the wait between births. These are
/// the five fields `EAB_Birthing` reads; the ability itself is attached by the type's own `AI_Abilities` list, so
/// a document only ever adjusts the numbers of an ability the type already has. A null member means "this
/// document says nothing about that number".</summary>
internal sealed class EnemyBirthingProfile
{
    internal int? ChildrenPerBirth { get; set; }
    internal int? ChildrenPerBirthMin { get; set; }
    internal int? ChildrenMax { get; set; }
    internal float? MinDelayUntilNextBirth { get; set; }
    internal float? MaxDelayUntilNextBirth { get; set; }
}

/// <summary>One enemy type's profile, as the editor exported it: the attributes the file sets on it, already
/// expanded out of whatever author grouping produced them. A null member means "this file says nothing about
/// that attribute"; the enemy type is the native data block's own `persistentID`.</summary>
internal sealed class EnemyProfileEnemy
{
    internal EnemyProfileEnemy(uint enemyTypeId, string source) { EnemyTypeId = enemyTypeId; Source = source; }

    internal uint EnemyTypeId { get; }

    /// <summary>The file this enemy came from, for the diagnostics a duplicate enemy type names.</summary>
    internal string Source { get; }

    internal IReadOnlyList<EnemyLimbProfile> Limbs { get; set; } = Array.Empty<EnemyLimbProfile>();
    internal EnemyDetectionProfile? Detection { get; set; }
    internal EnemyAppearanceProfile? Appearance { get; set; }
    internal EnemyBirthingProfile? Birthing { get; set; }
}

/// <summary>One parsed document: the enemy types it profiles. A document owns its own enemy types and nothing
/// else — there is no table another document could merge into and no author grouping left to resolve.</summary>
internal sealed class EnemyProfileDocument
{
    internal EnemyProfileDocument(string source) { Source = source; }

    internal string Source { get; }
    internal List<EnemyProfileEnemy> Enemies { get; } = new();
}

/// <summary>Closed set of rejection codes. A code is part of the contract: the release report and the website
/// both branch on it, so a new kind of refusal gets a new member instead of a reworded detail.</summary>
internal static class EnemyProfileCodes
{
    internal const string Json = "profile-json";
    internal const string Read = "profile-read";
    internal const string Schema = "profile-schema";
    internal const string Budget = "profile-budget";
    internal const string UnknownKey = "profile-unknown-key";
    internal const string EnemyType = "profile-enemy-type";
    internal const string Limb = "profile-limb";
    internal const string Number = "profile-number";
    internal const string Color = "profile-color";
    internal const string Duplicate = "profile-duplicate";
}

/// <summary>One refused document or enemy, with the code the caller branches on and a bounded detail.</summary>
internal readonly struct EnemyProfileRejection
{
    internal EnemyProfileRejection(string code, string source, string detail)
    {
        Code = code; Source = source;
        Detail = detail.Length <= 512 ? detail : detail[..512];
    }

    internal string Code { get; }
    internal string Source { get; }
    internal string Detail { get; }

    public override string ToString() => Code + " in " + Source + ": " + Detail;
}

/// <summary>The parse result: either a document or the first refusal. A document with any refused entry yields
/// no document at all, because a partially applied profile is a different profile than the author wrote.</summary>
internal readonly struct EnemyProfileParse
{
    private EnemyProfileParse(EnemyProfileDocument? document, EnemyProfileRejection rejection)
    {
        Document = document; Rejection = rejection;
    }

    internal EnemyProfileDocument? Document { get; }
    internal EnemyProfileRejection Rejection { get; }
    internal bool Ok => Document != null;

    internal static EnemyProfileParse Parsed(EnemyProfileDocument document) => new(document, default);
    internal static EnemyProfileParse Refused(string code, string source, string detail)
        => new(null, new EnemyProfileRejection(code, source, detail));
}

/// <summary>The one reader of the document text. Strict by design: an unknown key is a refusal, so a renamed
/// or misspelled attribute fails at load with its own name instead of quietly applying nothing.</summary>
internal static class EnemyProfileReader
{
    private static readonly string[] TopLevelKeys = { "schemaVersion", "enemies" };

    internal static EnemyProfileParse Read(string source, string json)
    {
        if (json.Length > EnemyProfileSchema.MaximumDocumentBytes)
            return EnemyProfileParse.Refused(EnemyProfileCodes.Budget, source, "Document exceeds the per-file byte cap.");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException error)
        {
            return EnemyProfileParse.Refused(EnemyProfileCodes.Json, source, "Not valid JSON: " + error.Message);
        }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return EnemyProfileParse.Refused(EnemyProfileCodes.Schema, source, "The document root must be an object.");
            if (UnknownKey(root, TopLevelKeys) is { } extra)
                return EnemyProfileParse.Refused(EnemyProfileCodes.UnknownKey, source, "Unknown top-level key `" + extra + "`.");
            if (!TryVersion(root, out int version))
                return EnemyProfileParse.Refused(EnemyProfileCodes.Schema, source, "`schemaVersion` must be an integer.");
            if (version != EnemyProfileSchema.Version)
                return EnemyProfileParse.Refused(EnemyProfileCodes.Schema, source,
                    "Unsupported schemaVersion " + version.ToString(CultureInfo.InvariantCulture)
                    + "; this build reads " + EnemyProfileSchema.Version.ToString(CultureInfo.InvariantCulture) + ".");

            var document = new EnemyProfileDocument(source);
            if (!root.TryGetProperty("enemies", out var enemies) || enemies.ValueKind != JsonValueKind.Array)
                return EnemyProfileParse.Refused(EnemyProfileCodes.Schema, source, "`enemies` must be an array.");
            if (enemies.GetArrayLength() > EnemyProfileSchema.MaximumEnemiesPerDocument)
                return EnemyProfileParse.Refused(EnemyProfileCodes.Budget, source, "Too many enemies.");
            var seen = new HashSet<uint>();
            foreach (var element in enemies.EnumerateArray())
            {
                var enemy = ReadEnemy(element, document, out var failure);
                if (enemy == null) return EnemyProfileParse.Refused(failure.Code, source, failure.Detail);
                // The same type twice in one file is the same data error as the same type in two files, caught
                // where it happens: the file is refused rather than silently keeping one of the two.
                if (!seen.Add(enemy.EnemyTypeId))
                    return EnemyProfileParse.Refused(EnemyProfileCodes.Duplicate, source,
                        "`enemyType` " + enemy.EnemyTypeId.ToString(CultureInfo.InvariantCulture)
                        + " is declared twice in this document.");
                document.Enemies.Add(enemy);
            }
            return EnemyProfileParse.Parsed(document);
        }
    }

    private static bool TryVersion(JsonElement root, out int version)
    {
        version = 0;
        if (!root.TryGetProperty("schemaVersion", out var value) || value.ValueKind != JsonValueKind.Number) return false;
        return value.TryGetInt32(out version);
    }

    /// <summary>One `enemies[]` element: the enemy type it profiles and the sections it sets. Unknown keys are
    /// refused by name rather than ignored, so a renamed attribute fails at load instead of applying nothing.</summary>
    private static EnemyProfileEnemy? ReadEnemy(JsonElement element, EnemyProfileDocument document, out EnemyProfileRejection failure)
    {
        failure = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, document.Source, "An enemy must be an object.");
            return null;
        }
        if (UnknownKey(element, new[] { "enemyType", "limbs", "detection", "appearance", "birthing" }) is { } extra)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.UnknownKey, document.Source, "Unknown enemy key `" + extra + "`.");
            return null;
        }
        // The type is the native data block's own `persistentID`: a positive whole number, because a zero or a
        // negative id names no block the game can build.
        if (!element.TryGetProperty("enemyType", out var typeElement) || typeElement.ValueKind != JsonValueKind.Number
            || !typeElement.TryGetUInt32(out uint enemyTypeId) || enemyTypeId == 0)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.EnemyType,
                document.Source, "An enemy has no positive integer `enemyType`.");
            return null;
        }
        var enemy = new EnemyProfileEnemy(enemyTypeId, document.Source);
        if (element.TryGetProperty("limbs", out var limbs) && ReadLimbs(limbs, enemy, out failure)) return null;
        if (element.TryGetProperty("detection", out var detection) && ReadDetection(detection, enemy, out failure)) return null;
        if (element.TryGetProperty("appearance", out var appearance) && ReadAppearance(appearance, enemy, out failure)) return null;
        if (element.TryGetProperty("birthing", out var birthing) && ReadBirthing(birthing, enemy, out failure)) return null;
        return enemy;
    }

    private static bool ReadLimbs(JsonElement element, EnemyProfileEnemy enemy, out EnemyProfileRejection failure)
    {
        failure = default;
        if (element.ValueKind != JsonValueKind.Array)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source, "`limbs` must be an array.");
            return true;
        }
        var limbs = new List<EnemyLimbProfile>();
        var seen = new HashSet<int>();
        foreach (var item in element.EnumerateArray())
        {
            if (limbs.Count >= EnemyProfileSchema.MaximumLimbsPerEnemy)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Budget, enemy.Source, "Enemy " + enemy.EnemyTypeId + " has too many limbs.");
                return true;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source, "A limb must be an object.");
                return true;
            }
            if (UnknownKey(item, new[] { "limbId", "health", "weakspotMultiplier", "armorMultiplier", "type" }) is { } extra)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.UnknownKey, enemy.Source, "Unknown limb key `" + extra + "`.");
                return true;
            }
            if (!item.TryGetProperty("limbId", out var idElement) || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt32(out int limbId) || limbId < 0)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source, "A limb has no non-negative integer `limbId`.");
                return true;
            }
            if (!seen.Add(limbId))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Duplicate, enemy.Source,
                    "Enemy " + enemy.EnemyTypeId + " names limb " + limbId.ToString(CultureInfo.InvariantCulture) + " twice.");
                return true;
            }
            var limb = new EnemyLimbProfile(limbId);
            if (item.TryGetProperty("health", out var health))
            {
                if (!Positive(health, out float value))
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "Limb `health` must be finite and greater than zero.");
                    return true;
                }
                limb.Health = value;
            }
            if (item.TryGetProperty("weakspotMultiplier", out var weakspot))
            {
                if (!NonNegative(weakspot, out float value))
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`weakspotMultiplier` must be finite and non-negative.");
                    return true;
                }
                limb.WeakspotMultiplier = value;
            }
            if (item.TryGetProperty("armorMultiplier", out var armor))
            {
                if (!NonNegative(armor, out float value))
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`armorMultiplier` must be finite and non-negative.");
                    return true;
                }
                limb.ArmorMultiplier = value;
            }
            if (item.TryGetProperty("type", out var kind))
            {
                if (kind.ValueKind != JsonValueKind.String || !TryKind(kind.GetString(), out var parsed))
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source,
                        "Limb `type` must be `normal`, `weakspot` or `armor`.");
                    return true;
                }
                limb.Kind = parsed;
            }
            if (limb.Health == null && limb.WeakspotMultiplier == null && limb.ArmorMultiplier == null && limb.Kind == null)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source,
                    "Limb " + limbId.ToString(CultureInfo.InvariantCulture) + " of enemy " + enemy.EnemyTypeId + " sets nothing.");
                return true;
            }
            limbs.Add(limb);
        }
        enemy.Limbs = limbs;
        return false;
    }

    private static bool ReadDetection(JsonElement element, EnemyProfileEnemy enemy, out EnemyProfileRejection failure)
    {
        failure = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, enemy.Source, "`detection` must be an object.");
            return true;
        }
        if (UnknownKey(element, new[] { "movementDistance", "buildupSpeed", "cooldownSpeed", "noiseRange" }) is { } extra)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.UnknownKey, enemy.Source, "Unknown detection key `" + extra + "`.");
            return true;
        }
        var detection = new EnemyDetectionProfile();
        if (element.TryGetProperty("movementDistance", out var movement))
        {
            if (!NonNegative(movement, out float value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`movementDistance` must be finite and non-negative.");
                return true;
            }
            detection.MovementDistance = value;
        }
        if (element.TryGetProperty("buildupSpeed", out var buildup))
        {
            if (!NonNegative(buildup, out float value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`buildupSpeed` must be finite and non-negative.");
                return true;
            }
            detection.BuildupSpeed = value;
        }
        if (element.TryGetProperty("cooldownSpeed", out var cooldown))
        {
            if (!NonNegative(cooldown, out float value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`cooldownSpeed` must be finite and non-negative.");
                return true;
            }
            detection.CooldownSpeed = value;
        }
        if (element.TryGetProperty("noiseRange", out var noise))
        {
            if (!NonNegative(noise, out float value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`noiseRange` must be finite and non-negative.");
                return true;
            }
            detection.NoiseRange = value;
        }
        if (detection.MovementDistance == null && detection.BuildupSpeed == null
            && detection.CooldownSpeed == null && detection.NoiseRange == null)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, enemy.Source,
                "Enemy " + enemy.EnemyTypeId + " has an empty `detection` object.");
            return true;
        }
        enemy.Detection = detection;
        return false;
    }

    private static bool ReadAppearance(JsonElement element, EnemyProfileEnemy enemy, out EnemyProfileRejection failure)
    {
        failure = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, enemy.Source, "`appearance` must be an object.");
            return true;
        }
        if (UnknownKey(element, new[] { "glowColor", "glowTransition" }) is { } extra)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.UnknownKey, enemy.Source, "Unknown appearance key `" + extra + "`.");
            return true;
        }
        var appearance = new EnemyAppearanceProfile();
        if (element.TryGetProperty("glowColor", out var color))
        {
            if (color.ValueKind != JsonValueKind.Array || color.GetArrayLength() is not (3 or 4))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Color, enemy.Source, "`glowColor` must be an array of three or four numbers.");
                return true;
            }
            var components = new float[color.GetArrayLength()];
            int index = 0;
            foreach (var component in color.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Number || !component.TryGetSingle(out float value)
                    || float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Color, enemy.Source, "Every `glowColor` component must be a finite number in 0..1.");
                    return true;
                }
                components[index++] = value;
            }
            appearance.GlowColor = components;
        }
        if (element.TryGetProperty("glowTransition", out var transition))
        {
            if (!NonNegative(transition, out float value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`glowTransition` must be finite and non-negative.");
                return true;
            }
            appearance.GlowTransition = value;
        }
        if (appearance.GlowColor == null)
        {
            // A transition with no colour would animate to whatever the prefab already had, which is a rule the
            // author cannot see in the data. Refused instead of accepted as a no-op.
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, enemy.Source,
                "Enemy " + enemy.EnemyTypeId + " sets `glowTransition` without `glowColor`.");
            return true;
        }
        enemy.Appearance = appearance;
        return false;
    }

    /// <summary>The birthing surface: five numbers, each optional, at least one required. The three counts are
    /// whole children and the two delays are seconds, so a fractional count is refused instead of silently
    /// rounded. `childrenPerBirthMin` is the lower end of the count the component rolls; it is not forced below
    /// `childrenPerBirth`, because the component's own reading order is what decides between them and a document
    /// is allowed to state only one end of a range it does not fully author.</summary>
    private static bool ReadBirthing(JsonElement element, EnemyProfileEnemy enemy, out EnemyProfileRejection failure)
    {
        failure = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, enemy.Source, "`birthing` must be an object.");
            return true;
        }
        if (UnknownKey(element, new[]
            {
                "childrenPerBirth", "childrenPerBirthMin", "childrenMax", "minDelayUntilNextBirth", "maxDelayUntilNextBirth"
            }) is { } extra)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.UnknownKey, enemy.Source, "Unknown birthing key `" + extra + "`.");
            return true;
        }
        var birthing = new EnemyBirthingProfile();
        if (element.TryGetProperty("childrenPerBirth", out var perBirth))
        {
            if (!Children(perBirth, out int value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`childrenPerBirth` must be a whole number of children, 0 or more.");
                return true;
            }
            birthing.ChildrenPerBirth = value;
        }
        if (element.TryGetProperty("childrenPerBirthMin", out var perBirthMin))
        {
            if (!Children(perBirthMin, out int value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`childrenPerBirthMin` must be a whole number of children, 0 or more.");
                return true;
            }
            birthing.ChildrenPerBirthMin = value;
        }
        if (element.TryGetProperty("childrenMax", out var childrenMax))
        {
            if (!Children(childrenMax, out int value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`childrenMax` must be a whole number of children, 0 or more.");
                return true;
            }
            birthing.ChildrenMax = value;
        }
        if (element.TryGetProperty("minDelayUntilNextBirth", out var minDelay))
        {
            if (!NonNegative(minDelay, out float value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`minDelayUntilNextBirth` must be finite and non-negative.");
                return true;
            }
            birthing.MinDelayUntilNextBirth = value;
        }
        if (element.TryGetProperty("maxDelayUntilNextBirth", out var maxDelay))
        {
            if (!NonNegative(maxDelay, out float value))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Number, enemy.Source, "`maxDelayUntilNextBirth` must be finite and non-negative.");
                return true;
            }
            birthing.MaxDelayUntilNextBirth = value;
        }
        if (birthing.ChildrenPerBirth == null && birthing.ChildrenPerBirthMin == null && birthing.ChildrenMax == null
            && birthing.MinDelayUntilNextBirth == null && birthing.MaxDelayUntilNextBirth == null)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, enemy.Source,
                "Enemy " + enemy.EnemyTypeId + " has an empty `birthing` object.");
            return true;
        }
        enemy.Birthing = birthing;
        return false;
    }

    /// <summary>A whole, non-negative child count. Zero is a deliberate value — a birthing type told to produce
    /// none — so only a negative or fractional number is refused.</summary>
    private static bool Children(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number) return false;
        if (!element.TryGetInt32(out int number) || number < 0) return false;
        value = number;
        return true;
    }

    private static bool TryKind(string? text, out EnemyLimbKind kind)
    {
        switch (text)
        {
            case "normal": kind = EnemyLimbKind.Normal; return true;
            case "weakspot": kind = EnemyLimbKind.Weakspot; return true;
            case "armor": kind = EnemyLimbKind.Armor; return true;
            default: kind = EnemyLimbKind.Normal; return false;
        }
    }

    private static bool Positive(JsonElement element, out float value)
        => NonNegative(element, out value) && value > 0f;

    private static bool NonNegative(JsonElement element, out float value)
    {
        value = 0f;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out value)) return false;
        return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
    }

    /// <summary>The first key of an object that the accepted set does not name, or null. Reported instead of
    /// ignored so a typo cannot turn a rule into a no-op.</summary>
    private static string? UnknownKey(JsonElement element, string[] accepted)
    {
        foreach (var property in element.EnumerateObject())
            if (Array.IndexOf(accepted, property.Name) < 0) return property.Name;
        return null;
    }
}

/// <summary>What one enemy type's profile resolved to. `Limbs`, `Detection`, `Appearance` and `Birthing` are
/// exactly what the one document that owns the type set; a type two documents both claim resolves to nothing at
/// all, so the applier writes nothing for that enemy rather than writing a guess.</summary>
internal sealed class EnemyProfileResolution
{
    internal IReadOnlyList<EnemyLimbProfile> Limbs { get; set; } = Array.Empty<EnemyLimbProfile>();
    internal EnemyDetectionProfile? Detection { get; set; }
    internal EnemyAppearanceProfile? Appearance { get; set; }
    internal EnemyBirthingProfile? Birthing { get; set; }
    internal bool IsEmpty => Limbs.Count == 0 && Detection == null && Appearance == null && Birthing == null;
    internal bool Applies => !IsEmpty;
}

/// <summary>The loaded profile table: every accepted document's enemy types, keyed by the native type id. There
/// is nothing to resolve between documents — the website's editor already expanded its own author groups — so the
/// table is a lookup, and the one question it can answer wrongly is a type two files both claim. That one refuses
/// the type whole, naming both files, because letting load order decide would make the same installation mean
/// different things after an unrelated edit.</summary>
internal sealed class EnemyProfileCatalog
{
    private readonly Dictionary<uint, EnemyProfileEnemy> _enemies = new();
    private readonly Dictionary<uint, EnemyProfileResolution> _resolved = new();
    private readonly List<EnemyProfileRejection> _rejections = new();
    /// <summary>The types a duplicate claim dropped, so a refused enemy stays refused however many later files
    /// describe it.</summary>
    private readonly HashSet<uint> _refused = new();

    internal IReadOnlyList<EnemyProfileRejection> Rejections => _rejections;
    internal int EnemyCount => _enemies.Count;

    /// <summary>Adds one accepted document. The only refusal here is a type another document already declared:
    /// that enemy is dropped from the table — both claims, not the later one — and the diagnostic names the two
    /// files, which is the whole answer to "why did my edit do nothing".</summary>
    internal bool Add(EnemyProfileDocument document)
    {
        bool accepted = true;
        foreach (var enemy in document.Enemies)
        {
            if (_refused.Contains(enemy.EnemyTypeId)) { accepted = false; continue; }
            if (_enemies.TryGetValue(enemy.EnemyTypeId, out var owner))
            {
                _rejections.Add(new EnemyProfileRejection(EnemyProfileCodes.Duplicate, document.Source,
                    "Enemy type " + enemy.EnemyTypeId.ToString(CultureInfo.InvariantCulture)
                    + " is declared by `" + owner.Source + "` and by `" + document.Source
                    + "`; neither profile is applied."));
                _enemies.Remove(enemy.EnemyTypeId);
                _refused.Add(enemy.EnemyTypeId);
                accepted = false;
                continue;
            }
            _enemies.Add(enemy.EnemyTypeId, enemy);
        }
        _resolved.Clear();
        return accepted;
    }

    internal void Reject(EnemyProfileRejection rejection) => _rejections.Add(rejection);

    /// <summary>The one enemy type's profile, or an empty resolution for a type no document describes and for one
    /// that was refused.</summary>
    internal EnemyProfileResolution Resolve(uint enemyTypeId)
    {
        if (_resolved.TryGetValue(enemyTypeId, out var cached)) return cached;
        var resolution = new EnemyProfileResolution();
        if (_enemies.TryGetValue(enemyTypeId, out var enemy))
        {
            resolution.Limbs = enemy.Limbs;
            resolution.Detection = enemy.Detection;
            resolution.Appearance = enemy.Appearance;
            resolution.Birthing = enemy.Birthing;
        }
        _resolved.Add(enemyTypeId, resolution);
        return resolution;
    }
}
