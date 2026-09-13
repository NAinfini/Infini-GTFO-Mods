namespace BepInEx.Configuration;

public sealed class ConfigEntry<T>
{
    public T Value { get; set; }
    internal ConfigEntry(T value) { Value = value; }
}
public sealed class ConfigDescription
{ public ConfigDescription(string description, object acceptable) { } }
public sealed class AcceptableValueRange<T>
{ public AcceptableValueRange(T minimum, T maximum) { } }
public sealed class ConfigFile
{
    internal readonly Dictionary<string, object> Preset = new();
    internal readonly Dictionary<string, object> Entries = new();
    public ConfigEntry<T> Bind<T>(string section, string key, T value, object description)
    {
        string id = section + "." + key; Probe.Call("bind:" + id);
        if (Entries.TryGetValue(id, out var prior)) return (ConfigEntry<T>)prior;
        var entry = new ConfigEntry<T>(Preset.TryGetValue(id, out var selected) ? (T)selected : value);
        Entries.Add(id, entry); return entry;
    }
}
