using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Agents;
using Enemies;
using ForgeEnemy.Profile;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The enemy profile store: the accepted documents plus the filesystem convention that finds them. It follows
/// the package convention the runtime already uses for plans — a package owns
/// <c>BepInEx/plugins/&lt;package&gt;/forge/&lt;kind&gt;/</c> — with the enemy kind at
/// <c>forge/enemies/*.json</c>. Nothing here reads a plan, a command or the network: a profile is data, so the
/// only thing this type decides is which documents are well-formed and which enemy type each entry reaches.
///
/// Determinism. Directories and files are enumerated in ordinal order and merged in that order, so the same
/// installation resolves to the same values on every peer and on every run. Order itself never picks a winner:
/// two entries claiming the same field for the same enemy are a refusal, not a precedence.
///
/// The store never throws. A package with an unreadable directory, a file that is not JSON, or a document
/// written for another schema is refused with a code and the rest of the installation still loads, because one
/// author's typo must not remove another author's attributes. A refusal is reported once through the module's
/// own diagnostics and is never retried during the session.
/// </summary>
internal sealed class EnemyProfileStore
{
    private readonly EnemyProfileCatalog _catalog;

    private EnemyProfileStore(EnemyProfileCatalog catalog, int documents)
    {
        _catalog = catalog;
        DocumentCount = documents;
    }

    internal int DocumentCount { get; }
    internal int EnemyCount => _catalog.EnemyCount;
    internal IReadOnlyList<EnemyProfileRejection> Rejections => _catalog.Rejections;

    /// <summary>Reads every <c>&lt;pluginRoot&gt;/*/forge/enemies/*.json</c> under a BepInEx root. The root is a
    /// parameter rather than <c>Paths</c> so the tests can point the same entry at a fixture directory.</summary>
    internal static EnemyProfileStore Discover(string bepInExRoot)
    {
        var catalog = new EnemyProfileCatalog();
        if (string.IsNullOrEmpty(bepInExRoot)) return new EnemyProfileStore(catalog, 0);
        string plugins;
        try { plugins = Path.GetFullPath(Path.Combine(bepInExRoot, "plugins")); }
        catch (Exception error)
        {
            catalog.Reject(new EnemyProfileRejection(EnemyProfileCodes.Read, bepInExRoot, "Unusable plugin root: " + error.GetType().Name));
            return new EnemyProfileStore(catalog, 0);
        }
        string[] packages;
        try
        {
            if (!Directory.Exists(plugins)) return new EnemyProfileStore(catalog, 0);
            packages = Directory.GetDirectories(plugins);
            Array.Sort(packages, StringComparer.Ordinal);
        }
        catch (Exception error)
        {
            catalog.Reject(new EnemyProfileRejection(EnemyProfileCodes.Read, plugins, "Unreadable plugin root: " + error.GetType().Name));
            return new EnemyProfileStore(catalog, 0);
        }
        int documents = 0;
        foreach (string package in packages)
        {
            string folder = Path.Combine(package, "forge", "enemies");
            string[] files;
            try
            {
                if (!Directory.Exists(folder)) continue;
                files = Directory.GetFiles(folder, "*.json");
                Array.Sort(files, StringComparer.Ordinal);
            }
            catch (Exception error)
            {
                catalog.Reject(new EnemyProfileRejection(EnemyProfileCodes.Read, Describe(plugins, folder),
                    "Unreadable profile folder: " + error.GetType().Name));
                continue;
            }
            foreach (string file in files)
            {
                string source = Describe(plugins, file);
                if (documents >= EnemyProfileSchema.MaximumDocuments)
                {
                    catalog.Reject(new EnemyProfileRejection(EnemyProfileCodes.Budget, source,
                        "The installation describes more than " + EnemyProfileSchema.MaximumDocuments + " profile documents."));
                    break;
                }
                string text;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > EnemyProfileSchema.MaximumDocumentBytes)
                    {
                        catalog.Reject(new EnemyProfileRejection(EnemyProfileCodes.Budget, source, "Document exceeds the per-file byte cap."));
                        continue;
                    }
                    text = File.ReadAllText(file);
                }
                catch (Exception error)
                {
                    catalog.Reject(new EnemyProfileRejection(EnemyProfileCodes.Read, source, "Unreadable document: " + error.GetType().Name));
                    continue;
                }
                var parse = EnemyProfileReader.Read(source, text);
                if (!parse.Ok) { catalog.Reject(parse.Rejection); continue; }
                // `Add` refuses the enemies a later document duplicates and names both files in its own
                // diagnostic; a document that added at least one enemy is a document this installation holds.
                if (catalog.Add(parse.Document!)) documents++;
            }
        }
        return new EnemyProfileStore(catalog, documents);
    }

    /// <summary>An empty store, for a session that never found a document. It resolves every enemy to nothing,
    /// which is what "no profile was written" means.</summary>
    internal static EnemyProfileStore Empty() => new(new EnemyProfileCatalog(), 0);

    internal EnemyProfileResolution Resolve(uint enemyTypeId) => _catalog.Resolve(enemyTypeId);

    /// <summary>A path relative to the plugin root, so a diagnostic names the package and file an author can
    /// open instead of a machine-specific absolute path.</summary>
    private static string Describe(string plugins, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(plugins, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch (Exception) { return path; }
    }
}

