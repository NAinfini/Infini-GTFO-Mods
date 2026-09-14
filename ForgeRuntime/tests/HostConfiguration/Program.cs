using BepInEx.Configuration;
using ForgeRuntime;
using ForgeRuntime.Framework;

int checks = 0, failures = 0, sequence = 0;
string root = Path.Combine(Path.GetTempPath(), "forge-config-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Case(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}
ConfigFile Config(string? mode, string? level = null)
{
    var text = (mode == null ? "" : "[Runtime]\nMode = " + mode + "\n")
        + (level == null ? "" : "\n[Logging]\nLevel = " + level + "\n");
    string path = Path.Combine(root, (++sequence) + ".cfg"); File.WriteAllText(path, text);
    return new ConfigFile(path, false) { SaveOnConfigSet = false };
}
try
{
    foreach (var (input, expected) in new (string?, RuntimeMode)[] {
        (null, RuntimeMode.Play), ("Off", RuntimeMode.Off), ("Play", RuntimeMode.Play),
        ("Authoring", RuntimeMode.Authoring), ("off", RuntimeMode.Off), ("  Play  ", RuntimeMode.Play),
        ("0", RuntimeMode.Off), ("1", RuntimeMode.Authoring), ("2", RuntimeMode.Play) })
    {
        Case("existing config " + (input ?? "default"), () => {
            var config = Config(input); var before = File.ReadAllText(config.ConfigFilePath);
            RuntimeSettings.Bind(config);
            Check(RuntimeSettings.Mode == expected, "configured mode changed");
            Check(config.Keys.Count == 2 && config.Keys.All(k => k.Section is "Runtime" or "Logging"), "host bound diagnostic keys");
            Check(RuntimeSettings.LogLevel == RuntimeLogLevel.Error, "absent Logging.Level did not default to error");
            Check(File.ReadAllText(config.ConfigFilePath) == before, "binding unexpectedly rewrote input fixture");
        });
    }
    foreach (string input in new[] { "77", "-1", "Of", "", "Off, Play", "true", "Disabled" })
    {
        Case("reject invalid mode '" + input + "'", () => {
            var config = Config(input); Exception? error = null;
            try { RuntimeSettings.Bind(config); } catch (InvalidOperationException caught) { error = caught; }
            Check(error != null, "invalid mode silently selected a default or combined enum flags");
        });
    }
    foreach (var (input, expected) in new (string, RuntimeLogLevel)[] {
        ("off", RuntimeLogLevel.Off), ("error", RuntimeLogLevel.Error), ("info", RuntimeLogLevel.Info), ("  Info ", RuntimeLogLevel.Info) })
        Case("logging level " + input.Trim(), () => {
            var config = Config("Play", input); RuntimeSettings.Bind(config);
            Check(RuntimeSettings.LogLevel == expected && RuntimeSettings.Mode == RuntimeMode.Play, "configured log level changed");
        });
    foreach (string input in new[] { "trace", "Trace", "warn", "", "2", "error,info" })
        Case("reject invalid logging level '" + input + "'", () => {
            var config = Config("Play", input); Exception? error = null;
            try { RuntimeSettings.Bind(config); } catch (InvalidOperationException caught) { error = caught; }
            Check(error != null && RuntimeSettings.LogLevel == RuntimeLogLevel.Off, "invalid log level selected a default or reached trace");
        });
    foreach (string mode in new[] { "Off", "Play", "Authoring", "0" })
        Case("real saved config roundtrip " + mode, () => {
            var config = Config(mode, "info"); config.SaveOnConfigSet = true;
            RuntimeSettings.Bind(config); var expected = RuntimeSettings.Mode; config.Save();
            var reopened = new ConfigFile(config.ConfigFilePath, false) { SaveOnConfigSet = false };
            RuntimeSettings.Bind(reopened);
            Check(RuntimeSettings.Mode == expected, "saved config changed mode");
            Check(RuntimeSettings.LogLevel == RuntimeLogLevel.Info, "saved config changed log level");
            Check(reopened.Keys.Count == 2, "saved host config includes diagnostics");
        });
    Case("default logging level is written as error", () => {
        var config = Config("Play"); config.SaveOnConfigSet = true; RuntimeSettings.Bind(config); config.Save();
        Check(File.ReadAllText(config.ConfigFilePath).Contains("[Logging]", StringComparison.Ordinal)
            && File.ReadAllLines(config.ConfigFilePath).Any(line => line.Trim() == "Level = error"), "saved config did not record the error default");
    });
    Case("bare Off config binds no extra keys", () => {
        var path = Path.Combine(root, "empty.cfg"); File.WriteAllText(path, "[Runtime]\nMode = Off\n");
        var config = new ConfigFile(path, false) { SaveOnConfigSet = false }; RuntimeSettings.Bind(config);
        Check(RuntimeSettings.Mode == RuntimeMode.Off, "Off became enabled");
        Check(config.Keys.Count == 2, "host bound diagnostic keys");
    });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Real BepInEx configuration: {checks} assertions passed; {failures} scenarios failed.");
Console.WriteLine("Only temporary configuration fixtures were read/written; installed profiles unchanged.");
Environment.ExitCode = failures == 0 ? 0 : 1;
