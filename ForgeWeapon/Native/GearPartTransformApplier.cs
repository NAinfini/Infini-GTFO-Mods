using System;
using System.Collections.Generic;
using System.Globalization;
using Gear;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>Applies authored gear-part poses to a part holder the game has just finished spawning.
///
/// This is the one place in the package that writes native state, and what it writes is the presentation layer
/// only: the absolute local transform of a part and its children, and whether that part is shown. It publishes no
/// fact, sends nothing over the network and never touches game state, so the effect of one package is the same on
/// every machine that has the same package — each client poses the models it can see (first person, the third
/// person copy of another player's gear, the menu preview, an icon render and a deployed sentry all assemble
/// their own holder through the same native path).
///
/// Every value is absolute, which makes the write idempotent: a holder that spawns again, an icon that re-renders
/// or a checkpoint reload all read the same authored numbers instead of accumulating an offset.</summary>
internal sealed class GearPartTransformApplier
{
    private readonly GearPartTransformSnapshot _snapshot;
    private readonly Action<string> _report;
    private readonly Action<string> _info;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    internal GearPartTransformApplier(GearPartTransformSnapshot snapshot, Action<string> report, Action<string> info)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>Authored blocks this session will ever apply, so a test can tell an accepted file from a rejected
    /// one without reading the log.</summary>
    internal int BlockCount => _snapshot.BlockCount;

    /// <summary>One holder finished spawning. Nothing is read here but the gear's own block record: a block with
    /// no authored file is left exactly as the game built it.</summary>
    internal void Apply(GearPartHolder? holder)
    {
        if (holder == null) return;
        var blockId = EquipmentNativeAdapter.GearBlockId(holder.GearIDRange);
        if (blockId == null || !_snapshot.TryGet(blockId, out var block)) return;
        foreach (var part in block.Parts)
        {
            var instance = Part(holder, part.Slot);
            if (instance == null) continue;
            // The author's part id is a check, not a key: a slot holding a different part block keeps the game's
            // own pose, and the refusal is reported once per authored entry rather than once per spawn.
            if (part.PartId != null && !MatchesPartId(holder, part))
            {
                Once("part:" + Number((byte)part.Slot), "weapon.gear-part-mismatch: block=" + blockId + " component=" + part.Slot
                    + " expected=" + Number(part.PartId.Value) + " actual=" + Number(holder.GearIDRange!.GetCompID((eGearComponent)part.Slot)));
                continue;
            }
            Write(instance.transform, part.Enabled, part.Position, part.EulerAngles, part.Scale);
            ApplyChildren(instance.transform, part.Slot, blockId, part.Children);
        }
        _info("weapon.gear-part-applied block=" + blockId + " parts=" + Number(block.Parts.Length));
    }

    /// <summary>Applies the whole authored child tree, not just its first level: a child entry may carry `children`
    /// of its own, and every node carries the full path from the part, so each one is resolved against the part's
    /// own transform — the hierarchy lookup shape the game itself understands. A path that is not there is reported
    /// once and its own subtree is dropped with it: nothing below a missing node can resolve, so the highest missing
    /// node is the one diagnostic that branch gets.</summary>
    private void ApplyChildren(Transform part, GearPartSlot slot, string blockId, GearPartChild[] children)
    {
        foreach (var child in children)
        {
            var target = part.Find(child.Path);
            if (target == null)
            {
                Once("child:" + Number((byte)slot) + ":" + child.Path, "weapon.gear-part-child-missing: block=" + blockId
                    + " component=" + slot + " path=" + child.Path);
                continue;
            }
            Write(target, child.Node.Enabled, child.Node.Position, child.Node.EulerAngles, child.Node.Scale);
            ApplyChildren(part, slot, blockId, child.Node.Children);
        }
    }

    /// <summary>Whether the slot really holds the authored part block. The expected value is the id the catalog
    /// writes and `GearIDRange.GetCompID` is the game's own per-component readback of it; an unreadable or
    /// different value refuses the whole entry.</summary>
    private static bool MatchesPartId(GearPartHolder holder, GearPartTransform part)
        => holder.GearIDRange != null && holder.GearIDRange.GetCompID((eGearComponent)part.Slot) == part.PartId!.Value;

    /// <summary>Absolute local values, written only where the file said something: an omitted field keeps the
    /// pose the game built instead of being reset to a default the author never asked for.</summary>
    private static void Write(Transform transform, bool? enabled, Vector3? position, Vector3? eulerAngles, Vector3? scale)
    {
        if (enabled != null) transform.gameObject.SetActive(enabled.Value);
        if (position != null) transform.localPosition = position.Value;
        if (eulerAngles != null) transform.localEulerAngles = eulerAngles.Value;
        if (scale != null) transform.localScale = scale.Value;
    }

    /// <summary>The slot-to-property table, spelled out: the game's `eGearComponent` member to the holder field
    /// that holds that part. No name is ever reflected on, so a renamed field is a compile error, not a silent
    /// no-op at runtime.</summary>
    private static GameObject? Part(GearPartHolder holder, GearPartSlot slot) => slot switch
    {
        GearPartSlot.FrontPart => holder.FrontPart,
        GearPartSlot.FrontPartAttachmentA => holder.FrontPartAttachmentA,
        GearPartSlot.FrontPartAttachmentB => holder.FrontPartAttachmentB,
        GearPartSlot.ReceiverPart => holder.ReceiverPart,
        GearPartSlot.ReceiverPartAttachment => holder.ReceiverPartAttachment,
        GearPartSlot.StockPart => holder.StockPart,
        GearPartSlot.SightPart => holder.SightPart,
        GearPartSlot.MagPart => holder.MagPart,
        GearPartSlot.FlashlightPart => holder.FlashlightPart,
        GearPartSlot.ToolMainPart => holder.ToolMainPart,
        GearPartSlot.ToolMainPartAttachment => holder.ToolMainPartAttachment,
        GearPartSlot.ToolGripPart => holder.ToolGripPart,
        GearPartSlot.ToolDeliveryPart => holder.ToolDeliveryPart,
        GearPartSlot.ToolDeliveryPartAttachment => holder.ToolDeliveryPartAttachment,
        GearPartSlot.ToolPayloadPart => holder.ToolPayloadPart,
        GearPartSlot.ToolTargetingPart => holder.ToolTargetingPart,
        GearPartSlot.ToolScreenPart => holder.ToolScreenPart,
        GearPartSlot.MeleeHeadPart => holder.MeleeHeadPart,
        GearPartSlot.MeleeNeckPart => holder.MeleeNeckPart,
        GearPartSlot.MeleeHandlePart => holder.MeleeHandlePart,
        GearPartSlot.MeleePommelPart => holder.MeleePommelPart,
        _ => null
    };

    // A holder spawns again on every view change and every icon render, so a refusal that repeated would flood the
    // log; one line per authored entry, and one per child path, is the whole diagnostic budget for it.
    private void Once(string key, string message)
    {
        if (!_reported.Add(key)) return;
        _report(message);
    }

    private static string Number(byte value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
