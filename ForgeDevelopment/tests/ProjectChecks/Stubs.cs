namespace BepInEx
{
    public static class Paths
    {
        public static string BepInExRootPath { get; set; } = "";
        public static string ConfigPath { get; set; } = "";
        public static string PluginPath { get; set; } = "";
    }
}

namespace BepInEx.Unity.IL2CPP
{
    public sealed class PluginMetadata
    {
        public string GUID { get; init; } = "";
        public string Name { get; init; } = "";
        public SemanticVersioning.Version Version { get; init; } = new(0, 0, 0);
    }

    public sealed class PluginInfo
    {
        public PluginMetadata Metadata { get; init; } = new();
    }

    public sealed class IL2CPPChainloader
    {
        public static IL2CPPChainloader Instance { get; } = new();
        public Dictionary<string, PluginInfo> Plugins { get; } = new(StringComparer.Ordinal);
    }
}

namespace ForgeDevelopment.Native
{
    internal sealed class TestSetting<T>
    {
        internal TestSetting(T value) => Value = value;
        internal T Value { get; set; }
    }

    internal static partial class Settings
    {
        internal static TestSetting<string> ProjectManifest { get; } = new("");
    }

    internal static class RuntimeDiagnostics
    {
        internal static string Number(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
