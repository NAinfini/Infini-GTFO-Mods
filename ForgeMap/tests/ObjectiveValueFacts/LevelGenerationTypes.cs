using System;

// The level-generation types the two production sources under test name, declared for the focused build only.
// They are the members the sources actually touch and no more: a member renamed in the game fails the mirror
// project (`ForgeMap.Native.csproj` compiles the same sources against the real interop assemblies), while the
// cases here drive values through them without a game process.

namespace LevelGeneration;

/// <summary>The layer members the doubles below index by. The production read only ever compares these three, and
/// the values are the build's own.</summary>
public enum LG_LayerType : byte
{
    MainLayer = 0,
    SecondaryLayer = 1,
    ThirdLayer = 2
}

/// <summary>One layer of a zone, which is what the zone address is derived from.</summary>
public sealed class LG_Layer
{
    public LG_LayerType m_type;
}

/// <summary>One level zone: the dimension it belongs to, the layer it was built on and its index in that
/// layer.</summary>
public sealed class LG_Zone
{
    public LG_Layer m_layer = new();
    public int m_dimensionIndex;
    public int IDinLayer;
}

/// <summary>One dimension portal.</summary>
public sealed class LG_DimensionPortal
{
    public int m_serialNumber;
    public int m_targetDimension;
}
