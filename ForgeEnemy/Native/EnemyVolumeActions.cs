using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enemies;
using ForgeRuntime.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The one action of the effect-volume family: `forge.action.combat.effect_volume`, the native volume
/// `EffectVolumeManager` keeps registered against the sphere the game's own `EV_Sphere` describes.
///
/// Each submission does two native things and reports both:
/// <list type="number">
/// <item>It builds an `EV_Sphere` at the anchor's own position, fills in the four choices the row carries —
/// `contents` and `modification` are the game's own `eEffectVolumeContents` and `eEffectVolumeModification`,
/// `radius_min`/`radius_max` are the two distances `ComputeAmount` interpolates between, and `scale` is
/// `modificationScale` — and registers it with `EffectVolumeManager.RegisterVolume`. That registration is what
/// makes the volume real: the manager's own update loop asks every registered volume what it does to every
/// registered target, and the target's `ReceiveModification` applies it. Nothing in this package writes a
/// target's health or infection itself.</item>
/// <item>It allocates the game's own fog sphere (`FogSphereAllocator`) at the same position and outer radius, so
/// the volume has a visible body. That half is presentation: the game's own sphere budget can be full, and a
/// submission whose volume registered is still committed when it did. The result's own `code` column carries
/// which of the two happened, so an author can see that a volume is running without being drawn.</item>
/// </list>
///
/// A volume with `follow_anchor` is moved to its anchor's own position by the frame pump that already runs for
/// the tag, and every volume one dispatch placed is unregistered — with its fog sphere returned — when the effect
/// that dispatch applied ends. The ledger is the record of what is still registered, so a package that unloads
/// mid-level releases every volume it placed.
///
/// The row carries no lifetime port: the step's own `effect` block is the clock (I-PLAN §3.4), the kernel casts
/// the handle, and `RestoreVolume` is the callback it calls when that effect ends — by its duration, by a
/// cancellation, by a released plan or by the world. A volume is filed under the kernel's own handle for the
/// step, which is also what the row publishes as `volumes` when the card asked for a cancel handle; a card with
/// no `effect` block names no handle, and its volumes live until the module releases them. Every request is
/// checked before the first anchor, because a submission that cannot be carried out as asked must not
/// half-apply.</summary>
internal sealed partial class EnemyModule
{
    internal const string VolumeBinding = EnemyVolumeContract.VolumeBinding;
    internal const string VolumeHandler = EnemyVolumeContract.VolumeHandler;

    /// <summary>The volume handler's ports, resolved at registration against the capability row in
    /// `EnemyVolumeContract`.</summary>
    internal static readonly HandlerShape VolumePorts = EnemyVolumeContract.Shape();

    /// <summary>The bounds the four numeric choices are refused outside of. The radius bound is a kilometre, far
    /// past any level's own extent, and exists so a value the sphere's own arithmetic cannot represent is refused
    /// rather than kept.</summary>
    private const double MinimumVolumeScale = 0.0, MaximumVolumeScale = 1000.0;
    private const double MinimumVolumeRadius = 0.0, MaximumVolumeRadius = 1000.0;

    /// <summary>The density the fog sphere is allocated with, which is the game's own scream-pop density
    /// (`ES_ScoutScream.SCREAM_POP_DENSITY`). The row carries no density parameter, so it takes the value the
    /// game's own visible volume uses rather than inventing a second one.</summary>
    private const float VolumeFogDensity = 0.5f;
    /// <summary>The radiance the fog sphere is allocated with. The row carries no colour parameter, so the volume
    /// is drawn in the neutral tone the catalog's own fog rows use rather than in a colour this row cannot
    /// explain.</summary>
    private static readonly Color VolumeFogRadiance = new(0.55f, 0.65f, 0.75f, 1f);
    private const float VolumeFogIntensity = 1f;

