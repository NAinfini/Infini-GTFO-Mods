using System.Globalization;
using System.Reflection;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using Host = ForgeRuntime.Plugin;
using MapPlugin = ForgeMap.Native.Plugin;

namespace ForgeMap.Tests.MapNativeAdapter;

// What the `gtfo.map_object` instance lookup answers when a hit hands it the object the bullet was resolved
// against. Native state is a managed double: the colliders, the doors and the terminals below are synthetic and
// NOT game-verified, and the climb from a collider to the door above it is the one step no reading can prove
// here — it is listed as unproven in evidence/door-terminal-hooks.json.
public sealed class MapObjectHitTests
{
    public MapObjectHitTests() => Reset();

    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private static void Reset()
    {
        var session = MapPlugin.Session;
        if (session != null)
        {
            try { session.Module.Dispose(); session.Dispose(); } catch { }
            typeof(MapPlugin).GetProperty("Session", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        }
        Harmony.Reset(); PlayerManager.Reset(); SNet.IsMaster = true;
        LG_LevelBuilder.Current = null; ZoneIndex.Reset(); World = 0;
        RundownManager.Reset();
        Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = null;
    }

    // Each case gets a world of its own: the zone table is keyed by the world epoch, so a case that reused an
    // earlier epoch would keep reading the earlier case's level.
    private static long World;

    private static RuntimeKernel Kernel()
    {
        var kernel = new RuntimeKernel(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
        // The canonical combat contract provider the host registers as a builtin: the Map provider binds the
        // heal action the player half implements, and the runtime refuses a binding whose capability was never
        // declared.
        kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        // The trigger contract is the host's other builtin: the map-object bindings name its capabilities.
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        kernel.BeginWorld(++World); return kernel;
    }

    /// <summary>Starts the one Map session over a kernel, exactly as the host does, with no native install hook:
    /// these cases read the instance lookup the registration already carries.</summary>
    private static MapPluginSession Start(RuntimeKernel kernel, List<string> info, List<string> warn)
        => MapPluginSession.Start(kernel, RuntimeLogLevel.Off, warn.Add, info.Add, () => { }, () => { });

    /// <summary>One component a bullet can be resolved against, standing under the map object it was hit
    /// through. The collider is what the hit path holds; the door above it is what it must be answered with.</summary>
    private static UnityEngine.Collider HitObject(UnityEngine.Component? above)
        => new() { Parent = above };

    /// <summary>A hit object belongs to the map object above it: the door's own address is the answer, and the
    /// door handed over directly keeps answering the same way.</summary>
    [Fact]
    public void map_object_resolver_accepts_a_hit_object_and_answers_its_own_entity()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new();
        using var session = Start(kernel, info, warn);
        kernel.StartRuntime(() => { });
        var world = new SyntheticLevel(kernel);
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_2);
        var door = world.Entrance(zone);
        var terminal = world.Terminal(zone, TERM_State.Awake);
        Require(world.Reference(door)?.Id == "gtfo.map_object:door/0/0/2/security",
            "The entrance door did not answer for itself.");
        // The door's own components — its blades, its frame — are what a shot really hits, so the collider the
        // bullet was resolved against is handed over and the door above it is the address that comes back.
        var doorBlade = HitObject(door);
        Require(world.Reference(doorBlade)?.Id == "gtfo.map_object:door/0/0/2/security",
            "A collider under the entrance door did not resolve to the door.");
        var terminalScreen = HitObject(terminal);
        Require(world.Reference(terminalScreen)?.Id == "gtfo.map_object:terminal/0/0/2/0",
            "A collider under the zone's terminal did not resolve to the terminal.");
        // A component's own instance is reached through the same lookup, so a deeper ancestor still answers for
        // the same object instead of for the component in between.
        var deeper = HitObject(HitObject(terminal));
        Require(world.Reference(deeper)?.Id == "gtfo.map_object:terminal/0/0/2/0",
            "A component below another component did not reach the terminal above both.");
        Require(session.LastFault == null && warn.Count == 0,
            "Resolving a hit object failed: " + session.LastFault + " " + string.Join(" | ", warn));
    }

    /// <summary>A hit object with no map object above it names nothing. World geometry, an enemy limb and a
    /// deployable are all this case: none of them may be answered with an address the object does not carry.</summary>
    [Fact]
    public void map_object_resolver_refuses_a_hit_object_with_no_map_object_ancestor()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new();
        using var session = Start(kernel, info, warn);
        kernel.StartRuntime(() => { });
        var world = new SyntheticLevel(kernel);
        world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_0);
        // Geometry the level built: a real collider with nothing this kind knows above it.
        var geometry = HitObject(null);
        // A collider that does have a parent, so the climb really runs and really finds no map object.
        Require(world.Reference(geometry) == null && world.Reference(HitObject(HitObject(null))) == null,
            "A hit object with no map object above it was given an address.");
        // An enemy's damage limb is a component too, and it is not a map object of this kind.
        var enemyLimb = HitObject(new UnityEngine.Collider());
        Require(world.Reference(enemyLimb) == null, "A damage limb was addressed as a map object.");
        Require(session.LastFault == null && warn.Count == 0,
            "Refusing a hit object failed: " + session.LastFault + " " + string.Join(" | ", warn));
    }

    /// <summary>Being a map object's component is not being addressable: a door the level did not make a zone's
    /// entrance, and a bulkhead transition, are refused with the cause the door's own reader reports — and the
    /// hit path never invents coordinates for either.</summary>
    [Fact]
    public void map_object_resolution_never_invents_an_address()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new();
        using var session = Start(kernel, info, warn);
        kernel.StartRuntime(() => { });
        var world = new SyntheticLevel(kernel);
        var zone = world.Zone(1, LG_LayerType.SecondaryLayer, eLocalZoneIndex.Zone_3);
        var bulkhead = world.Entrance(zone, eDoorStatus.Closed, eSecurityDoorType.Bulkhead);
        var loose = world.LooseDoor(eDoorStatus.Closed);
        Require(world.Reference(bulkhead) == null && world.Reference(loose) == null,
            "An unaddressable door answered for itself.");
        Require(world.Reference(HitObject(bulkhead)) == null && world.Reference(HitObject(loose)) == null,
            "An unaddressable door's hit object was given an address.");
        // Refusing is not failing: the door's own reader reports each cause once and nothing is published.
        Require(session.LastFault == null && session.MapObjects!.PublishedFacts == 0
            && warn.Count(r => r.Contains("bulkhead layer transition", StringComparison.Ordinal)) == 1
            && warn.Count(r => r.Contains("not the entrance gate of a zone", StringComparison.Ordinal)) == 1,
            "Unaddressable doors were not refused once per cause: " + string.Join(" | ", warn));
    }
}
