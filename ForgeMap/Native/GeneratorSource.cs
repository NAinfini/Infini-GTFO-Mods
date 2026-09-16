using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;
using HarmonyLib;
using LevelGeneration;
using SNetwork;

namespace ForgeMap.Native;

// The generator category's two readback points, declared with the reader they feed. Each is a postfix on the
// method the game itself runs when the generator or its group changed, and each one only chooses when to read:
// the callback's own arguments are never turned into fact values, because the instance is re-read afterwards
// and reports the state it is really in. Both are listed in `MapNativeHooks.Types` and frozen in
// `evidence/map-hooks.json`.

// The generator's own state replication callback. The state the callback carries names the player and the item
// that changed it — the one record of what changed, which the instance does not hold — while the powered
// reading the fact reports is read from the instance after the body returned.
[HarmonyPatch(typeof(LG_PowerGenerator_Core), nameof(LG_PowerGenerator_Core.OnStateChange))]
internal static class GeneratorCellReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_PowerGenerator_Core __instance, pPowerGeneratorState newState)
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardMapObjects(module => GeneratorObservation.CellChanged(__instance, newState, module));
    }
}

// The generator group's own state callback. The native group state carries a fog step and no member count, so
// this hook only chooses when to look: the module re-reads the group's members through the generator source and
// publishes the group fact only when every one of them reads powered.
[HarmonyPatch(typeof(LG_PowerGeneratorCluster), nameof(LG_PowerGeneratorCluster.OnStateChange))]
internal static class GeneratorClusterStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_PowerGeneratorCluster __instance)
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardMapObjects(module => GeneratorObservation.GroupChanged(__instance, module));
    }
}

/// <summary>
/// The generator reader. It answers for the two native instance kinds of the generator category: a
/// `LG_PowerGenerator_Core` is one generator and a `LG_PowerGeneratorCluster` is the group the level parented it
/// under, and each is addressed by <see cref="MapObjectGeneratorAddress"/> — the zone the instance stands in,
/// read through the same <see cref="ZoneIndex"/> every other map-object reader uses, and the serial the level
/// assigned. A subject whose zone or serial does not read produces no address at all, and its cause is reported
/// once per cause.
///
/// Nothing is synthesized here: the powered reading is the generator's own replicated status, and the group
/// counts are the group's own member array read at the moment of the read. The two facts a native callback
/// produces are published by the module this source is handed to, which re-reads the instance through this
/// source, so a fact never reports a state the instance is not in.
/// </summary>
internal sealed class GeneratorSource : IMapObjectSource
{
    private readonly Action<string> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    internal GeneratorSource(Action<string> report) => _report = report;

    public string Category => MapObjectGeneratorAddress.Category;

    public MapObjectReference? Parse(string text) => MapObjectGeneratorAddress.TryParse(text);

    public MapObjectReference? TryAddress(object instance)
    {
        // Only the two native kinds of this category are read at all: an instance of any other type is not a
        // generator, so it answers nothing and is not reported — the door and terminal readers refuse a foreign
        // instance the same way. A generator the level placed but this world cannot address is reported once
        // per cause.
        if (instance is not LG_PowerGenerator_Core && instance is not LG_PowerGeneratorCluster) return null;
        if (GeneratorObservation.AddressOf(instance) is { } address) return address;
        Report("map-object generator is not addressable: it is not a generator or a generator group of a zone "
            + "this level knows, so it carries none of the address's keys.");
        return null;
    }

    public MapObjectObservation? Read(object instance) => GeneratorObservation.Read(instance);

    public double[]? Position(object instance) => GeneratorObservation.Position(instance);

    public bool IsCurrentAddress(object instance, MapObjectReference address)
        => GeneratorObservation.AddressOf(instance) == address;

    public void Report(string message)
    {
        if (_reported.Add(message)) _report(message);
    }
}

/// <summary>
/// The game-bound half of the generator category: the one native read every generator fact and the generator
/// value row are answered from, and the table of the instances this world has read.
///
/// The value row needs the native subject again when a plan asks, and an address is all the module holds, so this
/// half remembers the generators its own reads produced and drops them with the world they belong to. An address
/// the table does not hold is a generator this provider never read, and the reader refuses it rather than reading
/// a same-serial object of a world it no longer holds.
/// </summary>
internal static class GeneratorObservation
{
    private static readonly Dictionary<string, LG_PowerGenerator_Core> Generators = new(StringComparer.Ordinal);
    private static long _world = long.MinValue;

    /// <summary>One world's table. A new world epoch drops the native instances of the level that no longer
    /// exists, so an address of the old level cannot answer with an instance of the new one.</summary>
    private static void Observe(long world)
    {
        if (world == _world) return;
        _world = world;
        Generators.Clear();
    }

    /// <summary>Whether the table still belongs to the world the session is in. A session that is gone reads no
    /// table at all.</summary>
    private static bool Current() => Plugin.Session is not { } session || session.WorldEpoch == _world;

    /// <summary>One generator cell change, from the generator's own replicated state callback. The cell and the
    /// player come from the state the callback carries — the one record of what changed, which the instance
    /// itself does not hold — and the direction the fact reports is read back from the instance by the module.
    /// The generator is remembered so the value row can read it again.</summary>
    internal static void CellChanged(LG_PowerGenerator_Core generator, pPowerGeneratorState newState,
        MapObjectModule module)
    {
        Observe(module.WorldEpoch);
        if (AddressOf(generator) is { } address) Generators[address.ToString()] = generator;
        module.GeneratorCellChanged(generator, CellOf(newState.itemDataThatChangedTheState),
            LevelObjectObservation.ActorOf(newState.playerThatChangedTheState));
    }

