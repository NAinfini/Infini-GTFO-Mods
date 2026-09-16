using System;

// The one interop type the production read names: the il2cpp struct array the objective machine keeps its item
// tables in. The real one needs the game's own runtime and cannot be loaded by a plain test host, so the focused
// build declares the two members the read uses. The mirror project (`ForgeMap.Native.csproj`) compiles the same
// source against the real interop, so a signature that drifts still fails a build.

namespace Il2CppInterop.Runtime.InteropTypes.Arrays;

/// <summary>An il2cpp array of value-type elements, as far as the read uses one.</summary>
public sealed class Il2CppStructArray<T> where T : struct
{
    private readonly T[] _items;

    public Il2CppStructArray(int length) => _items = new T[length];
    public Il2CppStructArray(T[] items) => _items = items ?? Array.Empty<T>();

    public int Length => _items.Length;
    public T this[int index] { get => _items[index]; set => _items[index] = value; }
}
