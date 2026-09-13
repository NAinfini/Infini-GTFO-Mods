using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ForgeDevelopment.Native;

// A cooperative scene walk replaces repeated global component enumerations.
// Assets not attached to a scene are still inventoried by the asset-specific stages.
internal sealed class SceneInventory
{
    private sealed record Bucket(Type Type, IntPtr NativeClass, List<Object> Values);
    private static Bucket For<T>() where T : Object
    {
        var klass = Il2CppClassPointerStore<T>.NativeClassPtr;
        if (klass == IntPtr.Zero) throw new InvalidOperationException("Scene inventory type unavailable: " + typeof(T).FullName);
        return new(typeof(T), klass, new());
    }
    private readonly Bucket[] _components =
    {
        For<Component>(), For<NavMeshAgent>(), For<Renderer>(), For<Camera>(), For<Light>(),
        For<ParticleSystem>(), For<AudioSource>(), For<Animator>(), For<Collider>(), For<Rigidbody>(),
        For<Canvas>(), For<CanvasRenderer>(), For<SkinnedMeshRenderer>()
    };
    private readonly List<Object> _objects = new();
    private readonly Dictionary<IntPtr, Bucket[]> _classes = new();

    internal IEnumerable<byte> Capture(Scene persistentScene)
    {
        var scenes = new List<Scene>();
        for (int i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i));
        if (persistentScene.IsValid() && !scenes.Exists(scene => scene.handle == persistentScene.handle)) scenes.Add(persistentScene);
        var pending = new Stack<(Transform Transform, int Child)>();
        foreach (var scene in scenes)
        {
            if (!scene.IsValid() || !scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                pending.Push((root.transform, -1));
                while (pending.Count > 0)
                {
                    yield return 0;
                    var (transform, child) = pending.Pop();
                    if (transform == null) continue;
                    if (child == -1)
                    {
                        var go = transform.gameObject;
                        _objects.Add(go);
                        foreach (var component in go.GetComponents<Component>())
                        {
                            yield return 0;
                            if (component == null) continue;
                            var klass = IL2CPP.il2cpp_object_get_class(component.Pointer);
                            if (!_classes.TryGetValue(klass, out var buckets))
                            {
                                var matching = new List<Bucket>();
                                foreach (var bucket in _components)
                                    if (IL2CPP.il2cpp_class_is_assignable_from(bucket.NativeClass, klass)) matching.Add(bucket);
                                _classes.Add(klass, buckets = matching.ToArray());
                            }
                            foreach (var bucket in buckets) bucket.Values.Add(component);
                        }
                        child = 0;
                    }
                    if (child >= transform.childCount) continue;
                    pending.Push((transform, child + 1));
                    pending.Push((transform.GetChild(child), -1));
                }
            }
        }
    }
    internal bool TryGet(Type type, out List<Object> values)
    {
        if (type == typeof(GameObject)) { values = _objects; return true; }
        foreach (var bucket in _components)
            if (bucket.Type == type) { values = bucket.Values; return true; }
        values = null!; return false;
    }
}