/// <summary>
/// The one native write path a profile submits through, kept in its own type for the same reason
/// <see cref="EnemyNativeWrite"/> is: the game assembly is reached from exactly one audited member. Every member
/// written here was read back from the frozen interop of build 20403457 as a public writable field or a public
/// native method; nothing is reached by reflection and no private member is touched.
///
/// The writes are additive. A profile that names no limb leaves every limb exactly as the prefab authored it,
/// which is why the entry point takes the resolved profile instead of a full limb table: there is no default to
/// invent, and no "unspecified" value that could be mistaken for zero.
/// </summary>
internal static class EnemyNativeProfile
{
    /// <summary>
    /// Applies the limb overrides and returns the limb ids the profile named that this enemy does not declare,
    /// in ascending order. A missing limb is not an error the native side can fix — the enemy type simply has no
    /// such limb — so it is reported instead of skipped silently.
    ///
    /// Health keeps the ratio the native setup already chose. The receiver's own <c>Setup</c> decides both the
    /// maximum and the current health (a checkpoint-restored or difficulty-scaled enemy starts below its
    /// maximum), so writing the maximum alone would leave the enemy standing at the old absolute health and
    /// silently change how hurt it is. The current health is therefore rescaled by the ratio it had, which makes
    /// a full-health enemy stay full and an already-damaged one keep its wound.
    /// </summary>
    internal static List<int> ApplyLimbs(Dam_EnemyDamageBase damage, IReadOnlyList<EnemyLimbProfile> profile)
    {
        var missing = new List<int>();
        var limbs = damage.DamageLimbs;
        if (limbs == null) return missing;
        foreach (var entry in profile)
        {
            Dam_EnemyDamageLimb? limb = null;
            for (int index = 0; index < limbs.Length; index++)
            {
                var candidate = limbs[index];
                if (candidate == null || candidate.m_limbID != entry.LimbId) continue;
                limb = candidate;
                break;
            }
            if (limb == null) { missing.Add(entry.LimbId); continue; }
            if (entry.Health is float health) SetHealth(limb, health);
            if (entry.WeakspotMultiplier is float weakspot) limb.m_weakspotDamageMulti = weakspot;
            if (entry.ArmorMultiplier is float armor) limb.m_armorDamageMulti = armor;
            // The native setter, not the field: the type is what `TestDamageModifiers` and the destruction rules
            // branch on, and only the receiver's own method is allowed to be the answer for "this limb is now a
            // weakspot".
            if (entry.Kind is EnemyLimbKind kind) limb.SetLimbDamageType((eLimbDamageType)(int)kind);
        }
        return missing;
    }

    private static void SetHealth(Dam_EnemyDamageLimb limb, float maximum)
    {
        float previousMax = limb.m_healthMax;
        float previousHealth = limb.m_health;
        limb.m_healthMax = maximum;
        limb.m_health = previousMax > 0f
            ? maximum * Math.Clamp(previousHealth / previousMax, 0f, 1f)
            : maximum;
    }

