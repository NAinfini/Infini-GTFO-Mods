using System;
using System.Collections.Generic;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// One colour and intensity transition over the light objects one request named, and the registry that drives the
/// transitions in flight.
///
/// The game has no gradient entry for a light: `LG_Light` carries `ChangeColor`/`ChangeIntensity` and a
/// `GetIntensity` read, and every vanilla colour scheme is applied in one step by `LG_BuildZoneLightsJob`. A
/// transition therefore has to be written frame by frame by this package, which is what this file is — the one
/// place the mod holds light state of its own, and it holds it only for as long as a transition lasts.
///
/// A transition's start values are sampled when it is scheduled and never when it was first written, so a request
/// that arrives while another is still running continues from the colour and intensity on the light and the two
/// never fight over it: the newer request replaces the older one for its own zone and category, and the older one
/// is dropped rather than left to drag the light to a value nobody asked for any more.
///
/// `brightness` is a multiplier of the light's own intensity at the moment the request arrives, which is also what
/// the level's own `IntensityMul` means; because a repeated request starts from the value on the light, two
/// requests with the same multiplier compound, and a request that only changes colour passes no brightness at all.
/// </summary>
internal sealed class LightColorFade
{
    /// <summary>One zone and one light category a transition drives. It is the registry's key because it is also
    /// what "the same request again" means: a second transition for the same zone and category replaces the first,
    /// and a transition over a different category of the same zone is a request about other lights and leaves it
    /// running.</summary>
    internal readonly struct Key : IEquatable<Key>
    {
        internal Key(int dimension, int layer, int zone, int category)
        { Dimension = dimension; Layer = layer; Zone = zone; Category = category; }

        internal int Dimension { get; }
        internal int Layer { get; }
        internal int Zone { get; }
        /// <summary>The `LG_Light.LightCategory` the transition is limited to, or <see cref="EveryCategory"/>.</summary>
        internal int Category { get; }

        public bool Equals(Key other) => Dimension == other.Dimension && Layer == other.Layer
            && Zone == other.Zone && Category == other.Category;
        public override bool Equals(object? other) => other is Key key && Equals(key);
        public override int GetHashCode() => HashCode.Combine(Dimension, Layer, Zone, Category);
    }

    /// <summary>The key member that means "the request named no category", so every light of the zone is driven.</summary>
    internal const int EveryCategory = -1;

    private sealed class Target
    {
        internal LG_Light Light = null!;
        internal Color FromColor;
        internal Color ToColor;
        internal float FromIntensity;
        internal float ToIntensity;
    }

    private readonly List<Target> _targets;
    private readonly float _seconds;
    private float _elapsed;

    private LightColorFade(List<Target> targets, float seconds)
    { _targets = targets; _seconds = seconds; }

    /// <summary>Whether the transition has written its last value. A transition of no length is applied when it is
    /// scheduled and answered as already done, which is how `transition: 0` is the write itself.</summary>
    internal bool Done => _elapsed >= _seconds;

    /// <summary>How many light objects the transition drives, which is what a caller reports as the work a request
    /// asked for. A request that matched no light is refused rather than answered as a transition over none.</summary>
    internal int Lights => _targets.Count;

    /// <summary>The transition one request asks for over the lights it was handed, or null when those lights carry
    /// nothing the request changes: a category no light of the zone has, or an empty zone. A transition of no
    /// length is applied here and answered as null, because there is no state left to drive.
    ///
    /// The start values are the light's own colour and intensity now — which for a light a previous transition is
    /// still driving is the value it currently shows, so a request that arrives mid-transition continues from
    /// there instead of jumping back to the value that transition started from.</summary>
    internal static LightColorFade? Between(IReadOnlyList<LG_Light> lights, Color? color, float? brightness,
        int category, float seconds)
    {
        if (color == null && brightness == null) return null;
        var targets = new List<Target>(lights.Count);
        foreach (var light in lights)
        {
            if (light == null) continue;
            if (category != EveryCategory && (int)light.m_category != category) continue;
            float intensity = light.GetIntensity();
            var current = light.m_color;
            targets.Add(new Target
            {
                Light = light,
                FromColor = current,
                ToColor = color ?? current,
                FromIntensity = intensity,
                ToIntensity = brightness is { } multiplier ? intensity * multiplier : intensity
            });
        }
        if (targets.Count == 0) return null;
        var fade = new LightColorFade(targets, Math.Max(0f, seconds));
        // A transition with a length is driven by the frames that follow and writes nothing here; one without a
        // length has no frames of its own, so the value it asked for is written now and it is done.
        if (!fade.Done) return fade;
        fade.Write(1f);
        return null;
    }