    /// <summary>One group state change, from the group's own replicated state callback. The native cluster state
    /// carries a fog step and no member count, so the module re-reads the group's members through this half and
    /// publishes the group fact only when every one of them reads powered.</summary>
    internal static void GroupChanged(LG_PowerGeneratorCluster cluster, MapObjectModule module)
    {
        Observe(module.WorldEpoch);
        module.GeneratorGroupConnected(cluster);
    }

    /// <summary>The address of one native instance: a generator's own serial or its group's, under the zone the
    /// instance stands in. A negative serial, a missing zone or a zone whose coordinates do not read yields no
    /// address rather than a guessed one.</summary>
    internal static MapObjectReference? AddressOf(object instance)
    {
        if (instance is LG_PowerGenerator_Core generator)
            return AddressOf(generator, generator.m_serialNumber, group: false);
        if (instance is LG_PowerGeneratorCluster cluster)
            return AddressOf(cluster, cluster.m_serialNumber, group: true);
        return null;
    }

    private static MapObjectReference? AddressOf(UnityEngine.Component component, int serial, bool group)
    {
        if (serial < 0) return null;
        var zone = component.GetComponentInParent<LG_Zone>();
        if (zone == null || zone.WasCollected) return null;
        if (ZoneIndex.Coordinates(zone) is not { } coordinates) return null;
        return group
            ? MapObjectGeneratorAddress.CreateGroup(coordinates.Dimension, coordinates.Layer, coordinates.Zone, serial)
            : MapObjectGeneratorAddress.Create(coordinates.Dimension, coordinates.Layer, coordinates.Zone, serial);
    }

    /// <summary>One instance re-read in one place: a generator answers its own powered reading beside the counts
    /// of the group it stands in, and a group answers the counts of its own members. Any other instance is not
    /// this category's and answers nothing.</summary>
    internal static MapObjectObservation? Read(object instance)
    {
        if (instance is LG_PowerGenerator_Core generator)
        {
            var (connected, total) = Counts(ClusterOf(generator));
            return new MapObjectObservation(true, null, null, null,
                new MapObjectGeneratorSnapshot(StatusOf(generator) == ePowerGeneratorStatus.Powered, connected, total));
        }
        if (instance is LG_PowerGeneratorCluster cluster)
        {
            var (connected, total) = Counts(cluster);
            return new MapObjectObservation(true, null, null, null, null,
                new MapObjectGeneratorGroupSnapshot(connected, total));
        }
        return null;
    }

    internal static double[]? Position(object instance)
    {
        if (instance is not UnityEngine.Component component || component.WasCollected) return null;
        var position = component.transform.position;
        return new[] { (double)position.x, (double)position.y, (double)position.z };
    }

    /// <summary>The group a generator belongs to, found by its own hierarchy: the level builder parents a
    /// group's generators under the group, so the nearest group above the generator is its owner. A generator
    /// that stands under no group belongs to no group, and every count for it is zero, which the module reads as
    /// "no group" rather than as a completed one.</summary>
    internal static LG_PowerGeneratorCluster? ClusterOf(LG_PowerGenerator_Core generator)
    {
        var found = generator.GetComponentInParent<LG_PowerGeneratorCluster>();
        return found == null || found.WasCollected ? null : found;
    }

    /// <summary>The two counts of one group: how many of its members read `Powered` and how many it holds. A
    /// group whose array does not read answers (0, 0).</summary>
    internal static (int Connected, int Total) Counts(LG_PowerGeneratorCluster? cluster)
    {
        if (cluster == null || cluster.WasCollected) return (0, 0);
        var members = cluster.m_generators;
        if (members == null) return (0, 0);
        int connected = 0, total = 0;
        for (int i = 0; i != members.Length; i++)
        {
            var member = members[i];
            if (member == null || member.WasCollected) continue;
            total++;
            if (StatusOf(member) == ePowerGeneratorStatus.Powered) connected++;
        }
        return (connected, total);
    }

    /// <summary>One generator's own replicated status. The state replicator's current state is the game's own
    /// reading of the generator; a replicator that does not read answers `UnPowered`, which is the value a
    /// generator holding no cell has.</summary>
    internal static ePowerGeneratorStatus StatusOf(LG_PowerGenerator_Core generator)
    {
        var replicator = generator.m_stateReplicator;
        return replicator == null ? ePowerGeneratorStatus.UnPowered : replicator.State.status;
    }

    /// <summary>One generator's powered reading and its group's counts, for
    /// `forge.condition.predicate.power`. The counts are the same derivation the group fact publishes, read at
    /// the moment of the query instead of remembered from the last change; a generator that stands under no
    /// group answers (0, 0), which is the group the level gave it.</summary>
    internal static JsonElement? ReadState(string generatorKey)
    {
        if (!Current() || !Generators.TryGetValue(generatorKey, out var generator) || generator.WasCollected)
            return null;
        var (connected, total) = Counts(ClusterOf(generator));
        return RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["value"] = RuntimeJson.From(StatusOf(generator) == ePowerGeneratorStatus.Powered),
            ["connected"] = RuntimeJson.From(connected),
            ["total"] = RuntimeJson.From(total)
        });
    }

    /// <summary>The cell one state change named, written as the item's own gear block id — the CRC the block
    /// carries. A state change with no item names none.</summary>
    private static string CellOf(Player.pItemData item)
    {
        uint crc = item.itemID_gearCRC;
        return crc == 0 ? "" : crc.ToString("x8", CultureInfo.InvariantCulture);
    }
}
