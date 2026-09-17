using System;

namespace GameData
{
    /// <summary>One log file a terminal holds (dump.cs:634828). The members the content action writes and reads
    /// back are mirrored: the file's own name, its body, the audio file it plays and whether a player may see it
    /// in the list.</summary>
    public class TerminalLogFileData
    {
        public string FileName = "";
        public Localization.LocalizedText? FileContent;
        public uint AttachedAudioFile;
        public bool IsVisible;
    }
}

/// <summary>The player's own stamina component (the interop wrapper the stamina row reads and writes).</summary>
public class PlayerStamina : UnityEngine.Component
{
    public float Stamina;
}

/// <summary>The player's own camera (the interop wrapper the shake row writes through).</summary>
public class FPSCamera : UnityEngine.Component
{
    public int ShakeCalls;
    public UnityEngine.Vector3 LastDirection;
    public void Shake(float duration, float amplitude, float frequency, UnityEngine.Vector3 direction)
    {
        ShakeCalls++;
        LastDirection = direction;
    }
}

/// <summary>The native liquid presets the screen-liquid row indexes by name (dump.cs:588824).</summary>
public enum ScreenLiquidSettingName { Blood = 0, Water = 1, Infection = 2, Poison = 3 }

/// <summary>The one local liquid system the screen-liquid row queues a job against (dump.cs:588817). The entry
/// answers whether the job was queued, which is the result the row reports.</summary>
public static class ScreenLiquidManager
{
    public static int Applied;
    public static ScreenLiquidSettingName LastSetting;
    public static UnityEngine.Vector3 LastPosition;
    public static UnityEngine.Vector3 LastDirection;
    public static bool ThrowOnApply;
    public static bool Accept = true;
    public static bool Apply(ScreenLiquidSettingName setting, UnityEngine.Vector3 position, UnityEngine.Vector3 direction)
    {
        if (ThrowOnApply) throw new InvalidOperationException("fixture native failure");
        if (!Accept) return false;
        Applied++;
        LastSetting = setting;
        LastPosition = position;
        LastDirection = direction;
        return true;
    }
    public static void Reset() { Applied = 0; ThrowOnApply = false; Accept = true; }
}

namespace LevelGeneration
{
    /// <summary>One authored world-event object: the key the row filters on and the terminal its own interaction
    /// component addresses, which is what the world-event and interaction rows read.</summary>
    public class LG_WorldEventObject : UnityEngine.Component
    {
        public string? WorldEventObjectKey;
    }

    /// <summary>The terminal's own interaction component, which is the instance a terminal object's interaction
    /// switches address.</summary>
    public class Interact_ComputerTerminal : Interact_Base
    {
        public LG_ComputerTerminal? m_terminal;
    }
}

namespace LevelGeneration
{
    /// <summary>The two world-event trigger components the observation patches read (dump.cs:697116,
    /// dump.cs:697240). Only the members the production sources reach are mirrored: the two entries whose result
    /// says whether the trigger really activated, and the look-at distance the component kind exists for. The
    /// entries throw because the adapter calls the readback directly with the result it wants to model.</summary>
    public class LG_InteractWorldEventTrigger : Interact_Timed
    {
        public bool Trigger(SNetwork.SNet_Player source) => throw new NotSupportedException("The adapter calls the readback directly.");
        public bool ResetTrigger(SNetwork.SNet_Player source) => throw new NotSupportedException("The adapter calls the readback directly.");
    }

    public class LG_LookatWorldEventTrigger : UnityEngine.Component
    {
        public bool ThrowOnRead;
        public float Distance = 4f;
        public bool Trigger(SNetwork.SNet_Player source) => throw new NotSupportedException("The adapter calls the readback directly.");
        public bool ResetTrigger(SNetwork.SNet_Player source) => throw new NotSupportedException("The adapter calls the readback directly.");
        public float GetCamHoverMaxDistance => ThrowOnRead ? throw new InvalidOperationException("fixture native read failure") : Distance;
    }

    /// <summary>The terminal command rules (dump.cs:682601), in the enum's own member order.</summary>
    public enum TERM_CommandRule : byte { Normal = 0, OnlyOnce = 1, OnlyOnceDelete = 2 }
}

namespace Enemies
{
    /// <summary>`eEnemyType` (dump.cs:591652), in the member order both managers index their tables by.</summary>
    public enum eEnemyType { Weakling = 0, Standard = 1, Special = 2, MiniBoss = 3, Boss = 4 }

    /// <summary>The level's own population component (dump.cs:591673). The fields the tuning writes are mirrored
    /// with the game's own types, and `SetCooldownFactor` records the value so a case can read what landed.</summary>
    public class EnemyPopulationManager : UnityEngine.Component
    {
        public static EnemyPopulationManager? Current;
        public float[]? m_baseWeightTable;
        public float[]? m_heatTable;
        public float m_maxHeat;
        public float CooldownFactor = 1f;
        public float[]? m_enemyTypeLimits;
        public void SetCooldownFactor(float value) => CooldownFactor = value;
        public static void Reset() => Current = null;
    }

    /// <summary>The per-dimension cost cap (dump.cs:591576). The cap is a static property, exactly as the
    /// production write reads it, and the per-type costs are the array it assigns.</summary>
    public class EnemyCostManager : UnityEngine.Component
    {
        public static EnemyCostManager? Current;
        public float[]? m_enemyTypeCosts;
        public static float AllowedTotalCost { get; set; }
        public static void Reset() { Current = null; AllowedTotalCost = 0f; }
    }
}
