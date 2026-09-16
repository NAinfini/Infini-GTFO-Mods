using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Iced.Intel;

// Offline extractor for the enemy damage window. It maps the Il2CppDumper build map onto PE RVAs, disassembles
// bounded regions around the damage entry points and their call sites, and writes ForgeEnemy/evidence/
// e10-damage-window.json. Nothing here executes game code, loads the game or starts a process.
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: DamageWindow <GTFO game root> <dump.cs> <output.json>");
    return 2;
}
string gameRoot = Path.GetFullPath(args[0]);
string dumpPath = Path.GetFullPath(args[1]);
string outputPath = Path.GetFullPath(args[2]);
string nativePath = Path.Combine(gameRoot, "GameAssembly.dll");
if (!File.Exists(nativePath)) { Console.Error.WriteLine("Missing " + nativePath); return 2; }
if (!File.Exists(dumpPath)) { Console.Error.WriteLine("Missing " + dumpPath); return 2; }

// Bounded windows, never a whole-section sweep: enough around each call site to read the argument setup and
// the return-value branch, and enough per entry to reach the limb branch.
const int CallWindow = 0x60;
const int EntryWindow = 0x180;
const int LongEntryWindow = 0x500;

// The entry points whose whole native body is decoded and whose RVA NativeHealth reviews. `StopAtReturn` false
// decodes the whole window linearly: the three gate bodies below return early on their first branch, and the
// branch that consults the network flag only exists after that return.
var reviewed = new (uint Rva, string Label, int Window, bool StopAtReturn)[]
{
    (0x161F790, "Dam_SyncedDamageBase.SendSetHealth", EntryWindow, true),
    (0x1380D50, "Dam_EnemyDamageBase.ReceiveSetHealth", EntryWindow, true),
    (0x137E570, "Dam_EnemyDamageBase.ProcessReceivedDamage", LongEntryWindow, true),
    (0x161F4E0, "Dam_SyncedDamageBase.SendLocally", EntryWindow, false),
    (0x161F5A0, "Dam_SyncedDamageBase.SendPacket", EntryWindow, false),
    (0x16201B0, "Dam_SyncedDamageBase.Setup", EntryWindow, false),
    (0x503EF0, "Dam_EnemyDamageBase.get_DamageBaseOwner", EntryWindow, true),
};

// The Il2Cpp types the evidence covers: method bodies, field offsets and enum members of the damage path.
var wanted = new HashSet<string>(StringComparer.Ordinal)
{
    "Dam_EnemyDamageBase", "Dam_SyncedDamageBase", "Dam_EnemyDamageLimb", "pBulletDamageData", "pFullDamageData",
    "pExplosionDamageData", "pSmallDamageData", "pSetHealthData", "pAddHealthData", "pMiniDamageData",
    "pMediumDamageData", "eLimbDamageType", "eLimbDestructionType", "ES_HitreactType", "DamageNoiseLevel",
};

// The methods the evidence file declares, with the role each one plays in the damage window and the Il2Cpp
// parameter names NativeEvidence checks against the interop assembly.
var targets = new (string Type, string Name, string Role, string[] Parameters)[]
{
    ("Dam_EnemyDamageBase", "ProcessReceivedDamage", "damage-window",
        new[] { "damage", "damageSource", "position", "direction", "hitreact", "tryForceHitreact", "limbID", "staggerDamageMulti", "damageNoiseLevel", "gearCategoryId" }),
    ("Dam_EnemyDamageBase", "BulletDamage", "enemy-entry",
        new[] { "dam", "sourceAgent", "position", "direction", "normal", "allowDirectionalBonus", "limbID", "staggerMulti", "precisionMulti", "gearCategoryId" }),
    ("Dam_EnemyDamageBase", "MeleeDamage", "enemy-entry",
        new[] { "dam", "sourceAgent", "position", "direction", "limbID", "staggerMulti", "precisionMulti", "backstabberMulti", "sleeperMulti", "skipLimbDestruction", "damageNoiseLevel", "gearCategoryId" }),
    ("Dam_EnemyDamageBase", "ExplosionDamage", "enemy-entry",
        new[] { "dam", "sourcePos", "force", "limbID", "gearCategoryId" }),
    ("Dam_EnemyDamageBase", "ReceiveBulletDamage", "packet-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceiveMeleeDamage", "packet-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceiveExplosionDamage", "packet-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceiveFireDamage", "packet-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceiveFreezeDamage", "packet-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceiveGlueDamage", "packet-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceivePushDamage", "packet-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceiveSetHealth", "replication-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceiveDestroyLimb", "replication-receiver", new[] { "data" }),
    ("Dam_EnemyDamageBase", "ReceiveExplosionForce", "replication-receiver", new[] { "data" }),
    ("Dam_SyncedDamageBase", "ReceiveAddHealth", "replication-receiver", new[] { "data" }),
    ("Dam_SyncedDamageBase", "AddHealth", "replication-sender", new[] { "addHealth", "sourceAgent" }),
    ("Dam_SyncedDamageBase", "SendSetHealth", "replication-sender", new[] { "health" }),
    ("Dam_SyncedDamageBase", "SendLocally", "damage-gate", Array.Empty<string>()),
    ("Dam_SyncedDamageBase", "SendPacket", "damage-gate", Array.Empty<string>()),
    ("Dam_EnemyDamageBase", "SendDestroyLimb", "replication-sender", new[] { "limbID", "destructionEventData" }),
    ("Dam_EnemyDamageBase", "Setup", "setup", new[] { "owner", "health", "healthMax" }),
    ("Dam_SyncedDamageBase", "Setup", "setup", Array.Empty<string>()),
    ("Dam_EnemyDamageBase", "SetupPackages", "packet-registration", new[] { "replicator" }),
    ("Dam_SyncedDamageBase", "SetupPackages", "packet-registration", new[] { "replicator" }),
    ("Dam_EnemyDamageBase", "UpdateWithAuthority", "authority-update", Array.Empty<string>()),
    ("Dam_EnemyDamageBase", "ProcessReceivedFireDamage", "damage-window", new[] { "disMul" }),
    ("Dam_EnemyDamageBase", "OnApplyDamageOverTimeModifier", "damage-window", new[] { "damage" }),
    ("Dam_EnemyDamageBase", "CheckDestruction", "limb-receiver",
        new[] { "limb", "localPos", "direction", "limbID", "severity", "tryForceHitreact", "hitreact" }),
    ("Dam_EnemyDamageBase", "InstantDead", "death", new[] { "allowRevive" }),
    ("Dam_SyncedDamageBase", "GetHealthRel", "health-readback", Array.Empty<string>()),
    ("Dam_SyncedDamageBase", "get_Health", "health-readback", Array.Empty<string>()),
    ("Dam_SyncedDamageBase", "get_HealthMax", "health-readback", Array.Empty<string>()),
    ("Dam_SyncedDamageBase", "get_DamageMax", "health-readback", Array.Empty<string>()),
    ("Dam_SyncedDamageBase", "RegisterDamage", "health-readback", new[] { "dam" }),
    ("Dam_SyncedDamageBase", "WillDamageKill", "health-readback", new[] { "dam" }),
    ("Dam_EnemyDamageLimb", "DoDamage", "limb-receiver", new[] { "dam" }),
    ("Dam_EnemyDamageLimb", "TestDamageModifiers", "limb-receiver", new[] { "dam", "precisionMulti" }),
};

