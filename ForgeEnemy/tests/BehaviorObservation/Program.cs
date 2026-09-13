using System.Text.Json;
using Agents;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;
using SNetwork;

if (args.Length != 1) { Console.Error.WriteLine("Usage: BehaviorObservation <report.json>"); return 2; }
var rows = new List<Row>();
void Case(string id, Action test)
{
    try { test(); rows.Add(new(id, true, "passed")); }
    catch (Exception error) { rows.Add(new(id, false, error.ToString())); Console.Error.WriteLine("FAIL " + id + ": " + error.Message); }
    finally { SNet.IsMaster = true; }
}
void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

Case("snapshot.exact-native-state", () =>
{
    using var s = new Scene(); var x = s.Read() ?? throw new InvalidOperationException("Behavior snapshot unavailable.");
    Require(x.Reference == s.Ref && x.NativeState == (int)ES_StateEnum.PathMove, "State/ref mismatch.");
    Require(x.HasValidTarget && x.ActiveAbility == (int)AgentAbility.Primary && x.CanTriggerAbilities && !x.Invisible,
        "Behavior values changed or were inferred.");
});
Case("snapshot.no-mutation", () =>
{
    using var s = new Scene(); var before = s.Enemy.Damage.Sends; _ = s.Read();
    Require(s.Enemy.Damage.Sends == before, "Read-only behavior snapshot mutated gameplay.");
});
Case("identity.wrong-reference", () =>
{
    using var s = new Scene();
    Require(EnemyBehaviorObserver.Read(s.Enemy, s.Ref with { Id = "gtfo.enemy:8" }) == null, "Wrong native ID rebound.");
});
Case("identity.old-life", () =>
{
    using var s = new Scene(); var token = s.Module.CaptureDespawn(s.Enemy); s.Module.CompleteDespawn(token);
    var fresh = s.Module.TrackSpawn(s.Enemy);
    Require(s.Module.ObserveBehavior(s.Ref) == null && s.Module.ObserveBehavior(fresh) != null, "Old life survived behavior lookup.");
});
Case("identity.old-world", () =>
{
    using var s = new Scene(); s.Kernel.BeginWorld(2); var fresh = s.Module.TrackSpawn(s.Enemy);
    Require(s.Module.ObserveBehavior(s.Ref) == null && s.Module.ObserveBehavior(fresh) != null, "Old world survived behavior lookup.");
});
Case("gate.phase-denied", () =>
{
    using var s = new Scene(); s.Allowed = false; Require(s.Read() == null, "Phase gate was bypassed.");
});
Case("gate.client-denied", () =>
{
    using var s = new Scene(); SNet.IsMaster = false; Require(s.Read() == null, "Client read claimed host behavior authority.");
});
Case("lifecycle.dead-unavailable", () =>
{
    using var s = new Scene(); s.Enemy.Alive = false; Require(s.Read() == null, "Dead AI was exposed as active behavior.");
});
Case("components.missing-ai", () =>
{
    using var s = new Scene(); s.Enemy.AI = null!; Require(s.Read() == null, "Missing AI fabricated a snapshot.");
});
Case("components.missing-locomotion", () =>
{
    using var s = new Scene(); s.Enemy.Locomotion = null!; Require(s.Read() == null, "Missing locomotion fabricated a snapshot.");
});
Case("components.missing-abilities", () =>
{
    using var s = new Scene(); s.Enemy.Abilities = null!; Require(s.Read() == null, "Missing abilities fabricated a snapshot.");
});
Case("components.owner-mismatch", () =>
{
    using var s = new Scene(); s.Enemy.Locomotion.m_agent = Scene.NewEnemy(8, 20);
    Require(s.Read() == null, "Foreign locomotion owner was accepted.");
});
Case("read.state-changed-mid-snapshot", () =>
{
    using var s = new Scene(); var reads = 0;
    s.Enemy.Locomotion.OnStateRead = () => { if (++reads == 2) s.Enemy.Locomotion.CurrentStateEnum = ES_StateEnum.ScoutScream; };
    Require(s.Read() == null, "Unstable state was returned as a stable snapshot.");
});
Case("read.target-changed-mid-snapshot", () =>
{
    using var s = new Scene(); var reads = 0;
    s.Enemy.OnTargetRead = () => { if (++reads == 2) s.Enemy.m_hasValidTarget = false; };
    Require(s.Read() == null, "Unstable target state was returned.");
});
Case("read.ability-changed-mid-snapshot", () =>
{
    using var s = new Scene(); var reads = 0;
    s.Enemy.Abilities.OnAbilityRead = () => { if (++reads == 2) s.Enemy.Abilities.ActiveAbility = AgentAbility.Secondary; };
    Require(s.Read() == null, "Unstable active ability was returned.");
});
Case("read.invisibility-changed-mid-snapshot", () =>
{
    using var s = new Scene(); var reads = 0;
    s.Enemy.OnInvisibleRead = () => { if (++reads == 2) s.Enemy.Invisible = true; };
    Require(s.Read() == null, "Unstable invisibility was returned.");
});
Case("read.getter-exception-fails-closed", () =>
{
    using var s = new Scene(); s.Enemy.OnInvisibleRead = () => throw new IOException("synthetic native getter");
    Require(s.Read() == null, "Native getter exception escaped the observation boundary.");
});
Case("lifecycle.pre-start", () =>
{
    using var s = new Scene(start: false); Require(s.Read() == null, "Pre-start behavior was accepted.");
});
Case("lifecycle.stopped", () =>
{
    using var s = new Scene(); s.Kernel.StopRuntime(); Require(s.Read() == null, "Stopped runtime returned behavior.");
});
Case("snapshot.frozen", () =>
{
    using var s = new Scene(); var x = s.Read()!; s.Enemy.Locomotion.CurrentStateEnum = ES_StateEnum.Dead;
    Require(x.NativeState == (int)ES_StateEnum.PathMove, "Snapshot retained mutable native state.");
});
Case("thread.off-thread-rejected", () =>
{
    using var s = new Scene(); Exception? caught = null;
    try { Task.Run(() => s.Module.ObserveBehavior(s.Ref)).GetAwaiter().GetResult(); }
    catch (RuntimeContractException error) { caught = error; }
    Require(caught is RuntimeContractException contract && contract.Code == "wrong-thread", "Off-thread read did not reject.");
});
Case("values.explicit-false-and-none", () =>
{
    using var s = new Scene(); s.Enemy.m_hasValidTarget = false; s.Enemy.Abilities.ActiveAbility = AgentAbility.None;
    s.Enemy.Abilities.CanTriggerAbilities = false; s.Enemy.Invisible = true;
    var x = s.Read() ?? throw new InvalidOperationException("Behavior snapshot unavailable.");
    Require(!x.HasValidTarget && x.ActiveAbility == 0 && !x.CanTriggerAbilities && x.Invisible,
        "False/none values were normalized or inferred.");
});

int failed = rows.Count(x => !x.Passed);
var report = new { schemaVersion = 1, verification = "E4-domain-native-behavior-observation-with-game-doubles",
    gameExecuted = false, multiplayerExecuted = false, passed = rows.Count - failed, failed, checks = rows };
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {rows.Count - failed}/{rows.Count} Enemy behavior observation cases; no GTFO execution.");
return failed == 0 ? 0 : 1;

internal sealed record Row(string Id, bool Passed, string Detail);
