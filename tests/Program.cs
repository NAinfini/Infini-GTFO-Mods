using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameData;
using InfiniTweaks;
using Player;

var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
jsonOptions.Converters.Add(new JsonStringEnumConverter());
using var history = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "historical-stats.json")));
var count = 0;
void Check(bool ok, string message) { count++; if (!ok) throw new Exception(message); }
bool Near(float a, float b) => Math.Abs(a-b) < 0.00001f;
void Invoke(string name, params object[] arguments) => typeof(GameDataTweaks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);
PlayerAgent Player(bool local = true, bool bot = false) => new() { IsLocallyOwned = local, Owner = new() { IsBot = bot } };
PlayerStamina Stamina(PlayerAgent owner, float amount = 1) { var s = new PlayerStamina { m_owner = owner, Stamina = amount }; owner.Stamina = s; return s; }
PlayerStamina.ActionCost Cost(float amount) => new() { baseStaminaCostInCombat = amount, baseStaminaCostOutOfCombat = amount, resetRestingTimerInCombat = true };
void Spend(PlayerStamina stamina, float amount, float time = 1)
{
    var cost = Cost(amount);
    if (StaminaCostPatch.ScaleCost(stamina, ref cost)) stamina.UseStamina(cost, time);
}

// Low-stamina regressions: applying a refund after clamping produces the wrong
// answer in both cases. Scaling the input must respect the actual cost first.
Settings.StaminaCostMultiplier.Value = 0.5f;
var local = Player();
var stamina = Stamina(local, 0.1f);
Spend(stamina, 0.4f);
Check(Near(stamina.Stamina, 0), "Overdraw must not refund stamina after the clamp.");
stamina.Stamina = 0.3f;
Spend(stamina, 0.4f);
Check(Near(stamina.Stamina, 0.1f), "Half of 0.4 must cost 0.2, not half of the clamped loss.");
stamina.Stamina = 1;
for (var i = 0; i < 10; i++) Spend(stamina, 0.1f, 0.1f);
Check(Near(stamina.Stamina, 0.95f), "Continuous costs must be independent of frame subdivisions.");
var mixedCost = Cost(0.2f);
mixedCost.baseStaminaCostOutOfCombat = -0.2f;
StaminaCostPatch.ScaleCost(stamina, ref mixedCost);
Check(Near(mixedCost.baseStaminaCostInCombat, 0.1f) && Near(mixedCost.baseStaminaCostOutOfCombat, -0.2f), "Recovery must not be reduced.");
Check(mixedCost.resetRestingTimerInCombat, "Nonzero costs retain the native rest timer rules.");

var remote = Player(false);
var bot = Player(true, true);
foreach (var owner in new[] { remote, bot })
{
    var other = Stamina(owner);
    var cost = Cost(0.2f);
    Check(StaminaCostPatch.ScaleCost(other, ref cost) && Near(cost.baseStaminaCostInCombat, 0.2f), "Do not alter remote players or host-owned bots.");
}
Settings.StaminaCostMultiplier.Value = 1;
stamina.Stamina = 0.8f;
Spend(stamina, 0.2f);
Check(Near(stamina.Stamina, 0.6f), "Neutral multiplier must retain vanilla costs.");
Settings.StaminaCostMultiplier.Value = 0;
stamina.Stamina = 0.1f;
Spend(stamina, 2);
Check(stamina.Stamina == 1, "Zero multiplier must provide full stamina.");
stamina.Stamina = 0.9f;
InfiniteStaminaPatch.KeepFull(stamina);
Check(stamina.Stamina == 1, "Combat caps must not defeat infinite stamina.");
var remoteStamina = Stamina(remote, 0.3f);
InfiniteStaminaPatch.KeepFull(remoteStamina);
Check(Near(remoteStamina.Stamina, 0.3f), "Infinite stamina must remain local.");
Settings.StaminaCostMultiplier.Value = 1;
Settings.RemoveJumpCost.Value = Settings.RemoveMeleeCost.Value = false;
Check(JumpCostPatch.AllowJumpCost(stamina) && MeleeCostPatch.AllowMeleeCost(stamina), "Cost toggles default to vanilla.");
Settings.RemoveJumpCost.Value = Settings.RemoveMeleeCost.Value = true;
Check(!JumpCostPatch.AllowJumpCost(stamina) && !MeleeCostPatch.AllowMeleeCost(stamina), "Cost toggles must work independently of the global multiplier.");
Check(JumpCostPatch.AllowJumpCost(remoteStamina) && MeleeCostPatch.AllowMeleeCost(remoteStamina), "Cost toggles must not affect a remote player.");
Settings.EnableChargeRecovery.Value = true;
ChargeRecoveryPatch.AllowRecovery(new Gear.MWS_ChargeUp { m_weapon = new() { Owner = local } });
Check(stamina.AllowRegen, "Charging should allow local stamina recovery.");
ChargeRecoveryPatch.AllowRecovery(new Gear.MWS_ChargeUp { m_weapon = new() { Owner = remote } });
Check(!remoteStamina.AllowRegen, "Do not enable remote charge recovery.");