    /// <summary>
    /// Applies the detection overrides. `noiseRange` is written even when the enemy's own noise detection is
    /// switched off, because switching it on is a behavioural change and not a value: a profile that turned it on
    /// would make an enemy notice sounds it was authored to ignore. An author who wants that writes the
    /// behaviour, and the release note says so.
    /// </summary>
    internal static void ApplyDetection(EnemyDetection detection, EnemyDetectionProfile profile)
    {
        if (profile.MovementDistance is float movement) detection.m_movementDetectionDistance = movement;
        if (profile.BuildupSpeed is float buildup) detection.m_detectionBuildupSpeed = buildup;
        if (profile.CooldownSpeed is float cooldown) detection.m_detectionCooldownSpeed = cooldown;
        if (profile.NoiseRange is float noise) detection.m_noiseDetectionRange = noise;
    }

    /// <summary>
    /// Applies the glow through the receiver's own <c>InterpolateGlow(colour, transitionTime)</c> rather than a
    /// direct write of <c>m_glowColor</c>: the glow is driven by its own animator, so the native entry point that
    /// the game itself uses to change it is the only write whose result the animator will keep. A profile that
    /// names no transition asks for the colour immediately.
    /// </summary>
    internal static void ApplyAppearance(EnemyAppearance appearance, EnemyAppearanceProfile profile)
    {
        if (profile.GlowColor is not { } color) return;
        var rgba = new Color(color[0], color[1], color[2], color.Length > 3 ? color[3] : 1f);
        appearance.InterpolateGlow(rgba, profile.GlowTransition ?? 0f);
    }

    /// <summary>
    /// Applies the birthing numbers to the enemy's own <see cref="EAB_Birthing"/> component. The ability itself is
    /// attached by the type's <c>AI_Abilities</c> list, so a profile only ever adjusts a component the type
    /// already has: an enemy with no birthing component returns false and nothing is written.
    ///
    /// Every member is the number the native state machine reads at the top of each birth cycle
    /// (<c>EAB_Birthing.Update</c>), which is why writing them once at spawn is enough — the first cycle after
    /// the hook already sees the authored values, and a value the document leaves out keeps the prefab's own.
    /// </summary>
    internal static bool ApplyBirthing(EnemyAbilities abilities, EnemyBirthingProfile profile)
    {
        if (abilities.GetAbility((AgentAbility)EnemyAbilityResources.SpawnChildrenAbility) is not EAB_Birthing birthing)
            return false;
        if (profile.ChildrenPerBirth is int perBirth) birthing.m_childrenPerBirth = perBirth;
        if (profile.ChildrenPerBirthMin is int perBirthMin) birthing.m_childrenPerBirthMin = perBirthMin;
        if (profile.ChildrenMax is int maximum) birthing.m_childrenMax = maximum;
        if (profile.MinDelayUntilNextBirth is float minimumDelay) birthing.m_minDelayUntilNextBirth = minimumDelay;
        if (profile.MaxDelayUntilNextBirth is float maximumDelay) birthing.m_maxDelayUntilNextBirth = maximumDelay;
        return true;
    }
}

/// <summary>
/// The module's profile half: it owns the store, resolves one enemy at the point the enemy becomes a known
/// native life, and reports what it refused. The application point is <see cref="EnemyModule.TrackSpawn"/>
/// because that is the first moment after the receiver finished setting itself up — the limb array, the
/// detection component and the appearance component are all built by then — and it happens once per native life
/// on every peer, from identical data, which is why no replication channel is involved.
///
/// Diagnostics are one per enemy type for the whole session, not one per spawn: a profile is static, so a
/// conflict or a limb the type does not declare produces the same sentence every time, and a level with fifty
/// strikers must not produce fifty identical lines.
/// </summary>
internal sealed partial class EnemyModule
{
    private const int MaximumProfileDiagnostics = 256;

    private EnemyProfileStore? _profiles;
    private readonly HashSet<uint> _profiledTypes = new();
    private bool _profileDiagnosticsTruncated;

    internal int ProfileDocumentCount => _profiles?.DocumentCount ?? 0;
    internal int ProfileEnemyCount => _profiles?.EnemyCount ?? 0;
    internal int ProfileRejectionCount => _profiles?.Rejections.Count ?? 0;