// Il2Cpp signatures for the methods the frozen v2 specification already lists, so both files can be compared
// field by field instead of by hand. Methods the v2 specification omits are checked against the interop
// assembly by NativeEvidence using the dump declaration instead.
var frozenSignatures = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["Dam_EnemyDamageBase.ProcessReceivedDamage"] = "System.Boolean Dam_EnemyDamageBase::ProcessReceivedDamage(System.Single,Agents.Agent,UnityEngine.Vector3,UnityEngine.Vector3,ES_HitreactType,System.Boolean,System.Int32,System.Single,DamageNoiseLevel,System.UInt32)",
    ["Dam_EnemyDamageBase.ReceiveBulletDamage"] = "System.Void Dam_EnemyDamageBase::ReceiveBulletDamage(pBulletDamageData)",
    ["Dam_EnemyDamageBase.ReceiveMeleeDamage"] = "System.Void Dam_EnemyDamageBase::ReceiveMeleeDamage(pFullDamageData)",
    ["Dam_EnemyDamageBase.ReceiveExplosionDamage"] = "System.Void Dam_EnemyDamageBase::ReceiveExplosionDamage(pExplosionDamageData)",
    ["Dam_EnemyDamageBase.ReceiveFireDamage"] = "System.Void Dam_EnemyDamageBase::ReceiveFireDamage(pSmallDamageData)",
    ["Dam_EnemyDamageBase.ReceiveSetHealth"] = "System.Void Dam_EnemyDamageBase::ReceiveSetHealth(pSetHealthData)",
    ["Dam_EnemyDamageBase.ReceiveDestroyLimb"] = "System.Void Dam_EnemyDamageBase::ReceiveDestroyLimb(Dam_EnemyDamageBase/pDestroyLimbData)",
    ["Dam_EnemyDamageBase.SendDestroyLimb"] = "System.Void Dam_EnemyDamageBase::SendDestroyLimb(System.Int32,CharacterDestruction.sDestructionEventData)",
    ["Dam_EnemyDamageBase.Setup"] = "System.Void Dam_EnemyDamageBase::Setup(Enemies.EnemyAgent,System.Single,System.Single)",
    ["Dam_EnemyDamageBase.InstantDead"] = "System.Void Dam_EnemyDamageBase::InstantDead(System.Boolean)",
    ["Dam_SyncedDamageBase.SendSetHealth"] = "System.Void Dam_SyncedDamageBase::SendSetHealth(System.Single)",
    ["Dam_SyncedDamageBase.SendLocally"] = "System.Boolean Dam_SyncedDamageBase::SendLocally()",
    ["Dam_SyncedDamageBase.SendPacket"] = "System.Boolean Dam_SyncedDamageBase::SendPacket()",
    ["Dam_SyncedDamageBase.GetHealthRel"] = "System.Single Dam_SyncedDamageBase::GetHealthRel()",
    ["Dam_SyncedDamageBase.get_Health"] = "System.Single Dam_SyncedDamageBase::get_Health()",
    ["Dam_SyncedDamageBase.get_HealthMax"] = "System.Single Dam_SyncedDamageBase::get_HealthMax()",
    ["Dam_SyncedDamageBase.get_DamageMax"] = "System.Single Dam_SyncedDamageBase::get_DamageMax()",
    ["Dam_SyncedDamageBase.RegisterDamage"] = "System.Boolean Dam_SyncedDamageBase::RegisterDamage(System.Single)",
    ["Dam_SyncedDamageBase.WillDamageKill"] = "System.Boolean Dam_SyncedDamageBase::WillDamageKill(System.Single)",
    ["Dam_EnemyDamageLimb.DoDamage"] = "System.Boolean Dam_EnemyDamageLimb::DoDamage(System.Single)",
    ["Dam_EnemyDamageLimb.TestDamageModifiers"] = "System.Single Dam_EnemyDamageLimb::TestDamageModifiers(System.Single,System.Single)",
};

