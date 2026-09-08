using System;
using System.Collections.Generic;

namespace InfiniTweaks;

internal static class BoosterRollRules
{
    internal readonly record struct Roll(uint Id, float Best);

    // RandomEffects describes slots of alternatives, not alternative whole
    // boosters. Match one existing effect per nonempty slot, without adding any.
    internal static bool TryMatch(uint[] ids, IReadOnlyList<Roll> fixedEffects, IReadOnlyList<Roll[]> slots, out float[] values)
    {
        var result = new float[ids.Length];
        var used = new bool[ids.Length];
        int Find(uint id)
        {
            for (int i = 0; i < ids.Length; i++) if (!used[i] && ids[i] == id) return i;
            return -1;
        }
        foreach (var effect in fixedEffects)
        {
            int index = Find(effect.Id);
            if (index < 0 || !float.IsFinite(effect.Best)) { values = Array.Empty<float>(); return false; }
            used[index] = true; result[index] = effect.Best;
        }
        bool MatchSlot(int slot)
        {
            if (slot == slots.Count) return Array.TrueForAll(used, value => value);
            if (slots[slot].Length == 0) return MatchSlot(slot + 1);
            foreach (var option in slots[slot])
            {
                int index = Find(option.Id);
                if (index < 0 || !float.IsFinite(option.Best)) continue;
                used[index] = true; result[index] = option.Best;
                if (MatchSlot(slot + 1)) return true;
                used[index] = false;
            }
            return false;
        }
        bool matched = MatchSlot(0);
        values = matched ? result : Array.Empty<float>();
        return matched;
    }
}
