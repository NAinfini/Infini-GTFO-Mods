using System;

namespace InfiniTweaks;

// Pure rules shared by the native hooks and offline regression checks.
internal static class CasualRules
{
    internal static int Reward(int earned, float multiplier) => earned <= 0 || !float.IsFinite(multiplier) || multiplier < 1 ? earned : (int)Math.Max(earned, Math.Min(100000d, Math.Floor(earned * (double)multiplier)));
    internal static bool ShowMarker(bool placed, bool sameDimension, bool aiming, bool hideAiming, float distanceSquared, float maxDistance) => placed && sameDimension && !(aiming && hideAiming) && distanceSquared <= maxDistance * maxDistance;
}