// Il2Cpp signatures for the remaining entries, derived from the dump declaration. NativeEvidence resolves each
// one against the interop assembly's own parameter list, so a wrong guess fails the audit.
var derivedSignatures = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["Dam_EnemyDamageBase.BulletDamage"] = "System.Void Dam_EnemyDamageBase::BulletDamage(System.Single,Agents.Agent,UnityEngine.Vector3,UnityEngine.Vector3,UnityEngine.Vector3,System.Boolean,System.Int32,System.Single,System.Single,System.UInt32)",
    ["Dam_EnemyDamageBase.MeleeDamage"] = "System.Void Dam_EnemyDamageBase::MeleeDamage(System.Single,Agents.Agent,UnityEngine.Vector3,UnityEngine.Vector3,System.Int32,System.Single,System.Single,System.Single,System.Single,System.Boolean,DamageNoiseLevel,System.UInt32)",
    ["Dam_EnemyDamageBase.ExplosionDamage"] = "System.Void Dam_EnemyDamageBase::ExplosionDamage(System.Single,UnityEngine.Vector3,UnityEngine.Vector3,System.Int32,System.UInt32)",
    ["Dam_EnemyDamageBase.ReceiveFreezeDamage"] = "System.Void Dam_EnemyDamageBase::ReceiveFreezeDamage(pSmallDamageData)",
    ["Dam_EnemyDamageBase.ReceiveGlueDamage"] = "System.Void Dam_EnemyDamageBase::ReceiveGlueDamage(pMiniDamageData)",
    ["Dam_EnemyDamageBase.ReceivePushDamage"] = "System.Void Dam_EnemyDamageBase::ReceivePushDamage(pFullDamageData)",
    ["Dam_EnemyDamageBase.ReceiveExplosionForce"] = "System.Void Dam_EnemyDamageBase::ReceiveExplosionForce(Dam_EnemyDamageBase/pExplosionForceData)",
    ["Dam_EnemyDamageBase.SetupPackages"] = "System.Void Dam_EnemyDamageBase::SetupPackages(SNet_Replicator)",
    ["Dam_SyncedDamageBase.Setup"] = "System.Void Dam_SyncedDamageBase::Setup()",
    ["Dam_SyncedDamageBase.SetupPackages"] = "System.Void Dam_SyncedDamageBase::SetupPackages(SNet_Replicator)",
    ["Dam_SyncedDamageBase.AddHealth"] = "System.Void Dam_SyncedDamageBase::AddHealth(System.Single,Agents.Agent)",
    ["Dam_SyncedDamageBase.ReceiveAddHealth"] = "System.Void Dam_SyncedDamageBase::ReceiveAddHealth(pAddHealthData)",
    ["Dam_EnemyDamageBase.UpdateWithAuthority"] = "System.Void Dam_EnemyDamageBase::UpdateWithAuthority()",
    ["Dam_EnemyDamageBase.ProcessReceivedFireDamage"] = "System.Void Dam_EnemyDamageBase::ProcessReceivedFireDamage()",
    ["Dam_EnemyDamageBase.OnApplyDamageOverTimeModifier"] = "System.Void Dam_EnemyDamageBase::OnApplyDamageOverTimeModifier(System.Single)",
    ["Dam_EnemyDamageBase.CheckDestruction"] = "System.Void Dam_EnemyDamageBase::CheckDestruction()",
};

// Parameter semantics. The first column is the Il2Cpp name from the dump, which is what NativeEvidence checks
// against the interop parameter list; the second is what the decoded instruction windows show about the value.
var parameterNotes = new Dictionary<string, string[]>(StringComparer.Ordinal)
{
    ["Dam_EnemyDamageBase.ProcessReceivedDamage"] = new[]
    {
        "requested damage before limb and armour modifiers",
        "attacker agent or null; the body reads it and passes it to a virtual owner call at 0x137E623",
        "hit position; callers pass a pointer to locals",
        "hit direction; callers pass a pointer to locals",
        "hit reaction class",
        "callers write 0 or 1 into the sixth native stack slot",
        "-1 means no specific limb; the fire, freeze and fire-tick callers pass -1",
        "stagger multiplier",
        "noise class of the hit",
        "gear category the damage came from",
    },
    ["Dam_EnemyDamageBase.BulletDamage"] = new[]
    {
        "requested damage", "attacker; packed as pBulletDamageData.source at 0x8", "hit position", "hit direction",
        "hit normal", "directional bonus flag", "hit limb; packed as pBulletDamageData.limbID at 0x10",
        "stagger multiplier", "precision multiplier", "gear category",
    },
    ["Dam_EnemyDamageBase.MeleeDamage"] = new[]
    {
        "requested damage", "attacker; packed as pFullDamageData.source at 0x8", "hit position", "hit direction",
        "hit limb; exported as pFullDamageData.limbID at 0x15", "stagger multiplier", "precision multiplier",
        "backstabber multiplier", "sleeper multiplier", "skip limb destruction", "noise class", "gear category",
    },
    ["Dam_EnemyDamageBase.ExplosionDamage"] = new[]
    {
        "requested damage", "explosion origin; there is no attacker parameter", "explosion force",
        "hit limb; exported as pExplosionDamageData.limbID at 0xA", "gear category",
    },
    ["Dam_EnemyDamageBase.ReceiveBulletDamage"] = new[] { "client bullet hit: damage, source, limbID and multipliers" },
    ["Dam_EnemyDamageBase.ReceiveMeleeDamage"] = new[] { "client melee hit: damage, source, limbID and multipliers" },
    ["Dam_EnemyDamageBase.ReceiveExplosionDamage"] = new[] { "client explosion hit: damage and limbID, no attacker field" },
    ["Dam_EnemyDamageBase.ReceiveFireDamage"] = new[] { "damage over time tick: damage and source" },
    ["Dam_EnemyDamageBase.ReceiveFreezeDamage"] = new[] { "damage over time tick: damage and source" },
    ["Dam_EnemyDamageBase.ReceiveGlueDamage"] = new[] { "glue amount only" },
    ["Dam_EnemyDamageBase.ReceivePushDamage"] = new[] { "push: damage, source and limbID" },
    ["Dam_EnemyDamageBase.ReceiveSetHealth"] = new[] { "SFloat16 health decoded from the set-health channel" },
    ["Dam_EnemyDamageBase.ReceiveDestroyLimb"] = new[] { "limb index and destruction payload" },
    ["Dam_EnemyDamageBase.ReceiveExplosionForce"] = new[] { "explosion force applied to the ragdoll" },
    ["Dam_SyncedDamageBase.ReceiveAddHealth"] = new[] { "SFloat16 health plus the healing agent" },
    ["Dam_SyncedDamageBase.AddHealth"] = new[] { "amount added, broadcast through the add-health channel", "the healer, carried as pAddHealthData.source" },
    ["Dam_SyncedDamageBase.SendSetHealth"] = new[] { "absolute health written to the synced field and broadcast" },
    ["Dam_EnemyDamageBase.SendDestroyLimb"] = new[] { "limb index", "destruction event payload" },
    ["Dam_EnemyDamageBase.Setup"] = new[] { "enemy that owns this damage receiver", "initial health", "maximum health" },
    ["Dam_EnemyDamageBase.SetupPackages"] = new[] { "packet registry the damage channels are registered with" },
    ["Dam_SyncedDamageBase.SetupPackages"] = new[] { "packet registry the damage channels are registered with" },
    ["Dam_EnemyDamageBase.OnApplyDamageOverTimeModifier"] = new[] { "damage-over-time modifier applied to a melee hit" },
    ["Dam_EnemyDamageBase.InstantDead"] = new[] { "whether a revive stays possible" },
    ["Dam_SyncedDamageBase.RegisterDamage"] = new[] { "damage registered against the damage maximum" },
    ["Dam_SyncedDamageBase.WillDamageKill"] = new[] { "damage checked against the remaining health" },
    ["Dam_EnemyDamageLimb.DoDamage"] = new[] { "damage applied to this limb after modifiers" },
    ["Dam_EnemyDamageLimb.TestDamageModifiers"] = new[] { "damage to modify", "precision multiplier from the hit" },
};