    /// <summary>One frame of the transition. A frame that arrives after the last one writes nothing, so a caller
    /// that ticks a transition it already finished cannot restart it.</summary>
    internal void Advance(float deltaSeconds)
    {
        if (Done) return;
        _elapsed += Math.Max(0f, deltaSeconds);
        Write(_seconds <= 0f ? 1f : Math.Min(1f, _elapsed / _seconds));
    }

    /// <summary>Writes the transition's value at one point of its own length, linearly. Nothing here reads the
    /// clock: the caller owns the frame and the seconds it stands for.</summary>
    private void Write(float progress)
    {
        foreach (var target in _targets)
        {
            target.Light.ChangeColor(Color.Lerp(target.FromColor, target.ToColor, progress));
            target.Light.ChangeIntensity(target.FromIntensity + (target.ToIntensity - target.FromIntensity) * progress);
        }
    }
}

/// <summary>
/// The transitions one world has in flight, keyed by the zone and category each is about. There is no ticker
/// here: the frame is the game's own, and the one patch that owns it calls <see cref="Tick"/>.
///
/// The world the transitions belong to is carried with them, because a light object does not survive the level it
/// was built in: a tick from a later world drops the whole table instead of reaching into objects the level
/// teardown already took, which is the one failure a frame-by-frame write could otherwise walk into.
/// </summary>
internal static class LightColorFades
{
    private static readonly Dictionary<LightColorFade.Key, LightColorFade> Active = new();
    private static long _world;

    /// <summary>How many transitions are in flight. It is what a caller reads to see the table without walking it,
    /// and it is the whole state this package keeps between frames.</summary>
    internal static int Count => Active.Count;

    /// <summary>Puts one transition under its key, replacing whatever the key already held — which is exactly the
    /// "the same request again" case: the new transition already sampled the lights' current values, so the one it
    /// replaces would only drag them back. A transition over no light is not scheduled at all.
    ///
    /// The first schedule of a world adopts that world and drops anything an earlier one left behind.</summary>
    internal static void Schedule(long world, LightColorFade.Key key, LightColorFade? fade)
    {
        if (fade == null) return;
        if (world != _world) { Active.Clear(); _world = world; }
        Active[key] = fade;
    }

    /// <summary>One frame for every transition of the given world. A tick from another world is the level teardown
    /// between them, so the table is dropped rather than advanced: its lights are not the standing level's.</summary>
    internal static void Tick(long world, float deltaSeconds)
    {
        if (Active.Count == 0) return;
        if (world != _world) { Clear(); return; }
        List<LightColorFade.Key>? finished = null;
        foreach (var entry in Active)
        {
            // A light the level took away throws on the next write; the entry is dropped rather than retried, and
            // the other transitions of the same frame are untouched by it.
            try { entry.Value.Advance(deltaSeconds); }
            catch (Exception) { (finished ??= new List<LightColorFade.Key>()).Add(entry.Key); continue; }
            if (entry.Value.Done) (finished ??= new List<LightColorFade.Key>()).Add(entry.Key);
        }
        if (finished == null) return;
        foreach (var key in finished) Active.Remove(key);
    }

    /// <summary>Drops every transition. The session calls it when it goes away, so a patch that still runs for a
    /// frame after teardown has nothing to write into.</summary>
    internal static void Clear()
    {
        Active.Clear();
        _world = 0;
    }
}