    /// <summary>One volume this provider has registered and not yet released: the anchor it follows, the sphere
    /// the manager holds, the fog sphere allocated for it (absent when the game's budget had no room), the kernel
    /// effect handle it is released through (absent for a card that applied no effect), and whether it tracks its
    /// anchor.</summary>
    private sealed record VolumeRecord(EntityReference Anchor, IntPtr EnemyPointer, object Volume,
        FogSphereAllocator? Fog, string? HandleKey, bool Follow)
    {
        /// <summary>The anchor's own position, written back into the sphere the manager already holds. A record
        /// that does not follow its anchor is never asked for one.</summary>
        internal void MoveTo(Vector3 position)
        {
            if (Volume is EV_Sphere sphere) sphere.position = position;
            if (Fog != null) Fog.SetPositionRange(position, FogRange);
        }

        /// <summary>The outer radius the fog sphere was allocated with, read back from the sphere so a move
        /// reallocates it at the same size rather than at a second, remembered one.</summary>
        internal float FogRange { get; init; }
    }

    /// <summary>One volume this provider has placed and not yet released, and the kernel handle it is filed
    /// under: `null` for a card whose step carried no effect, so the volume lives until the module lets it go. A
    /// handle is a string rather than the kernel's own value because the ledger only ever compares it, and a
    /// restore callback hands it back as the frame it was read from.</summary>
    private readonly List<VolumeRecord> _volumes = new();

    /// <summary>Every volume filed under one handle, released. Called by the kernel's own restore callback when
    /// the effect a dispatch applied ends; the entries go whether or not the native call answered, and a handle
    /// that names nothing — an effect the module already released, or one whose volumes all failed to place — is
    /// the no-op it has to be.</summary>
    internal void RestoreVolume(RuntimeEffectContext context)
    {
        if (context.Handle.ValueKind == JsonValueKind.Undefined) return;
        RemoveVolumes(context.Handle.GetRawText());
    }

