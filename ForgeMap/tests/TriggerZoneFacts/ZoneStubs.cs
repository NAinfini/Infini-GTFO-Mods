using System;
using ForgeRuntime.Framework;

// The two production symbols the map-object half names and this project does not compile: the provider identity its
// contract rows are built from, and the session half it composes on the one registration. They are stand-ins in the
// shape the other focused projects use — a source here reads exactly the members it touches, and a case that
// asserts a value is asserting it against the value `ForgeMap/ModuleDefinition.cs` carries.

namespace ForgeMap
{
    /// <summary>The Map provider identity, with the values `ForgeMap/ModuleDefinition.cs` carries.</summary>
    public static class ModuleDefinition
    {
        public const string ProviderId = "forge.module.gtfo.map";
        public const string Version = "0.1.0";
    }

    /// <summary>The expedition session half, which no case in this project triggers: a module that publishes
    /// nothing about an expedition end is exactly what a world with no expedition end in it has, so the stand-in
    /// exists to be constructed and disposed and nothing else.</summary>
    public sealed class ExpeditionModule : IDisposable
    {
        public ExpeditionModule(RuntimeModuleHandle registration, RuntimeKernel kernel, Func<bool> authority, Action<string> report)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentNullException.ThrowIfNull(kernel);
            ArgumentNullException.ThrowIfNull(authority);
            ArgumentNullException.ThrowIfNull(report);
        }

        public void Dispose() { }
    }
}