    /// <summary>Hands the session's store to the module. Called once, from Load, before any level exists.</summary>
    internal void LoadProfiles(EnemyProfileStore profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        if (profiles.Rejections.Count == 0) return;
        int reported = 0;
        foreach (var rejection in profiles.Rejections)
        {
            if (reported++ >= MaximumProfileDiagnostics)
            {
                ReportQuietly("and " + (profiles.Rejections.Count - MaximumProfileDiagnostics).ToString(CultureInfo.InvariantCulture)
                    + " further enemy profile refusals.");
                break;
            }
            ReportQuietly("enemy profile refused [" + rejection.Code + "] " + rejection);
        }
    }

    /// <summary>
    /// Applies this enemy's profile, if any. Called once per native life from the spawn path
    /// (<see cref="TrackSpawn"/>), which runs after the receiver's own setup on every peer.
    /// </summary>
    private void ApplyProfiles(EnemyAgent enemy)
    {
        var profiles = _profiles;
        var readType = _enemyType;
        if (profiles == null || readType == null) return;
        uint typeId;
        try
        {
            if (readType(enemy) is not { } resolved) return;
            typeId = resolved;
        }
        catch (Exception) { return; }
        var profile = profiles.Resolve(typeId);
        if (profile.IsEmpty) return;
        var missing = new List<int>();
        try
        {
            if (profile.Limbs.Count != 0 && enemy.Damage is { } damage)
                missing = EnemyNativeProfile.ApplyLimbs(damage, profile.Limbs);
            if (profile.Detection is { } detection && enemy.AI is { } ai && ai.m_detection is { } nativeDetection)
                EnemyNativeProfile.ApplyDetection(nativeDetection, detection);
            if (profile.Appearance is { } appearance && enemy.Appearance is { } nativeAppearance)
                EnemyNativeProfile.ApplyAppearance(nativeAppearance, appearance);
            if (profile.Birthing is { } birthing && enemy.Abilities is { } nativeAbilities)
                EnemyNativeProfile.ApplyBirthing(nativeAbilities, birthing);
        }
        catch (Exception error)
        {
            // A component that is absent on a type that has no such behaviour is a skip, not a failure; a native
            // getter that throws is reported and leaves the enemy as the prefab authored it.
            if (Diagnose(typeId))
                ReportQuietly("enemy profile not applied to enemy type " + typeId.ToString(CultureInfo.InvariantCulture)
                    + ": " + error.GetType().Name);
            return;
        }
        if (missing.Count != 0 && Diagnose(typeId))
            ReportQuietly("enemy profile names limbs enemy type " + typeId.ToString(CultureInfo.InvariantCulture)
                + " does not declare: " + string.Join(",", missing));
    }

    /// <summary>True the first time an enemy type has something to report, and never again: the answer cannot
    /// change during a session, so a repeated sentence is noise. The cap keeps a pathological installation from
    /// turning one spawn into unbounded logging.</summary>
    private bool Diagnose(uint typeId)
    {
        if (_profiledTypes.Contains(typeId)) return false;
        if (_profiledTypes.Count >= MaximumProfileDiagnostics)
        {
            if (!_profileDiagnosticsTruncated)
            {
                _profileDiagnosticsTruncated = true;
                ReportQuietly("further enemy profile diagnostics suppressed after " + MaximumProfileDiagnostics + " enemy types.");
            }
            return false;
        }
        _profiledTypes.Add(typeId);
        return true;
    }

    /// <summary>
    /// Every profile diagnostic goes through here. The callback is the host's logger, and both of this half's
    /// entry points sit in game-driven paths: <see cref="LoadProfiles"/> runs while the plugin loads and
    /// <see cref="ApplyProfiles"/> runs inside the native spawn hook. A logger that throws must not be able to
    /// abort a package load or a spawn, so the report is best-effort and the data layer's own answer — refuse, or
    /// write — is the only thing the rest of the code acts on.
    /// </summary>
    private void ReportQuietly(string message)
    {
        try { _report(message); }
        catch (Exception) { }
    }
}