// What the decoded bodies and call sites show, per method. Quoted so the audit can cite the same text.
var methodNotes = new Dictionary<string, string[]>(StringComparer.Ordinal)
{
    ["Dam_EnemyDamageBase.ProcessReceivedDamage"] = new[]
    {
        "the whole native body is 0x137E570..0x137E9FF and ends in ret at 0x137E9FF",
        "calls Dam_SyncedDamageBase.RegisterDamage at 0x137E5F7",
        "reads the attacker argument and passes it to a virtual owner call at 0x137E623",
        "contains no read of DamageLimbs, so a direct call never breaks a specific limb",
    },
    ["Dam_EnemyDamageBase.ReceiveBulletDamage"] = new[]
    {
        "calls ProcessReceivedDamage at 0x137EF63 with the attacker and the packet limb on the stack",
        "tests the returned al at 0x137EF68 and skips the rest of the hit when it is zero",
    },
    ["Dam_EnemyDamageBase.ReceiveMeleeDamage"] = new[]
    {
        "calls ProcessReceivedDamage at 0x1380768, tests al at 0x138076D and branches to 0x13807A9 when zero",
    },
    ["Dam_EnemyDamageBase.ReceiveExplosionDamage"] = new[]
    {
        "calls ProcessReceivedDamage at 0x137F6D7; the explosion packet carries no attacker",
    },
    ["Dam_EnemyDamageBase.BulletDamage"] = new[]
    {
        "calls SendLocally at 0x137D4BD and SendPacket at 0x137D37F",
        "does not call ProcessReceivedDamage: the hit is applied by the receiver the packet layer invokes",
    },
    ["Dam_EnemyDamageBase.MeleeDamage"] = new[] { "calls SendLocally at 0x137E2F5 and SendPacket at 0x137E1AD" },
    ["Dam_EnemyDamageBase.ExplosionDamage"] = new[] { "calls SendLocally at 0x137DB85 and SendPacket at 0x137DABD" },
    ["Dam_EnemyDamageBase.SendDestroyLimb"] = new[] { "called by CheckDestruction at 0x137D64D; limb destruction is a channel of its own" },
    ["Dam_SyncedDamageBase.SendSetHealth"] = new[] { "tests the host flag at 0x161F796 and the packet send/local receive shapes in the body" },
    ["Dam_EnemyDamageBase.ReceiveSetHealth"] = new[] { "decodes SFloat16 through 0x181BB5AE0 and stores the result at +0x20" },
};

