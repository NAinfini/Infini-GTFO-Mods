using Gear;
using Il2CppList = Il2CppSystem.Collections.Generic.List<Gear.GearIDRange>;

namespace ForgeWeapon.Native;

/// <summary>The game's own `GearManager.m_gearPerSlot` list, seen through <see cref="IGearPoolSlot"/>. This is the
/// whole of the interop boundary for the pool narrowing: the loader hook builds one per covered slot over the list
/// instance the game owns, and everything past this point works in managed items. Reads and writes go straight to
/// that instance, so narrowing a slot rewrites the list the game and every other holder already have rather than
/// replacing it with one the game would never see.</summary>
internal sealed class Il2CppGearPoolSlot : IGearPoolSlot
{
    private readonly Il2CppList _live;

    internal Il2CppGearPoolSlot(Il2CppList live) => _live = live;

    public int Count => _live.Count;

    public GearIDRange this[int index] => _live[index];

    public void Clear() => _live.Clear();

    public void Add(GearIDRange gear) => _live.Add(gear);
}
