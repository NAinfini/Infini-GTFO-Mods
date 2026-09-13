using InfiniTweaks;
using LevelGeneration;
using UnityEngine;

ItemMarkers.RunChecks();

namespace InfiniTweaks
{
    internal enum MarkerCategory { Other, Terminal }
    internal static partial class ItemMarkers
    {
        private sealed record Device(Component Owner, Component Anchor, MarkerCategory Category, Func<bool> Available, Func<string>? Detail = null);
        private sealed record Pin(int Id, bool Explicit);
        private static readonly Dictionary<int, Device> Devices = new();
        private static readonly Dictionary<int, LG_GenericTerminalItem> DiscoveryOwners = new();
        private static readonly Dictionary<uint, LG_GenericTerminalItem> Terminals = new();
        private static readonly Dictionary<int, uint> TerminalIds = new();
        private static readonly Dictionary<int, Pin> Pins = new();
        private static int labels, discoveries, removals;
        // Presentation/discovery are doubles; the linked production registration code
        // must only rebind an already-known pin, never reveal newly registered devices.
        private static void Label(Pin _) => labels++;
        private static void Forget(int id) { Pins.Remove(id); removals++; }
        private static void RememberTerminalObject(LG_GenericTerminalItem terminal, bool explicitDiscovery)
        { Pins[terminal.GetInstanceID()] = new(terminal.GetInstanceID(), explicitDiscovery); discoveries++; }
        internal static void RunChecks()
        {
            int checks = 0;
            void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
            var unattached = new LG_GenericTerminalItem();
            RegisterTerminal(unattached);
            Check(unattached.SpawnNode == null, "Missing area does not pass null into the native SpawnNode setter.");
            RemoveTerminal(unattached); removals = 0;
            var area = new LG_Area { m_courseNode = new() };
            var item = new LG_GenericTerminalItem { TerminalItemId = 10, Parent = area };
            RegisterTerminal(item);
            Check(item.SpawnNode == area.m_courseNode && Terminals[10] == item, "Missing node comes from containing area.");
            var nativeNode = new CourseNode(); item.SpawnNode = nativeNode; RegisterTerminal(item);
            Check(item.SpawnNode == nativeNode, "Native authored node is never overwritten.");
            item.TerminalItemId = 20; RegisterTerminal(item);
            Check(!Terminals.ContainsKey(10) && Terminals[20] == item, "Reconfiguration removes old QUERY identity.");
            var computer = new LG_ComputerTerminal { m_terminalItem = item, m_CameraAlign = new() };
            var screen = new Collider(); computer.Children.Add(screen); screen.transform.parent = computer.transform;
            RegisterComputer(computer);
            Check(ResolveTerminal(screen) == item, "Sibling screen collider resolves the owner's terminal identity.");
            Check(screen.GetComponent<PlayerPingTarget>()?.m_pingTargetStyle == eNavMarkerStyle.PlayerPingTerminal, "Computer collider receives native ping target.");
            Check(Pins.Count == 0 && discoveries == 0, "Registering a late device does not discover it.");
            Check(Devices[item.GetInstanceID()].Category == MarkerCategory.Terminal && Devices[item.GetInstanceID()].Anchor == computer.m_CameraAlign, "Computer uses terminal category and screen anchor.");
            computer.isActiveAndEnabled = false;
            Check(Devices[item.GetInstanceID()].Available(), "Native update-component activity is not terminal availability.");
            computer.isActiveAndEnabled = true;
            Check(Devices[item.GetInstanceID()].Available(), "Reenabled device becomes available without rescan.");
            Pins[item.GetInstanceID()] = new(item.GetInstanceID(), true);
            var previous = Devices[item.GetInstanceID()]; RegisterComputer(computer);
            Check(Devices[item.GetInstanceID()] == previous && discoveries == 0 && removals == 0, "Repeated Setup does not recreate unchanged marker metadata or visual.");
            computer.m_CameraAlign = new(); RegisterComputer(computer);
            Check(discoveries == 1 && removals == 1 && Pins[item.GetInstanceID()].Explicit, "Changed anchor rebinds known pin and preserves explicit discovery.");
            Check(labels > 0, "Reconfigured terminal refreshes existing title.");
            var replacement = new LG_GenericTerminalItem { TerminalItemId = 20 };
            computer.m_terminalItem = replacement; RegisterComputer(computer); RemoveTerminal(item);
            Check(Terminals[20] == replacement && DiscoveryOwners[computer.gameObject.GetInstanceID()] == replacement, "Destroying old terminal cannot remove replacement sharing ID/owner.");
            var reactorNode = new CourseNode();
            var reactor = new LG_WardenObjective_Reactor { SpawnNode = reactorNode, m_terminal = computer };
            RegisterReactor(reactor); RegisterReactor(reactor);
            Check(reactorNode.m_zone.TerminalsSpawnedInZone.Count == 1, "Reactor terminal is registered in zone exactly once.");
            Check(replacement.SpawnNode == reactorNode && Devices.ContainsKey(replacement.GetInstanceID()), "Reactor supplies its own native node and joins normal device registry.");
            Check(discoveries == 1, "Reactor registration does not bypass discovered-only rule.");
            RemoveTerminal(replacement);
            Check(Terminals.Count == 0 && TerminalIds.Count == 0 && Devices.Count == 0 && !DiscoveryOwners.ContainsKey(computer.gameObject.GetInstanceID()), "Destroy removes terminal identities, metadata and collider ownership.");
            RegisterReactor(new()); RegisterReactor(new() { m_terminal = new() });
            Check(Terminals.Count == 0 && discoveries == 1, "Incomplete native setup does not invent identity or reveal a device.");
            Console.WriteLine($"PASS: {checks} production marker-registration checks with managed doubles; not Unity rendering/multiplayer acceptance.");
        }
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type _, string __) { } }
    internal sealed class HarmonyPrefix : Attribute { }
    internal sealed class HarmonyPostfix : Attribute { }
}
namespace UnityEngine
{
    public class Object { private static int next; private readonly int id = ++next; public int GetInstanceID() => id; }
    public sealed class GameObject : Object
    {
        private readonly List<Component> components = new();
        public T AddComponent<T>() where T : Component, new() { var value = new T { gameObject = this }; components.Add(value); return value; }
        public T? GetComponent<T>() where T : class => components.OfType<T>().FirstOrDefault();
    }
    public class Component : Object
    {
        public GameObject gameObject = new(); public Component? Parent; public bool isActiveAndEnabled = true;
        private Transform? ownTransform;
        public Transform transform => this as Transform ?? (ownTransform ??= new Transform { gameObject = gameObject });
        public List<Component> Children = new();
        public T[] GetComponentsInChildren<T>(bool _) where T : Component => Children.OfType<T>().ToArray();
        public T? GetComponent<T>() where T : class => gameObject.GetComponent<T>();
        public T? TryCast<T>() where T : class => this as T;
        public T? GetComponentInParent<T>() where T : class => this as T ?? Parent?.GetComponentInParent<T>();
    }
    public sealed class Transform : Component { public Transform? parent; }
    public sealed class Collider : Component { }
}
public sealed class PlayerPingTarget : Component { public eNavMarkerStyle m_pingTargetStyle; }
public enum eNavMarkerStyle { PlayerPingTerminal }
namespace LevelGeneration
{
    public sealed class CourseNode { public Zone m_zone = new(); }
    public sealed class Zone { public List<LG_ComputerTerminal> TerminalsSpawnedInZone = new(); }
    public sealed class LG_Area : Component { public CourseNode m_courseNode = new(); }
    public sealed class LG_GenericTerminalItem : Component
    {
        public uint TerminalItemId;
        private CourseNode? node;
        public CourseNode? SpawnNode { get => node; set => node = value ?? throw new NullReferenceException("Native SpawnNode setter"); }
        public void Setup() { } public void OnDestroy() { }
    }
    public sealed class LG_ComputerTerminal : Component
    { public Component? m_terminalItem; public Transform? m_CameraAlign; public void Setup() { } }
    public sealed class LG_WardenObjective_Reactor : Component
    { public LG_ComputerTerminal? m_terminal; public CourseNode? SpawnNode; public void OnBuildDone() { } }
}
