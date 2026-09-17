using System.Text.Json;
using Enemies;
using ForgeEnemy.Native;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;

/// <summary>One case's world: a temporary installation laid out the way an installed package lays itself out
/// (`BepInEx/plugins/&lt;package&gt;/forge/enemies/*.json`), a started kernel with the provider's own registration in
/// it, and an enemy built the way the receiver builds one — damage limbs with their own health, a detection
/// component and an appearance component.
///
/// The documents are real files read by the production discovery, not objects handed to the parser, because the
/// convention that finds them (which folder, in which order, under which root) is half of what this suite states.
/// The application point is the production spawn path: `TrackSpawn` is what the game reaches through the
/// `EnemySync.OnSpawn` postfix, so a case that calls it exercises the same call the receiver makes.
///
/// Nothing here starts the game: the reader, the duplicate rules and the native writes are exercised against the
/// same doubles the other native-evidence suites compile, and the native calls themselves stay unexecuted.</summary>
internal sealed class Scene : IDisposable
{
    private readonly string _root;

    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    internal readonly List<string> Messages = new();
    internal bool Allowed = true;

    /// <summary>A case arms this to prove that a logger which throws cannot abort a spawn or a package load: the
    /// report is the host's, and the module's answer must not depend on it.</summary>
    internal bool ThrowOnReport;

    internal Scene(bool profileTypeReadable = true)
    {
        _root = Path.Combine(Path.GetTempPath(), "forge-enemy-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        Kernel.BeginWorld(1);
        // The combat and trigger contracts declare the bindings this provider's rows name, so they are registered
        // first, exactly as the host registers them.
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Module = new EnemyModule(Kernel, RuntimeLogLevel.Off, () => Allowed, Report, null,
            profileTypeReadable ? EnemyTypeReader.Read : static _ => null);
        Kernel.StartRuntime(static () => { });
    }

    private void Report(string message)
    {
        if (ThrowOnReport) throw new InvalidOperationException("the host logger refused the message");
        Messages.Add(message);
    }

    internal string Root => _root;

    /// <summary>Writes one document into a package's profile folder and returns the store the installed plugin
    /// would build from this root.</summary>
    internal EnemyProfileStore Install(string package, string file, string json)
    {
        string folder = Path.Combine(_root, "plugins", package, "forge", "enemies");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file), json);
        return EnemyProfileStore.Discover(_root);
    }

    /// <summary>An agent of one enemy type with two limbs and both optional components, all at authored values a
    /// case can compare against afterwards.</summary>
    internal static EnemyAgent NewEnemy(uint typeId = 11, ushort globalId = 7, long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = globalId, Pointer = new(pointer) };
        actor.EnemyData = new GameData.EnemyDataBlock { persistentID = typeId };
        actor.Damage = new() { Owner = actor, Pointer = new(pointer + 100) };
        actor.Damage.DamageLimbs = new[]
        {
            new Dam_EnemyDamageLimb { m_base = actor.Damage, m_limbID = 0, Pointer = new(pointer + 200), m_health = 50f, m_healthMax = 200f },
            new Dam_EnemyDamageLimb { m_base = actor.Damage, m_limbID = 1, Pointer = new(pointer + 201), m_health = 100f, m_healthMax = 100f }
        };
        var ai = new EnemyAI { Pointer = new(pointer + 300) };
        ai.m_enemyAgent = actor;
        ai.m_detection = new EnemyDetection { m_ai = ai };
        actor.AI = ai;
        actor.Appearance = new EnemyAppearance { m_owner = actor, Pointer = new(pointer + 400) };
        return actor;
    }

    /// <summary>A document with the given enemies, in the shape the website's editor exports: one object per
    /// enemy type under `enemies`.</summary>
    internal static string Bag(params string[] enemies)
        => "{ \"schemaVersion\": 1, \"enemies\": [ " + string.Join(",", enemies) + " ] }";

    /// <summary>An enemy for type 11, with the caller's attributes spliced into the enemy object itself. The
    /// caller writes either the attribute member — `"limbs": [...]` — or that member already wrapped in braces;
    /// either way the enemy differs from an accepted one in exactly the rule the case states.</summary>
    internal static string For11(string body)
    {
        if (body.Length == 0) return "{ \"enemyType\": 11 }";
        string attributes = body.Trim();
        // A wrapped body contributes its members, not itself: appended as-is it would be an unnamed value.
        if (attributes.Length > 1 && attributes[0] == '{' && attributes[^1] == '}')
            attributes = attributes.Substring(1, attributes.Length - 2).Trim();
        return "{ \"enemyType\": 11, " + attributes + " }";
    }

    /// <summary>One enemy of an arbitrary type, for the cases that need a second type or a second document.</summary>
    internal static string For(uint enemyTypeId, string body = "")
        => body.Length == 0 ? "{ \"enemyType\": " + enemyTypeId + " }" : "{ \"enemyType\": " + enemyTypeId + ", " + body + " }";
    /// <summary>The single rejection the store holds, asserted by the caller through `T`.</summary>
    internal static string CodeOf(EnemyProfileStore store)
    {
        T.Check(store.Rejections.Count == 1, "expected one rejection, saw " + store.Rejections.Count);
        return store.Rejections[0].Code;
    }

    public void Dispose()
    {
        Module.Dispose();
        Kernel.StopRuntime();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}

internal static class T
{
    internal sealed record CheckRow(string Id, bool Passed, string Detail);
    internal static readonly List<CheckRow> Rows = new();
    internal static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    internal static void Equal(string expected, string actual, string what)
        => Check(expected == actual, what + ": expected `" + expected + "`, saw `" + actual + "`");
    internal static void Near(float expected, float actual, string what)
        => Check(Math.Abs(expected - actual) < 0.001f, what + ": expected " + expected + ", saw " + actual);
    internal static void Case(string id, Action test)
    {
        try { test(); Rows.Add(new(id, true, "passed")); }
        catch (Exception e) { Rows.Add(new(id, false, e.ToString())); Console.Error.WriteLine("FAIL " + id + ": " + e.Message); }
        finally { SNetwork.SNet.IsMaster = true; }
    }
    internal static JsonElement Row(string json, string name) => JsonDocument.Parse(json).RootElement.GetProperty(name);
}
