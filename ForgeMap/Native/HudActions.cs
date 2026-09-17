using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;
using TMPro;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The HUD half of the presentation tier (`a-hud`): one value, drawn on the addressed client's own screen, at
/// the placement and for the audience the author picked. Everything here is local by construction — the game's
/// own status readout, its shield bar and its text fields live on the local player's GUI layer
/// (`GuiManager.m_playerLayer`), and a teammate's overhead line is built by each machine for itself — so this
/// class writes no world state, publishes no fact and answers with the tier's own non-committing result.
///
/// Both placements are the game's own readouts. `status_bar` writes the game's own local player shield readout,
/// which is the readout the checklist's own example ("护盾 45 / 100" beside the health bar) asks for.
/// `teammate_overhead` writes the extra line <see cref="TeammateOverhead"/> keeps under a teammate's name marker,
/// which the game re-renders from <see cref="TeammateOverheadHooks"/>. Nothing here invents a readout of its own.
///
/// `audience=self` means "the player this value belongs to, and nobody else", so the value's own owner travels
/// with the command as its session and this machine draws only when that session is the one sitting here. That is
/// the same comparison the network layer already makes before it hands a presentation request to this handler, and
/// it is made again here because the two are different facts: the request was addressed correctly, and the number
/// on this screen is this player's own. The combination `self` with `teammate_overhead` is refused by name — the
/// local player has no overhead marker — rather than drawn on somebody else's head.
///
/// Every object this class creates is owned by it: a readout is hidden when the plan hides it and released when
/// the world the readout belonged to is replaced. The world epoch is the identity that decides the last case, read
/// from every command the kernel dispatches.
/// </summary>
internal sealed class HudActions
{
    internal const string PlacementCode = "hud-placement-unsupported";
    internal const string FormCode = "hud-form-unsupported";
    internal const string AudienceCode = "hud-audience-unsupported";
    internal const string AddressCode = "hud-audience-not-addressed";
    internal const string SelfOverheadCode = "hud-self-overhead-unsupported";
    internal const string TargetCode = "hud-target-unsupported";
    internal const string ValueCode = "hud-value-required";
    internal const string VisibleCode = "hud-visible-required";
    internal const string ColorCode = "hud-color-invalid";
    internal const string LabelCode = "hud-label-too-long";
    internal const string ViewersCode = "viewers-required";
    internal const string ViewerCode = "viewers-unsupported";
    internal const string NoLayerCode = "hud-layer-unavailable";
    internal const int MaximumLabelLength = 64;

    private readonly Action<string> _report;
    private long _epoch;
    private bool _epochKnown;

    internal HudActions(Action<string> report) => _report = report ?? throw new ArgumentNullException(nameof(report));

    internal CommandResult HandleValue(CommandContext context) => Value(context);

    /// <summary>The `forge.action.presentation.hud_value` command, dispatched by the kernel with no session: the
    /// whole presentation tier is addressed by the network layer, so a caller that already knows which player the
    /// value belongs to hands that session in. `audience=team` needs none; `audience=self` is refused without
    /// one, because a value that belongs to nobody is not a value this machine may draw.</summary>
    internal CommandResult HandleValue(CommandContext context, string? session) => Value(context, session);

    /// <summary>The `forge.action.presentation.hud_value` command.</summary>
    internal CommandResult Value(CommandContext context) => Value(context, null);

