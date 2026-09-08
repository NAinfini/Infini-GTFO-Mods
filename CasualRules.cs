using System;

namespace InfiniTweaks;

// Pure rules shared by the native hooks and offline regression checks.
internal static class CasualRules
{
    internal static bool TryMerge(float held, float world, float cap, out float carried, out float remaining)
    {
        carried = held;
        remaining = world;
        if (!float.IsFinite(held) || !float.IsFinite(world) || !float.IsFinite(cap) || held < 0 || world <= 0 || cap <= 0 || held >= cap) return false;
        double total = (double)held + world;
        carried = (float)Math.Min(total, cap);
        remaining = total <= cap ? 0 : (float)(total - cap);
        return carried > held;
    }

    internal static int Reward(int earned, float multiplier) => earned <= 0 || !float.IsFinite(multiplier) || multiplier < 1 ? earned : (int)Math.Max(earned, Math.Min(100000d, Math.Floor(earned * (double)multiplier)));
    internal static bool ShowMarker(bool placed, bool sameDimension, bool aiming, bool hideAiming, float distanceSquared, float maxDistance) => placed && sameDimension && !(aiming && hideAiming) && distanceSquared <= maxDistance * maxDistance;
    internal static float EffectiveDamage(float before, float after) => float.IsFinite(before) && float.IsFinite(after) ? Math.Max(0f, Math.Min(before, before - after)) : 0;
    internal static string Accuracy(long fired, long hits) => fired == 0 ? "—" : $"{100d * Math.Min(fired, hits) / fired:0.0}%";
}
