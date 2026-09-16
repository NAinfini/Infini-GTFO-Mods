using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using UnityEngine;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeDevelopment.Native;

/// <summary>
/// The game's own answers for everything a record carries: process milliseconds, Unity frame, the game's network
/// time, the multiplayer role, the player slot, the level, the Runtime's world epoch and tick, the camera aim a
/// bookmark records and the screenshot itself. All of it is read on the game's main thread at enqueue time, because
/// most of it is IL2CPP state that must not be touched from the writer thread.
///
/// The reads are static because a caller that wants one value should not need a session; the
/// <see cref="IRecSessionContext"/> members are implemented explicitly over them, so there is one implementation of
/// each read and only one way to call it.
/// </summary>
internal sealed class RecTime : IRecSessionContext
{
    internal static readonly RecTime Instance = new();

    private static readonly long Origin = Environment.TickCount64;
    private static bool _slotFailed, _snetFailed, _levelFailed, _aimFailed;

    internal static void Install() => RecSession.UseContext(Instance);

    internal static long Milliseconds => Environment.TickCount64 - Origin;

    internal static int Frame { get { try { return Time.frameCount; } catch (Exception) { return -1; } } }
    internal static string GameVersion { get { try { return Application.version; } catch (Exception) { return "unavailable"; } } }
    internal static string UnityVersion { get { try { return Application.unityVersion; } catch (Exception) { return "unavailable"; } } }

    internal static string Role
    {
        get
        {
            try { return SNetwork.SNet.IsMaster ? "host" : "client"; }
            catch (Exception) { return "unavailable"; }
        }
    }

    /// <summary>The game's own session slot, or "unavailable" once the read has failed. The slot manager is missing
    /// outside a session, so the failure is remembered instead of paying the exception every record.</summary>
    internal static string Slot
    {
        get
        {
            if (_slotFailed) return "unavailable";
            try
            {
                var player = SNetwork.SNet.LocalPlayer;
                if (player == null) return "unavailable";
                return player.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { _slotFailed = true; return "unavailable"; }
        }
    }

    /// <summary>The player's own synchronized session time. SNet leaves it at zero until the player is synchronized
    /// with the game, which is exactly when a record becomes alignable across machines; a zero is reported as no time
    /// rather than as a real one.</summary>
    internal static float? SNetTime
    {
        get
        {
            if (_snetFailed) return null;
            try
            {
                var player = SNetwork.SNet.LocalPlayer;
                if (player == null) return null;
                var time = player.m_syncTime;
                return time > 0f && float.IsFinite(time) ? time : null;
            }
            catch (Exception) { _snetFailed = true; return null; }
        }
    }

    /// <summary>The expedition the process is in: the rundown's own active-expedition key, which is the identity the
    /// rest of this package already reports. It is "unavailable" outside a rundown rather than a made-up id.</summary>
    internal static string Level
    {
        get
        {
            if (_levelFailed) return "unavailable";
            try
            {
                var key = RundownManager.ActiveExpeditionUniqueKey;
                return string.IsNullOrEmpty(key) ? "unavailable" : key;
            }
            catch (Exception) { _levelFailed = true; return "unavailable"; }
        }
    }

    internal static long WorldEpoch => HostPlugin.Runtime?.WorldEpoch ?? -1;
    internal static long Tick => HostPlugin.Runtime?.CurrentTick ?? -1;

    string IRecSessionContext.Role => Role;
    string IRecSessionContext.Slot => Slot;
    string IRecSessionContext.Level => Level;
    string IRecSessionContext.GameVersion => GameVersion;
    string IRecSessionContext.UnityVersion => UnityVersion;
    long IRecSessionContext.WorldEpoch => WorldEpoch;
    long IRecSessionContext.Tick => Tick;
    int IRecSessionContext.Frame => Frame;
    float? IRecSessionContext.SNetTime => SNetTime;

    /// <summary>Writes the fixed part of every record. The writer is already inside the record object.</summary>
    void IRecSessionContext.WriteContext(Utf8JsonWriter json, long sequence, string sessionId, string channel, string kind)
    {
        json.WriteString("v", RecSession.SchemaVersion);
        json.WriteNumber("seq", sequence);
        json.WriteString("session", sessionId);
        json.WriteString("channel", channel);
        json.WriteString("kind", kind);
        json.WriteNumber("t", Milliseconds);
        var frame = Frame;
        if (frame >= 0) json.WriteNumber("frame", frame);
        if (SNetTime is { } snet) json.WriteNumber("snetTime", MathF.Round(snet, 3));
        json.WriteString("role", Role);
        json.WriteString("slot", Slot);
        json.WriteString("level", Level);
        var epoch = WorldEpoch;
        if (epoch >= 0) json.WriteNumber("worldEpoch", epoch);
        var tick = Tick;
        if (tick >= 0) json.WriteNumber("tick", tick);
    }

    /// <summary>Captures the screen into the session directory. A capture that cannot be taken answers an empty path,
    /// which the session records as an error rather than as an empty file.</summary>
    string IRecSessionContext.WriteScreenshot(string directory, string relative)
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = ScreenCapture.CaptureScreenshotAsTexture();
        try
        {
            var bytes = ImageConversion.EncodeToPNG(image);
            File.WriteAllBytes(path, bytes);
        }
        finally { UnityEngine.Object.Destroy(image); }
        return relative;
    }

    /// <summary>The player's own view when a bookmark is taken, so the bookmark says what they were looking at. It is
    /// a best-effort read: anything the game refuses answers null and the bookmark stays a label.</summary>
    (string Name, string Value)[]? IRecSessionContext.Aim()
    {
        if (_aimFailed) return null;
        try
        {
            var camera = Camera.main;
            if (camera == null) return null;
            var position = camera.transform.position;
            var forward = camera.transform.forward;
            return new[]
            {
                ("camera", RecReflect.Truncate(camera.name, 64)),
                ("position", Number(position.x) + "," + Number(position.y) + "," + Number(position.z)),
                ("forward", Number(forward.x) + "," + Number(forward.y) + "," + Number(forward.z))
            };
        }
        catch (Exception) { _aimFailed = true; return null; }
    }

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
