using UnityEngine;
using UnityEngine.SceneManagement;
using ForgeDevelopment.Native;

internal static class SceneInventoryTests
{
    static void Main()
    {
        var root = new GameObject();
        var cursor = root;
        for (int i = 0; i < 500; i++)
        {
            var child = new GameObject();
            child.Components.Add(new SkinnedMeshRenderer());
            cursor.transform.Children.Add(child.transform); cursor = child;
        }
        var persistent = new GameObject(); persistent.Components.Add(new Camera());
        SceneManager.Roots[1] = new[] { root }; SceneManager.Roots[2] = new[] { persistent };
        SceneManager.Scenes = new[] { new Scene(1), new Scene(2) };
        var scan = new SceneInventory();
        var iterator = scan.Capture(new Scene(2)).GetEnumerator();
        int steps = 0;
        while (iterator.MoveNext()) steps++;
        Check(scan.TryGet(typeof(GameObject), out var objects) && objects.Count == 502, "Walk loaded and persistent scenes once, including deep children.");
        Check(scan.TryGet(typeof(Renderer), out var renderers) && renderers.Count == 500, "Derived renderers belong to their base-class bucket.");
        Check(scan.TryGet(typeof(SkinnedMeshRenderer), out var skinned) && skinned.Count == 500, "Specific renderer bucket retains all skins.");
        Check(scan.TryGet(typeof(Camera), out var cameras) && cameras.Count == 1, "Persistent scene is not counted twice.");
        Check(scan.TryGet(typeof(Component), out var components) && components.Count == 1003, "All component types, including transforms, remain counted.");
        Check(steps > 1500 && GameObject.ComponentQueries == 502, "Traversal yields at object/component boundaries and queries each object once.");
        SceneManager.Scenes = new[] { new Scene(1) };
        var additional = new SceneInventory(); foreach (var _ in additional.Capture(new Scene(2))) { }
        Check(additional.TryGet(typeof(Camera), out cameras) && cameras.Count == 1, "Explicit persistent scene is included when sceneCount omits it.");
        Check(!scan.TryGet(typeof(Texture), out _), "Unattached asset types stay with the separate asset inventory.");
        Console.WriteLine($"PASS: 8 scene-inventory checks; 502 objects, {steps} cooperative yields, 502 component queries. Managed traversal test, not native timing.");
    }
    static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
}
namespace UnityEngine
{
    public class Object
    {
        static int next;
        internal static Dictionary<IntPtr, Object> Instances = new();
        public IntPtr Pointer { get; } = (IntPtr)(++next);
        public Object() => Instances[Pointer] = this;
    }
    public class Component : Object { }
    public class Transform : Component
    {
        public GameObject gameObject = null!;
        public List<Transform> Children = new();
        public int childCount => Children.Count;
        public Transform GetChild(int index) => Children[index];
    }
    public class GameObject : Object
    {
        public static int ComponentQueries;
        public Transform transform;
        public List<Component> Components = new();
        public GameObject() { transform = new() { gameObject = this }; Components.Add(transform); }
        public T[] GetComponents<T>() where T : Component { ComponentQueries++; return Components.OfType<T>().ToArray(); }
    }
    public class Renderer : Component { }
    public class SkinnedMeshRenderer : Renderer { }
    public class Camera : Component { }
    public class Light : Component { }
    public class ParticleSystem : Component { }
    public class AudioSource : Component { }
    public class Animator : Component { }
    public class Collider : Component { }
    public class Rigidbody : Component { }
    public class Canvas : Component { }
    public class CanvasRenderer : Component { }
    public class Texture : Object { }
}
namespace UnityEngine.AI { public class NavMeshAgent : Component { } }
namespace UnityEngine.SceneManagement
{
    public readonly record struct Scene(int handle)
    {
        public bool IsValid() => SceneManager.Roots.ContainsKey(handle);
        public bool isLoaded => IsValid();
        public GameObject[] GetRootGameObjects() => SceneManager.Roots[handle];
    }
    public static class SceneManager
    {
        public static Dictionary<int, GameObject[]> Roots = new();
        public static Scene[] Scenes = Array.Empty<Scene>();
        public static int sceneCount => Scenes.Length;
        public static Scene GetSceneAt(int index) => Scenes[index];
    }
}
namespace Il2CppInterop.Runtime
{
    public static class Il2CppClassPointerStore<T> { public static IntPtr NativeClassPtr = IL2CPP.Class(typeof(T)); }
    public static class IL2CPP
    {
        static readonly List<Type> Types = new();
        public static IntPtr Class(Type type) { if (!Types.Contains(type)) Types.Add(type); return (IntPtr)(Types.IndexOf(type) + 1); }
        public static IntPtr il2cpp_object_get_class(IntPtr pointer) => Class(UnityEngine.Object.Instances[pointer].GetType());
        public static bool il2cpp_class_is_assignable_from(IntPtr parent, IntPtr child) => Types[(int)parent - 1].IsAssignableFrom(Types[(int)child - 1]);
    }
}