    /// <summary>The `forge.action.presentation.hud_value` command. A caller that already knows which player the
    /// value belongs to hands that session in; the kernel's own dispatch passes none. `audience=team` needs none,
    /// and `audience=self` is refused without one, because a value that belongs to nobody is not a value this
    /// machine may draw.</summary>
    internal CommandResult Value(CommandContext context, string? session)
    {
        DropForNewWorld(context.WorldEpoch);
        if (!Audience(context, out var viewers, out var refusal)) return refusal;
        if (!context.Inputs.TryGetProperty("value", out var valuePort)
            || valuePort.ValueKind != JsonValueKind.Number || !valuePort.TryGetDouble(out double value)
            || !double.IsFinite(value)) return CommandResult.Rejected(ValueCode);
        if (!context.Inputs.TryGetProperty("visible", out var visiblePort)
            || visiblePort.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return CommandResult.Rejected(VisibleCode);
        bool visible = visiblePort.ValueKind == JsonValueKind.True;
        string? label = EnvironmentActions.Text(context.Inputs, "label");
        if (label is { Length: > MaximumLabelLength }) return CommandResult.Rejected(LabelCode);
        double maximum = EnvironmentActions.Number(context.Inputs, "maximum", 0);

        string? placement = EnvironmentActions.Text(context.Parameters, "placement");
        if (placement != "status_bar" && placement != "teammate_overhead")
            return CommandResult.Rejected(PlacementCode);
        string? form = EnvironmentActions.Text(context.Parameters, "form");
        if (form == null || Array.IndexOf(HudContract.Forms, form) < 0) return CommandResult.Rejected(FormCode);
        if (form == "bar" && placement != "status_bar") return CommandResult.Rejected(FormCode);
        string? audience = EnvironmentActions.Text(context.Parameters, "audience");
        if (audience == "self" && placement == "teammate_overhead") return CommandResult.Rejected(SelfOverheadCode);
        if (audience == "self")
        {
            // This value belongs to one player, and that player's own session has to be the one this command was
            // addressed to. The addressed session is carried by the request; the one this machine is sitting at is
            // the game's own local session.
            if (string.IsNullOrEmpty(session)) return CommandResult.Rejected(AddressCode);
            if (!string.Equals(session, LocalSession(), StringComparison.Ordinal))
                return CommandResult.Rejected(AddressCode);
        }
        else if (audience != "team") return CommandResult.Rejected(AudienceCode);
        if (!TryColor(EnvironmentActions.Text(context.Parameters, "color"), out var color))
            return CommandResult.Rejected(ColorCode);

        var status = GuiManager.Current is { } gui ? gui.m_playerLayer?.m_playerStatus : null;
        if (status == null) return CommandResult.Rejected(NoLayerCode);
        string line = Text(form, value, maximum, label);

        try
        {
            if (placement == "teammate_overhead") Overhead(viewers, line, color, visible);
            else Status(status, form, value, maximum, label, color, visible);
            return Presented();
        }
        catch (Exception error)
        {
            Report("map.hud-value-failed: " + error.GetType().Name + ": " + error.Message);
            return CommandResult.Create(CommandStatuses.Failed, CommitStates.None,
                "hud-write-exception", "", RuntimeJson.EmptyObject);
        }
    }

    /// <summary>The status bar's own readout, which is the game's. Its bar is always given the value — that is the
    /// game's own normalisation and it is what makes `bar` a form — and the text forms additionally write the one
    /// string that reads the value the way the author asked for.</summary>
    private static void Status(PUI_LocalPlayerStatus status, string form, double value, double maximum,
        string? label, Color? color, bool visible)
    {
        if (status.m_shieldUIParent != null) status.m_shieldUIParent.SetActive(visible);
        if (!visible) return;
        status.UpdateShield((float)value);
        if (form == "bar") return;
        SetText(status.m_shieldText, Text(form, value, maximum, label), color);
    }

    /// <summary>The overhead placement: one line under the named teammates' own name markers. The teammates are
    /// the ones the request addressed, because that is the only place a per-teammate value can come from; a
    /// teammate whose marker the game has not built yet keeps the value until it is.</summary>
    private void Overhead(IReadOnlyList<EntityReference> viewers, string line, Color? color, bool visible)
    {
        foreach (var viewer in viewers)
        {
            if (!Target(viewer, out IntPtr teammate, out var marker)) continue;
            TeammateOverhead.Show(teammate, marker, visible ? line : "", visible ? color : null);
        }
    }

    /// <summary>The teammate one viewer reference names, and that teammate's own marker when the game has already
    /// built it. The pointer comes from the identity module's recorded life and the marker from the agent that life
    /// resolves to, so the line is addressed to a player this process really tracks rather than to an id somebody
    /// wrote down. A reference this process cannot name is skipped: with a team audience the other teammates are
    /// still drawn, and the one nobody can name is exactly the one with no head to draw over.</summary>
    private static bool Target(EntityReference reference, out IntPtr teammate, out TeammateOverhead.IMarker? marker)
    {
        teammate = IntPtr.Zero;
        marker = null;
        if (reference.Id == null || !reference.Id.StartsWith(PlayerIdentityModule.EntityKind + ":", StringComparison.Ordinal))
            return false;
        if (PlayerIdentityModule.Current?.CurrentAgent(reference) is not { } agent) return false;
        var owner = agent.Owner;
        if (owner == null || owner.Pointer == IntPtr.Zero) return false;
        teammate = owner.Pointer;
        var nav = agent.NavMarker;
        if (nav != null && nav.Pointer != IntPtr.Zero) marker = new TeammateOverhead.NativeMarker(nav);
        return true;
    }

    /// <summary>The one line a value is rendered as. `number_of_max` and `percent` need the maximum the plan
    /// supplied; a plan that asked for either without one gets the number alone rather than a division by a
    /// maximum nobody named.</summary>
    internal static string Text(string form, double value, double maximum, string? label)
    {
        string body = form switch
        {
            "number_of_max" => maximum > 0
                ? value.ToString("0.##", CultureInfo.InvariantCulture) + " / " + maximum.ToString("0.##", CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture),
            "percent" => maximum > 0
                ? Math.Round(value / maximum * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                : value.ToString("0.##", CultureInfo.InvariantCulture),
            _ => value.ToString("0.##", CultureInfo.InvariantCulture)
        };
        return string.IsNullOrEmpty(label) ? body : label + " " + body;
    }

    private static void SetText(TextMeshPro? text, string body, Color? color)
    {
        if (text == null) return;
        text.SetText(body);
        if (color is { } value) text.color = value;
    }

    /// <summary>The overhead lines are the one shape this class does not own alone — each of them holds the
    /// game's own extra-information row — so a world the kernel has moved past releases them through the model
    /// that saved that row's visibility.</summary>
    private void DropForNewWorld(long epoch)
    {
        if (_epochKnown && _epoch == epoch) return;
        _epoch = epoch;
        _epochKnown = true;
        TeammateOverhead.Clear();
    }

    /// <summary>The address of the player sitting at this machine, or null while the game has no local player. It
    /// is the same value the network layer compares an inbound presentation request's address against: the game's
    /// own player slot, read here the way `PlayerSessions` reads it, never the account id `Lookup` carries.</summary>
    private static string? LocalSession()
    {
        var local = SNetwork.SNet.LocalPlayer;
        if (!SNetwork.SNet.HasLocalPlayer || local == null) return null;
        try { return local.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture); }
        catch (Exception) { return null; }
    }

    /// <summary>The audience a presentation request declares, checked exactly as the other presentation rows
    /// check it: the port is the kernel's routing list, and what this layer verifies is that it named players.
    /// The references are handed back because the overhead placement draws one line per teammate, so the list is
    /// read here rather than parsed a second time inside that placement.</summary>
    private static bool Audience(CommandContext context, out IReadOnlyList<EntityReference> viewers, out CommandResult refusal)
    {
        viewers = Array.Empty<EntityReference>();
        refusal = CommandResult.Rejected(ViewersCode);
        if (!context.Inputs.TryGetProperty("viewers", out var value) || value.ValueKind != JsonValueKind.Array)
            return false;
        string prefix = PlayerIdentityModule.EntityKind + ":";
        var named = new List<EntityReference>();
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
            named.Add(reference);
        }
        if (named.Count == 0) return false;
        viewers = named;
        return true;
    }

    /// <summary>One `#rrggbb` colour. Absent means "leave the game's own colour alone"; a present value that is
    /// not that grammar is refused rather than parsed into a colour nobody asked for.</summary>
    internal static bool TryColor(string? text, out Color? color)
    {
        color = null;
        if (string.IsNullOrEmpty(text)) return true;
        string hex = text.StartsWith("#", StringComparison.Ordinal) ? text.Substring(1) : text;
        if (hex.Length != 6) return false;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed)) return false;
        color = new Color(((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f, 1f);
        return true;
    }

    private static CommandResult Presented() => CommandResult.Create(CommandStatuses.Succeeded, CommitStates.None,
        "hud-value-presented", "", RuntimeJson.EmptyObject);

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception) { /* a reporter that throws must not replace the outcome it reports */ }
    }
}
