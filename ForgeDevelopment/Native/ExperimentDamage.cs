using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The one experiment that cannot be expressed as a read or a call: scaling the damage this machine takes.
/// The rewrite is a patch this module installs on demand and removes again, armed only for the duration of a
/// command, so nothing about damage handling changes while no experiment is running:
/// <list type="bullet">
/// <item><c>ExperimentDamage.Arm(factor)</c> patches the local player's damage entry points and multiplies
/// incoming damage by <c>factor</c> (0 makes the next hit a no-op, 0.5 halves it).</item>
/// <item><c>ExperimentDamage.Disarm()</c> unwinds every patch it installed and restores the original
/// methods, so the effect cannot outlive the command that asked for it.</item>
/// <item>A level cleanup or a failed patch also disarms: the prefix never survives the object it was armed
/// for.</item>
/// </list>
/// </summary>
internal static class ExperimentDamage
{
    private static readonly string[] EntryPoints =
    { "BulletDamage", "MeleeDamage", "ExplosionDamage", "FireDamage", "PushDamage", "FallDamage" };

    private static readonly List<MethodInfo> Patched = new();
    private static Harmony? _harmony;
    private static float _factor = 1f;

    internal static bool Armed => Patched.Count != 0;
    internal static float Factor => _factor;

    /// <summary>Installs the rewrite if it is not installed and sets the multiplier.</summary>
    internal static string Arm(float factor)
    {
        if (float.IsNaN(factor) || factor < 0f) return "the damage factor must be zero or positive, not " + factor;
        var type = typeof(Dam_SyncedDamageBase);
        _harmony ??= new Harmony(Plugin.PluginGuid + ".experiment.damage");
        var installed = 0;
        var skipped = new List<string>();
        foreach (var name in EntryPoints)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method == null)
            {
                skipped.Add(name + " (not present)");
                continue;
            }
            if (Patched.Contains(method)) continue;
            try
            {
                _harmony.Patch(method, prefix: new HarmonyMethod(typeof(ExperimentDamage).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!));
                Patched.Add(method);
                installed++;
            }
            catch (Exception e)
            {
                skipped.Add(name + " (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }
        _factor = factor;
        var report = "damage factor " + factor + "; patched " + installed + " of " + EntryPoints.Length + " entry points";
        if (skipped.Count != 0) report += "; skipped " + string.Join(", ", skipped);
        Log(report);
        return report;
    }

    /// <summary>Removes every patch this module installed. Safe to call when nothing is armed.</summary>
    internal static string Disarm()
    {
        _factor = 1f;
        if (Patched.Count == 0) return "no damage patch was installed";
        var restored = 0;
        var failures = new List<string>();
        foreach (var method in Patched)
        {
            try
            {
                _harmony?.Unpatch(method, HarmonyPatchType.All, _harmony.Id);
                restored++;
            }
            catch (Exception e)
            {
                failures.Add(method.Name + ": " + e.Message);
            }
        }
        Patched.Clear();
        var report = "restored " + restored + " damage entry points";
        if (failures.Count != 0) report += "; failed " + string.Join(", ", failures);
        Log(report);
        return report;
    }

    /// <summary>
    /// Runs inside the game's own damage call. It only ever changes the damage argument of the local
    /// player's own damage component, and only while a factor other than 1 is armed.
    /// </summary>
    private static void Prefix(Dam_SyncedDamageBase __instance, ref float dam)
    {
        try
        {
            if (_factor == 1f || dam <= 0f) return;
            var player = PlayerManager.GetLocalPlayerAgent();
            if (player?.Damage == null || !ReferenceEquals(player.Damage, __instance)) return;
            dam *= _factor;
        }
        catch (Exception e)
        {
            Log("the damage rewrite failed and was ignored: " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static void Log(string message)
    {
        try { Plugin.PluginLog.LogInfo("experiment damage: " + message); }
        catch (Exception) { }
    }
}