// The four questions this evidence file answers. Each conclusion names the rows and sites that support it and
// keeps every unresolved point in its own list instead of folding it into the answer.
var conclusions = new Dictionary<string, object>(StringComparer.Ordinal)
{
    ["q1-damage-entry-parameters"] = new
    {
        answer = "Dam_EnemyDamageBase.ProcessReceivedDamage takes (Single damage, Agent damageSource, Vector3 position, Vector3 direction, ES_HitreactType hitreact, Boolean tryForceHitreact, Int32 limbID, Single staggerDamageMulti, DamageNoiseLevel damageNoiseLevel, UInt32 gearCategoryId) and returns Boolean.",
        sources = new[] { "Dam_EnemyDamageBase.ProcessReceivedDamage", "Dam_EnemyDamageBase.BulletDamage", "Dam_EnemyDamageBase.MeleeDamage",
            "Dam_EnemyDamageBase.ExplosionDamage", "Dam_EnemyDamageBase.ReceiveBulletDamage", "Dam_EnemyDamageBase.ReceiveMeleeDamage",
            "Dam_EnemyDamageBase.ReceiveExplosionDamage", "Dam_EnemyDamageBase.ReceiveFireDamage" },
        callers = new[] { "Dam_EnemyDamageBase.ReceiveBulletDamage", "Dam_EnemyDamageBase.ReceiveMeleeDamage",
            "Dam_EnemyDamageBase.ReceiveExplosionDamage", "Dam_EnemyDamageBase.ReceiveFireDamage",
            "Dam_EnemyDamageBase.ReceiveFreezeDamage", "Dam_EnemyDamageBase.UpdateFireDamage" },
        detail = new[]
        {
            "The attacker is the second argument and is consumed by the native body, which passes it to a virtual call at 0x137E623.",
            "The hit limb is the seventh argument; -1 means no specific limb, which is what the fire, freeze and fire-tick callers pass.",
            "The by-value packet structs are passed by pointer: pBulletDamageData.source at 0x8, pBulletDamageData.limbID at 0x10, pFullDamageData.source at 0x8, pFullDamageData.limbID at 0x15.",
            "pExplosionDamageData has no source field, so an explosion can never name an attacker.",
            "Dam_EnemyDamageLimb is not an entry point: no scanned section calls DoDamage and the limb type calls nothing in the damage base.",
        },
    },
    ["q2-client-shot-on-host"] = new
    {
        answer = "supported",
        detail = new[]
        {
            "The bullet, melee, full and small damage packets all carry a pAgent source, and every receiver hands it to ProcessReceivedDamage.",
            "The damage kind is the receiver that was called: ReceiveBulletDamage, ReceiveMeleeDamage, ReceiveExplosionDamage, ReceiveFireDamage, ReceiveFreezeDamage, ReceivePushDamage and ReceiveGlueDamage are separate native methods.",
            "The hit limb travels as a byte in the same packet and reaches the seventh argument of ProcessReceivedDamage.",
            "ProcessReceivedDamage returns Boolean and the receiver branches on it, so an applied hit is distinguishable from a rejected one.",
        },
        unverified = new[]
        {
            "Which side submits the hit (the shooting client, the host, or both) is outside this window: the send sites decoded here are the damage entry points' own (BulletDamage 0x137D37F, FallDamage 0x161E377).",
            "The vtable slot invoked at 0x137E623 is resolved only through the slot numbering of the receive methods, not by reading a vtable.",
        },
    },
    ["q3-sentry-source"] = new
    {
        answer = "partially-supported",
        detail = new[]
        {
            "Bullet and melee damage always carry a source agent, so a turret that records itself or its deployer reaches Forge as that agent.",
            "Explosion damage structures cannot name any attacker.",
            "Nothing in the frozen metadata or the decoded windows distinguishes a turret from a player.",
        },
        unverified = new[]
        {
            "Who records the damage: it needs the tool or gear damage site that calls BulletDamage, which is outside this window.",
            "Whether a deployed turret passes its deployer as sourceAgent.",
        },
    },
    ["q4-forge-external-damage"] = new
    {
        answer = "Dam_EnemyDamageBase.BulletDamage(damage, sourceAgent, position, direction, normal, allowDirectionalBonus, limbID, staggerMulti, precisionMulti, gearCategoryId)",
        detail = new[]
        {
            "BulletDamage is declared on Dam_EnemyDamageBase with the limb id overload, so a Forge hit can name the limb; Dam_SyncedDamageBase.BulletDamage has no limb parameter.",
            "BulletDamage calls SendLocally and SendPacket, and the packet receiver is what applies the hit, so one call covers the host application and the replication of the hit.",
            "Read the actual damage back through get_Health (health at +0x20) and the bounds through get_HealthMax and get_DamageMax.",
            "Limb destruction is a separate channel: CheckDestruction calls SendDestroyLimb (site 0x137D64D), so the hit itself only decides whether the limb breaks.",
            "A client is not refused by the entry point. Dam_SyncedDamageBase.Setup (0x16201B0) writes m_onlyToMaster at +0x25 as (DamageBaseOwner == 1), and Dam_EnemyDamageBase.get_DamageBaseOwner (0x503EF0) is `mov eax,2; ret`, so every enemy damage receiver has the field zero. Both gates then answer true without reading the network flag: SendLocally (0x161F4E0) returns 1 before the flag load at [net+0xB8]+0xB9, and SendPacket (0x161F5A0) does the same before its `sete al`. The entry point therefore applies the hit locally and sends its own packet on a client as well; host-only submission has to be enforced by the caller.",
            "A rejected hit cannot be told apart from one the receiver's rules reduce to nothing. BulletDamage returns void, and the Boolean ProcessReceivedDamage returns is consumed inside the receive methods (ReceiveBulletDamage 0x137EF68, ReceiveMeleeDamage 0x138076D), so the only observable afterwards is get_Health.",
            "A null attacker reaches a receiver that ignores it. ProcessReceivedDamage's only use of the attacker argument is the virtual call at 0x137E623 through the method-pointer pair at owner-vtable + 0x240; the same encoding places ReceiveBulletDamage's declared slot 45 at +0x400, which makes +0x240 slot 17, the dump's slot for EnemyAgent.RegisterDamageInflictor(Agent). That body returns immediately when its argument answers `op_Inequality(inflictor, null) == false`, so a hit with no attacking agent is a shape the window accepts.",
        },
        unverified = new[]
        {
            "Where a hit sent by a non-host goes: the send group and the two send paths behind SendPacket are outside this window.",
        },
    },
};

string buildId = "";
// The frozen build id is the Steam build number the auditor compares against appmanifest_493520.acf; the
// game's own revision.txt is a different number and is recorded separately.
string manifest = Path.GetFullPath(Path.Combine(gameRoot, "..", "..", "appmanifest_493520.acf"));
if (File.Exists(manifest))
{
    var match = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"([^\"]+)\"");
    if (match.Success) buildId = match.Groups[1].Value;
}
string revision = File.Exists(Path.Combine(gameRoot, "revision.txt"))
    ? File.ReadAllText(Path.Combine(gameRoot, "revision.txt")).Trim() : "";