Settings.AimPunchMultiplier.Value = 0.1f;
for (var i = 0; i < 5; i++)
{
    var punch = 2f;
    AimPunchPatch.ApplyMultiplier(ref punch);
    Check(Near(punch, 0.2f), "Aim punch must not compound across hits/camera re-enables.");
}
Settings.TeammatesIgnoreBullets.Value = true;
Check(!FriendlyBulletPatch.AllowBulletDamage(new() { Target = remote }, local), "Local bullets should not hurt teammates.");
Check(!FriendlyBulletPatch.AllowBulletDamage(new() { Target = bot }, local), "Local bullets should not hurt bots.");
Check(FriendlyBulletPatch.AllowBulletDamage(new() { Target = local }, remote), "This toggle must not grant incoming bullet immunity.");
Check(FriendlyBulletPatch.AllowBulletDamage(new() { Target = local }, new Agents.Agent()), "Non-player damage must remain intact.");
Check(FriendlyBulletPatch.AllowBulletDamage(new() { Target = local }, local), "Do not expand teammate protection to self damage.");
Check(FriendlyBulletPatch.AllowBulletDamage(new() { Target = remote }, bot), "Do not change bot-fired bullets.");
Settings.TeammatesIgnoreBullets.Value = false;
Check(FriendlyBulletPatch.AllowBulletDamage(new() { Target = remote }, local), "Disabled friendly-fire toggle must permit native damage.");