    /// <summary>One anchor's line in the volume result, in the row order the schema declares: the four fixed
    /// columns first, then the count. A placement whose fog sphere the game's own budget refused keeps the same
    /// status and commit state as a drawn one — it is a committed volume — and says which of the two happened in
    /// the `code` column, because the row's schema has exactly those five columns and no sixth of its own.</summary>
    private sealed record VolumeRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>`forge.action.combat.effect_volume`. The request check runs before the first anchor, so a
    /// submission that cannot be carried out as asked writes nothing; every volume of one dispatch is filed under
    /// the kernel's handle for the step, which is what the effect's own ending calls the restore callback with.</summary>
    internal CommandResult EffectVolume(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        if (!TryContents(context.Parameters, out var contents)) return CommandResult.Rejected("contents-unknown");
        if (!TryModification(context.Parameters, out var modification)) return CommandResult.Rejected("modification-unknown");
        if (!TryScale(context.Parameters, out var scale)) return CommandResult.Rejected("scale-out-of-range");
        if (!TryRadius(context.Parameters, out var minRadius, out var maxRadius)) return CommandResult.Rejected("radius-out-of-range");
        if (!TryFollow(context.Parameters, out var follow)) return CommandResult.Rejected("follow-flag-invalid");
        var anchors = Entities(context.Inputs, "anchors");
        if (anchors.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");
        // The kernel cast this handle for the step before the handler ran, and it is what the kernel calls the
        // restore callback back with. A card with no `effect` block has none: the volumes are still placed and
        // stay in the ledger, which the module's own release and the world's end both drain.
        string? handleKey = context.EffectHandle is { } effectHandle ? effectHandle.GetRawText() : null;

        var rows = new List<VolumeRow>(anchors.Length);
        int placed = 0, failed = 0;
        foreach (var anchor in anchors)
        {
            VolumeRow Row(string status, string commitState, string code)
                => new(anchor, status, commitState, code, anchors.Length);
            var entry = Resolve(anchor);
            if (entry == null) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "stale-or-unsupported-recipient")); continue; }
            var enemy = entry.Enemy;
            Vector3 position;
            try
            {
                if (!enemy.Alive) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "not-alive")); continue; }
                position = enemy.Position;
            }
            catch (Exception) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "enemy-position-unavailable")); continue; }
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
            { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "enemy-position-unavailable")); continue; }

            var sphere = new EV_Sphere
            {
                position = position, minRadius = minRadius, maxRadius = maxRadius,
                contents = contents, modification = modification, modificationScale = scale,
                invert = false, effectOrder = 0
            };
            FogSphereAllocator? fog = null;
            bool allocated = false;
            try
            {
                // The fog sphere is allocated first, so a submission whose volume registration throws leaves no
                // sphere behind: the volume is what the row promises and the sphere is drawn for it, not instead
                // of it.
                fog = new FogSphereAllocator();
                fog.SetPositionRange(position, maxRadius);
                fog.SetDensity(VolumeFogDensity);
                fog.SetRadiance(VolumeFogRadiance, VolumeFogIntensity);
                allocated = fog.TryAllocate();
                EffectVolumeManager.RegisterVolume(sphere);
            }
            catch (Exception)
            {
                if (allocated && fog != null) DeallocateFog(fog);
                rows.Add(Row(CommandStatuses.Failed, CommitStates.None, "native-volume-exception"));
                failed++;
                continue;
            }
            // The manager now holds the sphere, so a receiver that changed under this dispatch would leave a
            // volume tracking an instance no plan can name any more.
            if (Resolve(anchor) == null || !ReferenceEquals(Resolve(anchor), entry))
            {
                try { EffectVolumeManager.UnregisterVolume(sphere); } catch (Exception) { }
                if (allocated && fog != null) DeallocateFog(fog);
                rows.Add(Row(CommandStatuses.Failed, CommitStates.None, "receiver-changed-during-placement"));
                failed++;
                continue;
            }
            // The volume is registered either way; the code says whether the fog sphere could be drawn for it,
            // which is the one thing about the submission the row's own five columns can still report.
            rows.Add(Row(CommandStatuses.Succeeded, CommitStates.Confirmed,
                allocated ? "volume-placed" : "volume-placed-undrawn"));
            placed++;
            _volumes.Add(new VolumeRecord(anchor, entry.EnemyPointer, sphere, allocated ? fog : null, handleKey, follow)
                { FogRange = maxRadius });
        }

        // A volume that is registered but whose fog sphere could not be allocated is still a committed volume, so
        // it counts as one; the aggregate only knows what was written. The `volumes` handle is the kernel's own,
        // published into that port when the card asked for a cancel handle, so the handler names nothing itself.
        return AggregateVolumeRows(placed, failed, rows.Count, rows, RuntimeJson.From(new { results = rows }));
    }

    /// <summary>One volume's end, in the same three shapes every other release in this package uses: the sphere
    /// left the manager's own list, or it was already gone, or the call could not be made.</summary>
    private static void DeallocateFog(FogSphereAllocator fog)
    {
        try { fog.Deallocate(); } catch (Exception) { }
    }

    /// <summary>The volume aggregate: everything placed is a success, nothing placed is a rejection or an
    /// unknown, and anything in between is partial — the same rule every multi-target action in this package
    /// uses, spelled once here because the row's own columns are its own.</summary>
    private CommandResult AggregateVolumeRows(int placed, int failed, int total, IReadOnlyList<VolumeRow> rows, JsonElement outputs)
    {
        if (placed == total) return CommandResult.Succeeded(outputs);
        if (placed > 0) return CommandResult.Partial(outputs, failed > 0 ? CommitStates.None : CommitStates.Confirmed);
        string code = total == 1 ? rows[0].Code : "effect_volume-all-rejected";
        if (failed == 0) return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.None, code, "", outputs);
    }

    /// <summary>The per-frame half of the volume ledger: every volume that follows its anchor is moved to where
    /// the anchor now is. The pump is the same one the tag and the markers already run on, so a level with no
    /// enemies registers no second per-frame hook, and a volume whose step carried no effect is still moved by it
    /// until the module lets it go.</summary>
    internal void PumpVolumes()
    {
        if (_volumes.Count == 0) return;
        for (int index = _volumes.Count - 1; index >= 0; index--)
        {
            var record = _volumes[index];
            if (!record.Follow) continue;
            // A follow whose anchor is gone keeps the volume where it last was: an anchor no plan can name is not
            // a position this pump may invent.
            var entry = Resolve(record.Anchor);
            if (entry == null || entry.EnemyPointer != record.EnemyPointer) continue;
            try
            {
                var position = entry.Enemy.Position;
                if (float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z))
                    record.MoveTo(position);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Every volume one handle placed, released. Called by the kernel's own restore callback when the
    /// effect that dispatch applied ends; the entries go whether or not the native call answered.</summary>
    internal void RemoveVolumes(string? handleKey)
    {
        if (handleKey == null) return;
        for (int index = _volumes.Count - 1; index >= 0; index--)
        {
            if (_volumes[index].HandleKey != handleKey) continue;
            ReleaseVolume(_volumes[index]);
            _volumes.RemoveAt(index);
        }
    }

    /// <summary>Drops every volume this package still holds, for the module that is going away. A package that
    /// unloads mid-level must not leave a volume registered with a manager that outlives it.</summary>
    internal void ReleaseVolumes()
    {
        for (int index = _volumes.Count - 1; index >= 0; index--) ReleaseVolume(_volumes[index]);
        _volumes.Clear();
    }

    /// <summary>The one native release. A manager that already dropped the sphere, or a fog sphere the game
    /// already collected, is not an error worth escaping into the kernel's own pump.</summary>
    private static void ReleaseVolume(VolumeRecord record)
    {
        if (record.Volume is EffectVolume volume) { try { EffectVolumeManager.UnregisterVolume(volume); } catch (Exception) { } }
        if (record.Fog != null) DeallocateFog(record.Fog);
    }

    /// <summary>The `contents` parameter as the game's own enum. A value outside the row's own vocabulary is
    /// refused by name rather than mapped onto a neighbouring member.</summary>
    private static bool TryContents(JsonElement parameters, out eEffectVolumeContents contents)
    {
        contents = eEffectVolumeContents.All;
        if (!parameters.TryGetProperty("contents", out var value) || value.ValueKind != JsonValueKind.String) return false;
        switch (value.GetString())
        {
            case "all": contents = eEffectVolumeContents.All; return true;
            case "health": contents = eEffectVolumeContents.Health; return true;
            case "infection": contents = eEffectVolumeContents.Infection; return true;
            default: return false;
        }
    }

    /// <summary>The `modification` parameter as the game's own enum.</summary>
    private static bool TryModification(JsonElement parameters, out eEffectVolumeModification modification)
    {
        modification = eEffectVolumeModification.Inflict;
        if (!parameters.TryGetProperty("modification", out var value) || value.ValueKind != JsonValueKind.String) return false;
        switch (value.GetString())
        {
            case "inflict": modification = eEffectVolumeModification.Inflict; return true;
            case "shield": modification = eEffectVolumeModification.Shield; return true;
            default: return false;
        }
    }

    private static bool TryScale(JsonElement parameters, out float scale)
    {
        scale = 0f;
        if (!TryNumber(parameters, "scale", out var number)) return false;
        if (number < MinimumVolumeScale || number > MaximumVolumeScale) return false;
        scale = (float)number;
        return true;
    }

    /// <summary>The two radii, in the one check that keeps them a sphere: the inner radius may not exceed the
    /// outer one, because the game's own answer at or beyond the outer radius is "no effect" and an inner radius
    /// past it would name an amount nothing could ever receive.</summary>
    private static bool TryRadius(JsonElement parameters, out float minRadius, out float maxRadius)
    {
        minRadius = 0f; maxRadius = 0f;
        if (!TryNumber(parameters, "radius_min", out var minimum)) return false;
        if (!TryNumber(parameters, "radius_max", out var maximum)) return false;
        if (minimum < MinimumVolumeRadius || maximum > MaximumVolumeRadius || minimum > maximum) return false;
        minRadius = (float)minimum; maxRadius = (float)maximum;
        return true;
    }

    /// <summary>The `follow_anchor` parameter. The port is optional — a card that says nothing about following
    /// asks for a volume that stays where it was placed — so only a value that is present and not a boolean is
    /// refused.</summary>
    private static bool TryFollow(JsonElement parameters, out bool follow)
    {
        follow = false;
        if (!parameters.TryGetProperty("follow_anchor", out var value)) return true;
        if (value.ValueKind == JsonValueKind.True) { follow = true; return true; }
        return value.ValueKind == JsonValueKind.False;
    }

    private static bool TryNumber(JsonElement parameters, string name, out double number)
    {
        number = 0;
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return false;
        return value.TryGetDouble(out number) && double.IsFinite(number);
    }
}