if (buildId.Length == 0) { Console.Error.WriteLine("Cannot read the Steam build id from " + manifest); return 2; }

var image = File.ReadAllBytes(nativePath);
var peOffset = BitConverter.ToInt32(image, 0x3c);
int sectionCount = BitConverter.ToInt16(image, peOffset + 6);
int optionalSize = BitConverter.ToInt16(image, peOffset + 20);
ulong imageBase = BitConverter.ToUInt64(image, peOffset + 24 + 24);
var sections = new List<(string Name, uint Virtual, uint VirtualSize, uint Raw, uint RawSize)>();
for (int i = 0; i < sectionCount; i++)
{
    int header = peOffset + 24 + optionalSize + i * 40;
    string name = Encoding.ASCII.GetString(image, header, 8).TrimEnd('\0');
    sections.Add((name, BitConverter.ToUInt32(image, header + 12), BitConverter.ToUInt32(image, header + 8),
        BitConverter.ToUInt32(image, header + 20), BitConverter.ToUInt32(image, header + 16)));
}
byte[] Read(uint rva, int length)
{
    foreach (var section in sections)
        if (rva >= section.Virtual && rva < section.Virtual + Math.Max(section.VirtualSize, section.RawSize))
        {
            uint delta = rva - section.Virtual;
            int available = (int)Math.Min((uint)length, section.RawSize - delta);
            var buffer = new byte[Math.Max(available, 1)];
            Array.Copy(image, (int)(section.Raw + delta), buffer, 0, available);
            return buffer;
        }
    return Array.Empty<byte>();
}
Iced.Intel.Decoder NewDecoder(byte[] bytes) => Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes));

// --- Il2CppDumper map: method RVAs, virtual slots, field offsets and enum members of the damage types ------
// One linear pass. A method RVA line is a "// RVA:" line that is not preceded by an attribute on the same line
// and it always names the next declaration in the type body.
var fields = new List<(string Type, string Declaration, int Offset)>();
var enumMembers = new List<(string Type, string Name, int Value)>();
var methods = new List<(string Type, string Name, string Signature, uint Rva, int Slot)>();
var lines = File.ReadAllLines(dumpPath);
var typePattern = new Regex(@"^(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(class|struct|enum)\s+([A-Za-z_][\w\.`]*)");
var fieldPattern = new Regex(@"^(?:public|private|protected|internal)\s+(?:static\s+|readonly\s+|const\s+|sealed\s+|override\s+)*([\w\.<>\[\]`]+)\s+([\w<>]+)\s*;.*?//\s*0x([0-9A-Fa-f]+)");
var methodPattern = new Regex(@"^(?:public|private|protected|internal)\s+(?:static\s+|virtual\s+|override\s+|sealed\s+|abstract\s+|extern\s+|new\s+)*([\w\.<>\[\]`,]+)\s+(\w+)\((.*?)\)\s*\{\s*\}\s*$");
var enumPattern = new Regex(@"^public const (\w+) (\w+) = (-?\d+);");
var rvaPattern = new Regex(@"^//\s*RVA:\s*0x([0-9A-Fa-f]+)");
string type = "";
bool enumType = false, fieldsSection = false;
(uint Rva, int Slot)? pendingRva = null;
foreach (var raw in lines)
{
    var line = raw.TrimEnd('\r');
    if (line.Length > 0 && line[0] != ' ' && line[0] != '\t')
    {
        var declared = typePattern.Match(line);
        if (declared.Success)
        {
            type = declared.Groups[2].Value;
            enumType = declared.Groups[1].Value == "enum";
            fieldsSection = false;
        }
        pendingRva = null;
        continue;
    }
    var trimmed = line.TrimStart();
    if (trimmed.StartsWith("//", StringComparison.Ordinal))
    {
        var rva = rvaPattern.Match(trimmed);
        if (rva.Success)
            pendingRva = (uint.Parse(rva.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null),
                trimmed.Contains("Slot:") ? int.Parse(trimmed[(trimmed.IndexOf("Slot:", StringComparison.Ordinal) + 5)..].Trim(), null) : -1);
        else if (trimmed.EndsWith(" Fields", StringComparison.Ordinal)) fieldsSection = true;
        else if (trimmed.EndsWith(" Methods", StringComparison.Ordinal) || trimmed.EndsWith(" Properties", StringComparison.Ordinal)
            || trimmed.EndsWith(" Nested Types", StringComparison.Ordinal)) fieldsSection = false;
        continue;
    }
    if (!wanted.Contains(type)) continue;
    if (enumType)
    {
        var member = enumPattern.Match(trimmed);
        if (member.Success)
            enumMembers.Add((type, member.Groups[2].Value, int.Parse(member.Groups[3].Value, null)));
        continue;
    }
    var method = methodPattern.Match(trimmed);
    if (method.Success)
    {
        if (pendingRva is not { } location)
            throw new InvalidDataException($"Method without an RVA attribute: {type}.{method.Groups[2].Value}");
        methods.Add((type, method.Groups[2].Value, method.Groups[1].Value + " " + method.Groups[2].Value + "(" + method.Groups[3].Value + ")",
            location.Rva, location.Slot));
        pendingRva = null;
        continue;
    }
    var field = fieldPattern.Match(trimmed);
    if (fieldsSection && field.Success)
        fields.Add((type, field.Groups[1].Value + " " + field.Groups[2].Value,
            int.Parse(field.Groups[3].Value, System.Globalization.NumberStyles.HexNumber, null)));
}
var methodByKey = new Dictionary<(string Type, string Name), (string Signature, uint Rva, int Slot)>();
foreach (var entry in methods) methodByKey[(entry.Type, entry.Name)] = (entry.Signature, entry.Rva, entry.Slot);
if (methodByKey.Count == 0) { Console.Error.WriteLine("No methods parsed from " + dumpPath); return 2; }
// The map is only usable when it resolves the reviewed entry points to the reviewed addresses. A parse drift
// (an Il2CppDumper formatting change) must fail here rather than produce wrong evidence downstream.
foreach (var anchor in reviewed)
{
    string name = anchor.Label[(anchor.Label.IndexOf('.') + 1)..];
    string owner = anchor.Label[..anchor.Label.IndexOf('.')];
    if (!methodByKey.TryGetValue((owner, name), out var resolved) || resolved.Rva != anchor.Rva)
    {
        Console.Error.WriteLine($"Map anchor mismatch: {anchor.Label} expected 0x{anchor.Rva:X}, "
            + (methodByKey.TryGetValue((owner, name), out var found) ? $"parsed 0x{found.Rva:X}" : "not found"));
        return 2;
    }
}