void ResetData()
{
    ArchetypeDataBlock.Blocks.Clear(); RecoilDataBlock.Blocks.Clear(); FlashlightSettingsDataBlock.Blocks.Clear();
    foreach (var b in history.RootElement.GetProperty("6244c01").GetProperty("Archetype").EnumerateArray())
        ArchetypeDataBlock.AddBlock(JsonSerializer.Deserialize<ArchetypeDataBlock>(b.GetRawText(), jsonOptions)!);
    foreach (var b in history.RootElement.GetProperty("6244c01").GetProperty("Recoil").EnumerateArray())
        RecoilDataBlock.AddBlock(JsonSerializer.Deserialize<RecoilDataBlock>(b.GetRawText(), jsonOptions)!);
    typeof(GameDataTweaks).GetField("_applied", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
}
void Compare(JsonElement expected, object actual, string path, bool archetype)
{
    foreach (var p in expected.EnumerateObject())
    {
        if (p.Name is "name" or "internalEnabled" or "persistentID" or "RecoilDataID") continue;
        // Presets restore combat stats, not text, models, animations, or sentry behavior.
        if (archetype && (p.Name.StartsWith("Sentry_") || p.Name is "PublicName" or "Description" or "ShellCasingSize" or "ShellCasingSpeedRange" or "EquipSequence" or "AimSequence")) continue;
        var field = actual.GetType().GetField(p.Name);
        Check(field != null, $"Missing covered field: {path}.{p.Name}");
        var value = field!.GetValue(actual)!;
        if (p.Value.ValueKind == JsonValueKind.Object) Compare(p.Value, value, path+"."+p.Name, false);
        else if (p.Value.ValueKind == JsonValueKind.Number) Check(Near(p.Value.GetSingle(), Convert.ToSingle(value)), $"Historical mismatch: {path}.{p.Name}");
        else Check(p.Value.ToString() == value.ToString(), $"Historical mismatch: {path}.{p.Name}");
    }
}
foreach (var pair in new[] { ("3376fbd", WeaponPreset.OriginalR6), ("6244c01", WeaponPreset.R8Current) })
{
    ResetData();
    var shared = JsonSerializer.Serialize(RecoilDataBlock.GetBlock(9), jsonOptions);
    Invoke("ApplyHelGun", pair.Item2);
    Invoke("ApplySniper", pair.Item2);
    foreach (var expected in history.RootElement.GetProperty(pair.Item1).GetProperty("Archetype").EnumerateArray())
    {
        var weapon = ArchetypeDataBlock.GetBlock(expected.GetProperty("persistentID").GetUInt32())!;
        Compare(expected, weapon, pair.Item1+"/"+weapon.persistentID, true);
        var expectedRecoil = history.RootElement.GetProperty(pair.Item1).GetProperty("Recoil").EnumerateArray()
            .Single(b => b.GetProperty("persistentID").GetUInt32() == expected.GetProperty("RecoilDataID").GetUInt32());
        Compare(expectedRecoil, RecoilDataBlock.GetBlock(weapon.RecoilDataID)!, pair.Item1+"/recoil", false);
    }
    Check(shared == JsonSerializer.Serialize(RecoilDataBlock.GetBlock(9), jsonOptions), "Sniper preset must not mutate shared recoil 9.");
    Check(RecoilDataBlock.Blocks.Count == 4, "Each enabled weapon must have exactly one private recoil block.");
}
ResetData();
var unchanged = JsonSerializer.Serialize(ArchetypeDataBlock.Blocks, jsonOptions);
Invoke("ApplyHelGun", WeaponPreset.Disabled); Invoke("ApplySniper", WeaponPreset.Disabled);
Check(unchanged == JsonSerializer.Serialize(ArchetypeDataBlock.Blocks, jsonOptions) && RecoilDataBlock.Blocks.Count == 2, "Disabled presets must make no changes.");
Settings.RecoilMultiplier.Value = 0.5f;
Invoke("ApplyRecoilMultiplier");
var recoil = RecoilDataBlock.GetBlock(9)!;
Check(Near(recoil.power.Min, 1) && Near(recoil.recoilPosImpulse.z, -0.3f) && Near(recoil.recoilRotImpulse.z, 2.5f), "Half recoil must scale aim and weapon impulses once.");
Check(Near(recoil.dampening, 9) && Near(recoil.hipFireCrosshairSizeDefault, 200), "Recoil strength must not alter recovery or spread.");
Settings.RecoilMultiplier.Value = 0;
Invoke("ApplyRecoilMultiplier");
Check(recoil.power.Max == 0 && recoil.recoilPosImpulse.sqrMagnitude == 0 && recoil.recoilPosShift.sqrMagnitude == 0 && recoil.recoilRotImpulse.sqrMagnitude == 0 && recoil.concussionIntensity == 0, "Zero recoil must remove all configured impulses.");

ResetData();
Settings.RecoilMultiplier.Value = 1;
Settings.HelGunVersion.Value = Settings.SniperVersion.Value = WeaponPreset.Disabled;
var light = new FlashlightSettingsDataBlock { range = 95, angle = 170 };
FlashlightSettingsDataBlock.AddBlock(light);
GameDataTweaks.Apply();
Check(light.range == 100 && light.angle == 179, "Flashlight must respect upper limits.");
light.range = 20; light.angle = 50;
GameDataTweaks.Apply();
Check(light.range == 20 && light.angle == 50, "Duplicate initialization must not compound data changes.");
Settings.FlashlightRangeAdjustment.Value = -100; Settings.FlashlightAngleAdjustment.Value = -178;
Invoke("ApplyFlashlights");
Check(Near(light.range, 0.1f) && light.angle == 1, "Narrower/shorter flashlight must respect lower limits.");
Check(CasualRules.TryMerge(40, 40, 100, out var carried, out var remaining) && carried == 80 && remaining == 0, "2 + 2 uses must become 4.");
Check(CasualRules.TryMerge(80, 60, 100, out carried, out remaining) && carried == 100 && remaining == 40, "Overflow must retain two world uses.");
Check(!CasualRules.TryMerge(100, 40, 100, out _, out _), "Full packs swap normally.");
foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
{
    Check(!CasualRules.TryMerge(invalid, 20, 100, out _, out _), "Invalid carried amounts must not mutate items.");
    Check(!CasualRules.TryMerge(20, invalid, 100, out _, out _), "Invalid world amounts must not mutate items.");
}
var random = new Random(2100);
for (int i = 0; i < 500; i++)
{
    float a = random.Next(0, 10000) / 100f, b = random.Next(1, 10000) / 100f;
    bool merged = CasualRules.TryMerge(a, b, 100, out carried, out remaining);
    Check(merged && carried <= 100 && remaining >= 0 && Math.Abs(a + b - carried - remaining) < 0.0001f, "Fractional native ammo must be conserved.");
}
Check(CasualRules.Reward(0, 100) == 0 && CasualRules.Reward(3, 2.5f) == 7 && CasualRules.Reward(3000, 100) == 100000, "Farmer must multiply earned currency, floor and cap.");
Check(CasualRules.Reward(150000, 1) == 150000 && CasualRules.Reward(12, float.NaN) == 12, "Farmer must never reduce native rewards or accept NaN.");
Check(CasualRules.EffectiveDamage(10, -50) == 10 && CasualRules.EffectiveDamage(10, 15) == 0 && CasualRules.EffectiveDamage(10, float.NaN) == 0, "Damage must exclude overkill, healing and invalid health.");
Check(CasualRules.Accuracy(0, 0) == "—" && CasualRules.Accuracy(10, 20) == "100.0%", "Unknown accuracy and piercing upper bound.");
Check(CasualRules.ShowMarker(true, true, false, true, 900, 30), "Marker includes distance boundary.");
Check(!CasualRules.ShowMarker(true, true, false, true, 901, 30), "Out-of-range marker hides.");
Check(!CasualRules.ShowMarker(true, false, false, true, 1, 30) && !CasualRules.ShowMarker(false, true, false, true, 1, 30), "Wrong dimension/carried marker hides.");
Check(!CasualRules.ShowMarker(true, true, true, true, 1, 30) && CasualRules.ShowMarker(true, true, true, false, 1, 30), "Aim toggle controls marker visibility.");
var fixedRolls = new[] { new BoosterRollRules.Roll(1, 1.3f) };
var choices = new[] { new[] { new BoosterRollRules.Roll(2, 1.4f), new BoosterRollRules.Roll(3, 1.5f) }, new[] { new BoosterRollRules.Roll(4, 0.8f) } };
Check(BoosterRollRules.TryMatch(new uint[] { 4, 1, 3 }, fixedRolls, choices, out var rolls) && rolls.SequenceEqual(new[] { 0.8f, 1.3f, 1.5f }), "Perfect booster matches one choice from each random slot, preserving actual effect order.");
Check(!BoosterRollRules.TryMatch(new uint[] { 1, 2, 3, 4 }, fixedRolls, choices, out _), "Never select both alternatives from the same slot.");
Check(!BoosterRollRules.TryMatch(new uint[] { 1, 99, 4 }, fixedRolls, choices, out _), "Unknown effects retain original booster.");
Check(!BoosterRollRules.TryMatch(new uint[] { 1, 3 }, fixedRolls, choices, out _), "Missing required slot rejects template.");
Check(BoosterRollRules.TryMatch(new uint[] { 1 }, fixedRolls, Array.Empty<BoosterRollRules.Roll[]>(), out _), "Fixed-only boosters must work.");
var overlap = new[] { new[] { new BoosterRollRules.Roll(2, 1.2f), new BoosterRollRules.Roll(3, 1.3f) }, new[] { new BoosterRollRules.Roll(2, 1.4f) } };
Check(BoosterRollRules.TryMatch(new uint[] { 2, 3 }, Array.Empty<BoosterRollRules.Roll>(), overlap, out rolls) && rolls.SequenceEqual(new[] { 1.4f, 1.3f }), "Template matching backtracks when slot choices overlap.");
var row = new StatisticRow();
Check(row.Apply(2, 8, 10, 5, 0), "Accept initial host-observed accuracy.");
Check(!row.Apply(2, 8, 20, 10, 0) && row.Fired == 10, "Duplicate snapshot cannot double counts.");
Check(!row.Apply(2, 7, 5, 3, 0), "Out-of-order snapshots are ignored.");
Check(row.Apply(1, 1, 12, 8, 0), "Owner accuracy supersedes host estimate with its own sequence.");
Check(!row.Apply(2, 99, 30, 20, 0) && row.Fired == 12, "Estimates never replace exact peer reports.");
Check(row.Apply(3, 2, 0, 0, 40) && row.Hit == 8 && row.Damage == 40, "Host damage does not overwrite owner accuracy.");
Check(!row.Apply(3, 2, 0, 0, 80) && !row.Apply(3, 1, 0, 0, 10) && row.Damage == 40, "Damage snapshots are cumulative, not additive.");
Check(!row.Apply(1, 2, 1, 2, 0) && !row.Apply(1, 2, -1, 0, 0) && !row.Apply(3, 3, 0, 0, float.NaN), "Reject malformed statistics.");
// Exercise the actual production protocol between independent clients. A third
// player is the host but runs no stats protocol at all.
var clientA = new StatisticsSync(); var clientB = new StatisticsSync();
clientA.Reset(99, false); clientB.Reset(99, false);
StatisticsSync.Session Connect(StatisticsSync from, ulong fromId, StatisticsSync to, ulong toId)
{
    var challenge = to.ReceiveSession(fromId, new() { Generation = from.Generation });
    Check(challenge is { Kind: 1 }, "Announcement must request a receiver-issued challenge.");
    var confirmation = from.ReceiveSession(toId, challenge!.Value);
    Check(confirmation is { Kind: 2 }, "Only the addressed current incarnation answers a challenge.");
    to.ReceiveSession(fromId, confirmation!.Value);
    return confirmation.Value;
}
StatisticsSync.Snapshot Packet(StatisticsSync from, ulong toId, ulong owner, uint sequence, byte kind = 1, uint weapon = 7) => new()
{
    Generation = from.Generation, Recipient = from.Peers[toId].OutboundGeneration,
    Token = from.Peers[toId].OutboundToken, Player = owner, Weapon = weapon,
    Sequence = sequence, Kind = kind, Fired = 40, Hit = 20, Damage = 50
};
var firstConfirmation = Connect(clientA, 1, clientB, 2);
Connect(clientB, 2, clientA, 1);
var oldPacket = Packet(clientA, 2, 1, 100);
Check(clientB.Receive(1, oldPacket) && clientB.Get(1, 7).Fired == 40, "Exact accuracy must work without a participating host.");
Check(clientA.Receive(2, Packet(clientB, 1, 2, 20)) && !clientA.HostData && !clientB.HostData, "Both clients share accuracy while damage stays unknown.");
Check(!clientB.Receive(1, Packet(clientA, 2, 2, 101)), "An owner cannot publish exact accuracy for another player.");
Check(!clientB.Receive(1, Packet(clientA, 2, 1, 101, 3)), "A non-host cannot supply damage.");
Check(!clientB.Receive(1, Packet(clientA, 2, 1, 101, 2)), "A non-host cannot supply host estimates.");
Check(clientB.Receive(1, Packet(clientA, 2, 1, 101, 1, 8)), "Track a second weapon in the same stream.");
clientB.Get(1, 7).Damage = 75;
clientA.Reset(99, false);
var freshConfirmation = Connect(clientA, 1, clientB, 2);
var freshPacket = Packet(clientA, 2, 1, 1); freshPacket.Fired = 2; freshPacket.Hit = 1;
Check(clientB.Get(1, 7).Fired == 0 && clientB.Get(1, 8).Fired == 0 && clientB.Get(1, 7).Damage == 75, "Reconnecting resets all owner weapon accuracy, preserving host damage.");
Check(clientB.Receive(1, freshPacket) && clientB.Get(1, 7).Fired == 2, "Reconnected sequence 1 must replace the old sequence 100 stream immediately.");
Check(!clientB.Receive(1, oldPacket) && !clientB.Receive(1, freshPacket), "Reject retired-generation and duplicate packets.");
clientB.ReceiveSession(1, freshConfirmation);
Check(clientB.Get(1, 7).Fired == 2, "Repeated confirmation must not erase active counters.");
var staleChallenge = clientB.ReceiveSession(1, new() { Generation = oldPacket.Generation });
Check(staleChallenge.HasValue && clientA.ReceiveSession(2, staleChallenge.Value) == null, "A stale announcement cannot get confirmation from the new incarnation.");
clientB.ReceiveSession(1, firstConfirmation);
Check(!clientB.Receive(1, oldPacket) && clientB.Get(1, 7).Fired == 2, "An old confirmation cannot answer a fresh challenge or roll the stream backward.");
var lateConfirmation = freshConfirmation;
clientB.Reset(99, false);
Check(!clientB.Receive(1, freshPacket), "Receiver reset rejects pre-checkpoint data even from a still-current sender.");
clientB.ReceiveSession(1, lateConfirmation);
Check(!clientB.Receive(1, freshPacket), "Pre-reset confirmations cannot restore an old receiver session.");
Connect(clientA, 1, clientB, 2);
Check(clientB.Receive(1, Packet(clientA, 2, 1, 2)), "Still-connected sender can synchronize with a reset receiver.");
Connect(clientB, 2, clientA, 1);
Check(clientA.Receive(2, Packet(clientB, 1, 2, 1)), "Reset receiver's own new stream also starts at sequence 1.");

// A stats-enabled host joins later. It may supply damage/estimates, but must
// not erase either client's exact accuracy when establishing its session.
var host = new StatisticsSync(); host.Reset(99, true);
Connect(host, 99, clientB, 2);
Check(clientB.HostData && clientB.Get(1, 7).Fired == 40, "Host handshake preserves owner accuracy.");
Check(clientB.Receive(99, Packet(host, 2, 1, 3, 3)) && clientB.Get(1, 7).Damage == 50, "Only participating host supplies effective damage.");
Check(!clientB.Receive(99, Packet(host, 2, 1, 4, 2)) && clientB.Get(1, 7).AccuracySource == 1, "Host estimates never override exact accuracy.");
Check(clientB.Receive(99, Packet(host, 2, 3, 5, 2)), "Host can supply estimates for an unmodded player.");
var oldHostPacket = Packet(host, 2, 1, 6, 3);
host.Reset(99, true); Connect(host, 99, clientB, 2);
Check(clientB.Get(1, 7).Fired == 40 && clientB.Get(1, 7).Damage == 0 && clientB.Get(3, 7).Fired == 0, "Host reset clears only host-owned data, not exact peer accuracy.");
Check(clientB.Receive(99, Packet(host, 2, 1, 1, 3)) && !clientB.Receive(99, oldHostPacket), "New host stream accepts sequence 1 and rejects old damage.");
clientB.ChangeMaster(1, false);
Check(!clientB.HostData && clientB.Get(1, 7).Fired == 40 && clientB.Get(1, 7).Damage == 0, "Host migration preserves owner accuracy but starts unknown damage.");
Check(!clientB.Receive(99, Packet(host, 2, 1, 2, 3)), "Former host loses damage authority immediately.");
Connect(clientA, 1, clientB, 2);
Check(clientB.HostData && clientB.Get(1, 7).Fired == 40, "Existing peer becoming host does not reset its exact accuracy.");
Check(clientB.Receive(1, Packet(clientA, 2, 1, 3, 3)), "New host can publish damage through its existing peer session.");
Console.WriteLine($"PASS: {count} regression assertions. Unity/IL2CPP, graphics and multiplayer require in-game validation.");
