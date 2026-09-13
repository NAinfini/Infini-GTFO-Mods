using BepInEx.Configuration;
using ForgeRuntime;

int checks = 0, failures = 0, sequence = 0;
string root = Path.Combine(Path.GetTempPath(), "forge-config-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Case(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}
ConfigFile Config(string? mode)
{
    var text = (mode == null ? "" : "[Runtime]\nMode = " + mode + "\n")
        + "\n[Framework]\nPlanPath = content/plan.json\nAllowedPermissions = gtfo.enemy.health.read,gtfo.enemy.health.write\n";
    string path = Path.Combine(root, (++sequence) + ".cfg"); File.WriteAllText(path, text);
    return new ConfigFile(path, false) { SaveOnConfigSet = false };
}
try
{
    foreach (var (input, expected) in new (string?, RuntimeMode)[] {
        (null, RuntimeMode.Authoring), ("Off", RuntimeMode.Off), ("Play", RuntimeMode.Play),
        ("Authoring", RuntimeMode.Authoring), ("off", RuntimeMode.Off), ("  Play  ", RuntimeMode.Play),
        ("0", RuntimeMode.Off), ("1", RuntimeMode.Authoring), ("2", RuntimeMode.Play) })
    {
        Case("existing config " + (input ?? "default"), () => {
            var config = Config(input); var before = File.ReadAllText(config.ConfigFilePath);
            RuntimeSettings.Bind(config);
            Check(RuntimeSettings.Mode == expected, "configured mode changed");
            Check(RuntimeSettings.PlanPath.Value == "content/plan.json", "plan path changed");
            Check(RuntimeSettings.AllowedPermissions.Value == "gtfo.enemy.health.read,gtfo.enemy.health.write", "grants changed");
            Check(config.Keys.Count == 3 && config.Keys.All(k => k.Section is "Runtime" or "Framework"), "host bound diagnostic keys");
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
    foreach (string mode in new[] { "Off", "Play", "Authoring", "0" })
        Case("real saved config roundtrip " + mode, () => {
            var config = Config(mode); config.SaveOnConfigSet = true;
            RuntimeSettings.Bind(config); var expected = RuntimeSettings.Mode; config.Save();
            var reopened = new ConfigFile(config.ConfigFilePath, false) { SaveOnConfigSet = false };
            RuntimeSettings.Bind(reopened);
            Check(RuntimeSettings.Mode == expected, "saved config changed mode");
            Check(RuntimeSettings.AllowedPermissions.Value == "gtfo.enemy.health.read,gtfo.enemy.health.write", "saved config changed permissions");
            Check(reopened.Keys.Count == 3, "saved host config includes diagnostics");
        });
    Case("empty plan and permissions remain empty", () => {
        var path = Path.Combine(root, "empty.cfg"); File.WriteAllText(path, "[Runtime]\nMode = Off\n");
        var config = new ConfigFile(path, false) { SaveOnConfigSet = false }; RuntimeSettings.Bind(config);
        Check(RuntimeSettings.Mode == RuntimeMode.Off, "Off became enabled");
        Check(RuntimeSettings.PlanPath.Value == "" && RuntimeSettings.AllowedPermissions.Value == "", "host auto-selected plan or permissions");
    });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Real BepInEx configuration: {checks} assertions passed; {failures} scenarios failed.");
Console.WriteLine("Only temporary configuration fixtures were read/written; installed profiles unchanged.");
Environment.ExitCode = failures == 0 ? 0 : 1;