// --- Direct call edges ------------------------------------------------------------------------------------
// Call sites are located by scanning the two code sections for the rel32 call opcode and accepting only targets
// that are a declared method of a wanted type. This is a locator, not a proof: NativeEvidence re-decodes the
// reported address and checks the instruction text.
var callTargets = new HashSet<uint>(methodByKey.Values.Select(v => v.Rva));
var reviewedTargets = new HashSet<uint>(reviewed.Select(r => r.Rva));
var targetNames = new HashSet<string>(targets.Select(t => t.Type + "." + t.Name), StringComparer.Ordinal);
var edges = new List<(uint Site, uint Target, string Source, string TargetName)>();
var addressToName = new Dictionary<uint, string>();
foreach (var entry in methods) if (!addressToName.ContainsKey(entry.Rva)) addressToName[entry.Rva] = entry.Type + "." + entry.Name;
var methodStarts = methods.Where(m => m.Rva != 0).Select(m => m.Rva).Distinct().OrderBy(r => r).ToArray();
// A call site belongs to the declared method whose start most recently precedes it. The name is only
// trustworthy when the site sits strictly inside that method: a site at a method start is a coincidence of
// addresses, not a call made by that method.
string? OwningMethod(uint site, out bool insideBody)
{
    int low = 0, high = methodStarts.Length - 1, found = -1;
    while (low <= high)
    {
        int middle = (low + high) / 2;
        if (methodStarts[middle] <= site) { found = middle; low = middle + 1; }
        else high = middle - 1;
    }
    if (found < 0) { insideBody = false; return null; }
    insideBody = site > methodStarts[found];
    return addressToName[methodStarts[found]];
}
string[] sources = { "text", "il2cpp" };
foreach (var section in sections.Where(s => sources.Contains(s.Name)))
{
    uint start = section.Virtual, end = section.Virtual + section.RawSize;
    for (uint rva = start; rva + 5 <= end; rva++)
    {
        if (image[(int)(section.Raw + (rva - section.Virtual))] != 0xE8) continue;
        int delta = BitConverter.ToInt32(image, (int)(section.Raw + (rva - section.Virtual) + 1));
        long target = rva + 5L + delta;
        if (target <= 0 || target > uint.MaxValue || !callTargets.Contains((uint)target)) continue;
        edges.Add((rva, (uint)target, section.Name, addressToName[(uint)target]));
    }
}
// Recorded edges are the damage pipeline only: a call made from inside one of the declared methods. The
// reviewed entry bodies are always recorded, and for those the call is proven by the body decode rather than
// by the caller attribution. Everything else needs a site strictly inside its caller's body and a target that
// is not the caller itself, so the locator never claims a call it cannot attribute.
var recordedEdges = edges.Where(e => reviewedTargets.Contains(e.Site)
    || (OwningMethod(e.Site, out bool insideBody) is { } owner && insideBody
        && targetNames.Contains(owner) && addressToName[e.Target] != owner)).ToArray();

// --- Bounded disassembly ----------------------------------------------------------------------------------
string Describe(in Instruction instruction)
{
    var text = instruction.ToString();
    int space = text.IndexOf(' ');
    return space < 0 ? text : text[..space] + "|" + text[(space + 1)..];
}
List<(uint Rva, string Text)> Decode(uint rva, int length, bool stopAtReturn)
{
    var bytes = Read(rva, length);
    if (bytes.Length == 0) return new List<(uint, string)>();
    var decoder = NewDecoder(bytes);
    decoder.IP = imageBase + rva;
    ulong end = decoder.IP + (ulong)bytes.Length;
    var decoded = new List<(uint, string)>();
    while (decoder.IP < end)
    {
        var instruction = decoder.Decode();
        if (instruction.Code == Code.INVALID) break;
        decoded.Add(((uint)(instruction.IP - imageBase), Describe(instruction)));
        if (stopAtReturn && (instruction.Code == Code.Retnq || instruction.Code == Code.Int3)) break;
    }
    return decoded;
}
var entries = new List<object>();
foreach (var target in reviewed)
{
    var decoded = Decode(target.Rva, target.Window, target.StopAtReturn);
    entries.Add(new
    {
        rva = "0x" + target.Rva.ToString("X"),
        method = target.Label,
        windowBytes = target.Window,
        stopAtReturn = target.StopAtReturn,
        instructions = decoded.Select(d => new { rva = "0x" + d.Rva.ToString("X"), text = d.Text }).ToArray(),
    });
}
// Each call site is decoded from its own start, so the window begins at an instruction boundary instead of at
// an arbitrary offset that might land inside an earlier instruction. The first instruction of the window is
// therefore the call, followed by the code that consumes its result.
var seenSites = new HashSet<uint>();
var callSiteWindows = new List<object>();
foreach (var edge in recordedEdges.OrderBy(e => e.Site))
{
    if (!seenSites.Add(edge.Site)) continue;
    var decoded = Decode(edge.Site, CallWindow, true);
    callSiteWindows.Add(new
    {
        site = "0x" + edge.Site.ToString("X"),
        section = edge.Source,
        caller = OwningMethod(edge.Site, out _),
        target = "0x" + edge.Target.ToString("X"),
        targetMethod = edge.TargetName,
        instructions = decoded.Select(d => new { rva = "0x" + d.Rva.ToString("X"), text = d.Text }).ToArray(),
    });
}

// --- Output ------------------------------------------------------------------------------------------------
var damageMethods = new List<object>();
foreach (var target in targets)
{
    string id = target.Type + "." + target.Name;
    if (!methodByKey.TryGetValue((target.Type, target.Name), out var located))
    {
        Console.Error.WriteLine("Target not declared in the dump: " + id);
        return 2;
    }
    var parameterNames = ParameterNames(located.Signature);
    if (parameterNames.Length != target.Parameters.Length)
    {
        Console.Error.WriteLine($"Parameter count mismatch for {id}: dump has [{string.Join(",", parameterNames)}], "
            + $"evidence declares [{string.Join(",", target.Parameters)}]");
        return 2;
    }
    var notes = parameterNotes.GetValueOrDefault(id, Array.Empty<string>());
    damageMethods.Add(new
    {
        id,
        role = target.Role,
        type = target.Type,
        name = target.Name,
        il2cppSignature = frozenSignatures.GetValueOrDefault(id) ?? derivedSignatures.GetValueOrDefault(id),
        rawDeclaration = located.Signature,
        parameters = parameterNames.Select((name, index) => new
        {
            name,
            note = index < notes.Length ? notes[index] : null,
        }).ToArray(),
        nativeRva = "0x" + located.Rva.ToString("X"),
        virtualSlot = located.Slot,
        observations = methodNotes.GetValueOrDefault(id, Array.Empty<string>()),
    });
}
var packetTypes = new List<object>();
foreach (var packet in new[] { "pBulletDamageData", "pFullDamageData", "pExplosionDamageData", "pSmallDamageData",
    "pSetHealthData", "pAddHealthData", "pMiniDamageData", "pMediumDamageData" })
{
    var packetFields = fields.Where(f => f.Type == packet)
        .Select(f => new { declaration = f.Declaration, offset = "0x" + f.Offset.ToString("X") }).ToArray();
    if (packetFields.Length == 0) { Console.Error.WriteLine("Packet type not found in the dump: " + packet); return 2; }
    packetTypes.Add(new { type = packet, fields = packetFields });
}
var payload = new
{
    schemaVersion = 1,
    evidenceFile = "ForgeEnemy/evidence/e10-damage-window.json",
    area = "damage-window",
    buildId,
    gameRevision = revision,
    gameAssemblySha256 = Hash(nativePath),
    gameExecuted = false,
    verification = "metadata-static-native-call-sites",
    nativeRvaLock = new
    {
        owner = "ForgeEnemy/tests/NativeEvidence/NativeHealth.cs",
        entries = reviewed.Select(r => new { method = r.Label, rva = "0x" + r.Rva.ToString("X") }).ToArray(),
    },
    extraction = new
    {
        tool = "ForgeEnemy/tests/DamageWindow",
        command = "dotnet <artifacts>/bin/DamageWindow/release/DamageWindow.dll <GTFO game root> <dump.cs> <output.json>",
        disassembler = "Iced 1.x x86-64",
        dumpSha256 = Hash(dumpPath),
        callEdges = "rel32 call opcode scan over .text and il2cpp, accepted only when the relative target is a declared method of a wanted type",
        imageBase = "0x" + imageBase.ToString("X"),
        callWindowBytes = CallWindow,
    },
    damageMethods = damageMethods.ToArray(),
    packetTypes = packetTypes.ToArray(),
    reviewedEntries = entries.ToArray(),
    callEdges = recordedEdges.OrderBy(e => e.Site).Select(e => new
    {
        from = OwningMethod(e.Site, out _),
        to = e.TargetName,
        site = "0x" + e.Site.ToString("X"),
        targetRva = "0x" + e.Target.ToString("X"),
        section = e.Source,
    }).ToArray(),
    callSiteWindows = callSiteWindows.ToArray(),
    enumValues = enumMembers.Select(e => new { type = e.Type, name = e.Name, value = e.Value }).ToArray(),
    conclusions,
};
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"methods={damageMethods.Count} packetTypes={packetTypes.Count} callEdges={recordedEdges.Length} "
    + $"callSiteWindows={callSiteWindows.Count} scannedCallEdges={edges.Count}");
Console.WriteLine("wrote " + outputPath);
return 0;

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    using var sha = System.Security.Cryptography.SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(stream));
}
// Il2Cpp parameter names from the dump declaration, without the default values the dump appends.
static string[] ParameterNames(string declaration)
{
    int open = declaration.IndexOf('('), close = declaration.LastIndexOf(')');
    if (open < 0 || close < open) return Array.Empty<string>();
    string body = declaration[(open + 1)..close];
    if (body.Length == 0) return Array.Empty<string>();
    var names = new List<string>();
    int depth = 0, start = 0;
    for (int i = 0; i <= body.Length; i++)
    {
        if (i < body.Length && (body[i] == '<' || body[i] == '(' || body[i] == '[')) depth++;
        else if (i < body.Length && (body[i] == '>' || body[i] == ')' || body[i] == ']')) depth--;
        else if (i == body.Length || (body[i] == ',' && depth == 0))
        {
            string part = body[start..i].Trim();
            int equals = part.IndexOf('=');
            if (equals >= 0) part = part[..equals].Trim();
            names.Add(part[(part.LastIndexOf(' ') + 1)..]);
            start = i + 1;
        }
    }
    return names.ToArray();
}
