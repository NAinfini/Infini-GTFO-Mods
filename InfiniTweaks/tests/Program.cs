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
string oldMarkerConfig = "[Item Markers]\nEnablePersistentItemMarkers = true\n# retired\nMarkerScale = 0.8\nClearMarkersKey = F6\n[Marker Item - 30]\nEnabled = false\n[Marker Item - 85]\nDisplayName = M\n[Markers - Ammo]\nDistanceMeters = 85\nColor = #FFFFFF\n[Combat]\nRecoilMultiplier = 0.5\n";
string cleanMarkerConfig = "[Item Markers]\nEnablePersistentItemMarkers = true\nClearMarkersKey = F6\n[Markers - Ammo]\nDistanceMeters = 85\n[Combat]\nRecoilMultiplier = 0.5\n";
Check(MarkerRules.CleanConfig(oldMarkerConfig) == cleanMarkerConfig, "Remove per-item settings and appearance overrides; preserve category range, global toggle, key and combat");
Check(MarkerRules.CleanConfig(cleanMarkerConfig) == cleanMarkerConfig, "Config cleanup is idempotent");
Check(MarkerRules.CleanConfig("[Markers - DoorLock]\nDistanceMeters = 40\n" + cleanMarkerConfig) == cleanMarkerConfig, "Retired security-door settings are removed without changing other settings");
Check(MarkerRules.CleanConfig(oldMarkerConfig.Replace("\n", "\r\n")) == cleanMarkerConfig.Replace("\n", "\r\n"), "Config cleanup supports Windows newlines");
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
Check(CasualRules.Reward(0, 100) == 0 && CasualRules.Reward(3, 2.5f) == 7 && CasualRules.Reward(3000, 100) == 100000, "Farmer must multiply earned currency, floor and cap.");
Check(CasualRules.Reward(150000, 1) == 150000 && CasualRules.Reward(12, float.NaN) == 12, "Farmer must never reduce native rewards or accept NaN.");
Check(CasualRules.Reward(1, 100000) == 100000 && CasualRules.Reward(0, 100000) == 0 && CasualRules.Reward(int.MaxValue, 100000) == int.MaxValue, "Requested 100000 multiplier saturates without integer overflow or fake earnings.");
Check(!MarkerRules.TerminalAllowed(false, true) && !MarkerRules.TerminalAllowed(true, false), "Normal doors and containers are not persistent markers, even when explicitly pinged.");
Check(!MarkerRules.TerminalAllowed(false, true) && MarkerRules.TerminalAllowed(false, false), "Locked doors stay excluded; non-door devices remain eligible.");
Check(CasualRules.ShowMarker(true, true, false, true, 900, 30), "Marker includes distance boundary.");
Check(!CasualRules.ShowMarker(true, true, false, true, 901, 30), "Out-of-range marker hides.");
Check(!CasualRules.ShowMarker(true, false, false, true, 1, 30) && !CasualRules.ShowMarker(false, true, false, true, 1, 30), "Wrong dimension/carried marker hides.");
Check(!CasualRules.ShowMarker(true, true, true, true, 1, 30) && CasualRules.ShowMarker(true, true, true, false, 1, 30), "Aim toggle controls marker visibility.");
var fixedRolls = new[] { new BoosterRollRules.Roll(1, 1.3f) };
var old26 = HistoricalBoosterTemplates.All.Single(t => t.Id == 26 && t.Category == 1);
Check(BoosterRollRules.TryMatch(new uint[] { 50, 7, 8 }, old26.Effects, old26.Slots, out var oldValues) && oldValues.SequenceEqual(new[] { 1.3f, 1.3f, 1.3f }), "Historical template 26 must match its three persistent effects.");
var old38 = HistoricalBoosterTemplates.All.Single(t => t.Id == 38 && t.Category == 2);
Check(BoosterRollRules.TryMatch(new uint[] { 12, 8, 50, 7 }, old38.Effects, old38.Slots, out oldValues) && oldValues.SequenceEqual(new[] { 0.87f, 2f, 1.5f, 1.5f }), "Historical template 38 retains its negative effect and original effect order.");
Check(!BoosterRollRules.TryMatch(new uint[] { 12, 8, 50, 7, 11 }, old38.Effects, old38.Slots, out _), "Historical matching cannot combine mutually exclusive negative effects.");
var choices = new[] { new[] { new BoosterRollRules.Roll(2, 1.4f), new BoosterRollRules.Roll(3, 1.5f) }, new[] { new BoosterRollRules.Roll(4, 0.8f) } };
Check(BoosterRollRules.TryMatch(new uint[] { 4, 1, 3 }, fixedRolls, choices, out var rolls) && rolls.SequenceEqual(new[] { 0.8f, 1.3f, 1.5f }), "Perfect booster matches one choice from each random slot, preserving actual effect order.");
Check(!BoosterRollRules.TryMatch(new uint[] { 1, 2, 3, 4 }, fixedRolls, choices, out _), "Never select both alternatives from the same slot.");
Check(!BoosterRollRules.TryMatch(new uint[] { 1, 99, 4 }, fixedRolls, choices, out _), "Unknown effects retain original booster.");
Check(!BoosterRollRules.TryMatch(new uint[] { 1, 3 }, fixedRolls, choices, out _), "Missing required slot rejects template.");
Check(BoosterRollRules.TryMatch(new uint[] { 1 }, fixedRolls, Array.Empty<BoosterRollRules.Roll[]>(), out _), "Fixed-only boosters must work.");
var overlap = new[] { new[] { new BoosterRollRules.Roll(2, 1.2f), new BoosterRollRules.Roll(3, 1.3f) }, new[] { new BoosterRollRules.Roll(2, 1.4f) } };
Check(BoosterRollRules.TryMatch(new uint[] { 2, 3 }, Array.Empty<BoosterRollRules.Roll>(), overlap, out rolls) && rolls.SequenceEqual(new[] { 1.4f, 1.3f }), "Template matching backtracks when slot choices overlap.");
// Exercise the production HUD hooks, including their explicit second submission
// after native UpdateExtraInfo. No pre-existing marker callback/cache is required.
PlayerManager.Local = local;
PlayerManager.PlayerAgentsInLevel.AddRange(new[] { local, remote });
remote.NavMarker = new() { Player = remote };
var hud = remote.NavMarker;
remote.Damage!.Health = 0.1f; remote.Damage.Infection = 0.6f;
hud.m_playerBackpack!.AmmoStorage.StandardAmmo.RelInPack = 0.5f;
hud.m_playerBackpack.AmmoStorage.SpecialAmmo.RelInPack = 0.2f;
hud.m_playerBackpack.AmmoStorage.ClassAmmo.RelInPack = 0.75f;
foreach (var slot in new[] { InventorySlot.Standard, InventorySlot.Special, InventorySlot.Class })
    hud.m_playerBackpack.Items[slot] = new() { Instance = new ItemEquippable { ArchetypeName = slot switch { InventorySlot.Standard => "Assault Rifle", InventorySlot.Special => "Heavy Assault Rifle", _ => "Sniper Sentry" } } };
ResourceHud.Clear(); ResourceHud.Tick(local);
Check(hud.UpdateCalls == 1 && hud.DisplayExtra == "" && !hud.m_extraInfoVisible && hud.DisplayName == "Teammate", "No held pack: keep the native name and hide extra percentages even with low health.");
void Hold(eResourceContainerSpawnType type)
{
    local.Inventory!.WieldedSlot = InventorySlot.ResourcePack;
    local.Inventory.WieldedItem = new Gear.ResourcePackFirstPerson { m_packType = type };
    ResourceHud.Tick(local);
}
Hold(eResourceContainerSpawnType.AmmoWeapon);
Check(hud.DisplayExtra.Contains("Assault Rifle 50%") && hud.DisplayExtra.Contains("Heavy Assault Rifle 20%") && !hud.DisplayExtra.Contains("HP") && !hud.DisplayExtra.Contains("Sniper Sentry"), "Ammo pack shows only main and special reserves.");
Check(hud.m_extraInfoVisible && hud.m_extraInfo == "", "Held pack keeps native refresh scheduling without native duplicate resource text.");
hud.m_playerBackpack.AmmoStorage.StandardAmmo.RelInPack = 1f;
hud.UpdateExtraInfo();
Check(hud.DisplayExtra.Contains("Assault Rifle 100%") && hud.DisplayExtra.Contains("#30FF30"), "Resource amounts refresh without switching the held pack.");
hud.m_playerBackpack.AmmoStorage.StandardAmmo.RelInPack = 0.5f;
hud.UpdateExtraInfo();
Check(hud.DisplayExtra.Contains("<size=120%>") && hud.DisplayExtra.Contains("<b>") && hud.DisplayExtra.Contains("#FFFF00") && hud.DisplayExtra.Contains("#" + MarkerRules.ResourceColor(0.2f)), "Each weapon independently uses the continuous red/yellow/green reserve gradient.");
hud.m_playerBackpack.Items[InventorySlot.Standard].Instance!.ArchetypeName = "HEL Revolver";
hud.UpdateExtraInfo();
Check(hud.DisplayExtra.Contains("HEL Revolver 50%") && hud.DisplayExtra.Contains("Heavy Assault Rifle 20%") && !hud.DisplayExtra.Contains("Main "), "Equipment refresh reads actual names independently for both weapon slots.");
Hold(eResourceContainerSpawnType.AmmoTool);
Check(hud.DisplayExtra.Contains("Sniper Sentry 75%") && hud.DisplayExtra.Contains("#" + MarkerRules.ResourceColor(0.75f)) && !hud.DisplayExtra.Contains("Assault Rifle") && !hud.DisplayExtra.Contains("Heavy Assault Rifle"), "Tool pack replaces ammo text using the same supply gradient.");
var deployedSentry = new SentryGunInstance { Owner = remote, Ammo = 10, AmmoMaxCap = 100 };
typeof(ResourceHud).GetMethod("SentryPlaced", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { deployedSentry });
hud.UpdateExtraInfo();
Check(hud.DisplayExtra.Contains("Sniper Sentry 10%") && !hud.DisplayExtra.Contains("75%"), "Deployed sentry uses its real ammunition, not stale backpack reserves.");
typeof(ResourceHud).GetMethod("SentryRemoved", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { deployedSentry });
hud.UpdateExtraInfo();
Check(hud.DisplayExtra.Contains("Sniper Sentry 75%"), "After sentry pickup, tool information returns to the carried inventory source.");
hud.m_playerBackpack.Items[InventorySlot.Class].Instance!.ItemDataBlock!.GUIShowAmmoInfinite = true;
hud.UpdateExtraInfo();
Check(hud.DisplayExtra.Contains("Sniper Sentry INF") && !hud.DisplayExtra.Contains("75%"), "Biotracker/infinite tools use their native infinity flag, never a misleading refill percentage.");
hud.m_playerBackpack.Items[InventorySlot.Class].Instance!.ItemDataBlock!.GUIShowAmmoInfinite = false;
hud.m_playerBackpack.Items.Remove(InventorySlot.Class); hud.UpdateExtraInfo();
Check(hud.DisplayExtra == "", "Missing tool slots do not fabricate an ammo value.");
hud.m_playerBackpack.Items[InventorySlot.Class] = new() { Instance = new ItemEquippable { ArchetypeName = "Sniper Sentry" } };
Hold(eResourceContainerSpawnType.Health);
Check(hud.DisplayExtra.Contains("HP 10%") && !hud.DisplayExtra.Contains("Sniper Sentry") && !hud.DisplayExtra.Contains("Infection"), "Medical pack shows only health.");
Hold(eResourceContainerSpawnType.Disinfection);
Check(hud.DisplayExtra.Contains("Infection 60%") && !hud.DisplayExtra.Contains("HP"), "Disinfect pack shows only infection.");
local.Inventory!.WieldedSlot = InventorySlot.Standard;
ResourceHud.Tick(local);
Check(hud.DisplayExtra == "" && !hud.m_extraInfoVisible && hud.DisplayName == "Teammate", "Switching back to a weapon immediately clears extras without changing the native name.");
int unchangedCalls = hud.UpdateCalls; ResourceHud.Tick(local);
Check(hud.UpdateCalls == unchangedCalls, "An unchanged held context must not rebuild all teammate HUD text every frame.");
Hold(eResourceContainerSpawnType.AmmoWeapon); local.Alive = false; ResourceHud.Tick(local);
Check(hud.DisplayExtra == "" && !hud.m_extraInfoVisible, "A dead local player's previously held pack must not leave resource text visible.");
// Disabled means no ownership: neither held-context changes nor hooks may hide native information.
ResourceHud.Clear(); Settings.ResourceHud.Value = false; local.Alive = true;
hud.SetExtraInfoVisible(true); hud.UpdateExtraInfo();
unchangedCalls = hud.UpdateCalls;
foreach (var packType in Enum.GetValues<eResourceContainerSpawnType>())
{
    Hold(packType);
    Check(hud.m_extraInfoVisible && hud.DisplayExtra == "native old extra" && hud.UpdateCalls == unchangedCalls,
        "Disabled HUD must not touch native extra info when switching to " + packType);
}
Settings.ResourceHud.Value = true;
local.Inventory!.WieldedSlot = InventorySlot.Standard;
ResourceHud.Tick(local);
Check(!hud.m_extraInfoVisible && hud.DisplayExtra == "", "Enabled HUD owns the extra-info filter.");
Settings.ResourceHud.Value = false; ResourceHud.Tick(local);
Check(hud.m_extraInfoVisible && hud.DisplayExtra == "native old extra", "Disabling restores the last native visible state and text, not the custom hidden state.");
Settings.ResourceHud.Value = true; ResourceHud.Tick(local);
hud.SetExtraInfoVisible(false);
Settings.ResourceHud.Value = false; ResourceHud.Tick(local);
Check(!hud.m_extraInfoVisible && hud.DisplayExtra == "", "A newer native hide request while enabled supersedes the saved visible state.");
Settings.ResourceHud.Value = true;
hud.m_playerBackpack.Items[InventorySlot.ResourcePack] = new() { Instance = new() { ArchetypeName = "Medipack" } };
hud.m_playerBackpack.Items[InventorySlot.Consumable] = new() { Instance = new() { ArchetypeName = "Glow Sticks" } };
hud.m_playerBackpack.AmmoStorage.ResourcePackAmmo.BulletsInPack = 2;
hud.m_playerBackpack.AmmoStorage.ConsumableAmmo.BulletsInPack = 7;
Hold(eResourceContainerSpawnType.Health);
Check(!hud.DisplayExtra.Contains("Medipack") && !hud.DisplayExtra.Contains("Glow Sticks") && hud.DisplayExtra.Contains("HP 10%"), "User excludes teammate carried inventory even while a matching resource pack is held.");
hud.m_playerBackpack.AmmoStorage.ConsumableAmmo.BulletsInPack = 0; hud.UpdateExtraInfo();
Check(!hud.DisplayExtra.Contains("Glow Sticks"), "Empty inventory slots do not leave clutter.");
local.Inventory.WieldedSlot = InventorySlot.Standard; ResourceHud.Tick(local);
Check(hud.DisplayExtra == "", "Inventory counts never leak into normal weapon HUD.");
local.FPSCamera = new(); remote.Alive = true;
InputMapper.GetButton = (_, _) => true;
ResourceHud.UpdateOpacity(local);
Check(Near(ResourceHudView.ResourceAlpha, 0.15f) && hud.m_marker!.Alpha == 1, "ADS fades resource rows without fading the whole teammate marker.");
remote.Alive = false; ResourceHud.UpdateOpacity(local);
Check(hud.m_marker!.Alpha == 1 && !ResourceHudView.Living, "Downed-player rescue markers remain opaque and resource rows hide.");
remote.Alive = true;
FocusStateManager.CurrentState = eFocusState.Menu; ResourceHud.UpdateOpacity(local);
Check(hud.m_marker.Alpha == 1, "Menus do not inherit gameplay fading.");
FocusStateManager.CurrentState = eFocusState.FPS; ResourceHud.UpdateOpacity(local);
Settings.ResourceHud.Value = false; ResourceHud.Tick(local);
Check(hud.m_marker.Alpha == 1, "Disabling clears opacity owned by the resource HUD.");
hud.m_marker.Alpha = 0.6f; ResourceHud.UpdateOpacity(local);
Check(Near(hud.m_marker.Alpha, 0.6f), "Disabled HUD must not overwrite native/other marker opacity.");
Check(MarkerRules.Range(12, 0, 100) == 12 && MarkerRules.Range(12, 115, 100) == 60,
    "Explicit pings expand category range without changing passive discovery range.");
Check(MarkerRules.Range(80, 115, 100) == 60 && MarkerRules.Range(0, 115, 100) == 0,
    "Pings never shrink ranges or re-enable disabled categories.");
Check(MarkerRules.Range(10, 115, 114.99f) == 60 && MarkerRules.Range(10, 115, 115) == 10, "PING expires at 15 seconds and restores the ordinary range without forgetting the item.");
Check(MarkerRules.HudOpacity(0, false, 0.15f, true) == 1 && Near(MarkerRules.HudOpacity(40, false, 0.15f, true), 0.85f),
    "Dynamic opacity keeps crosshair information readable and fades peripheral labels.");
Check(MarkerRules.HudOpacity(40, false, 0.15f, false) == 1 && Near(MarkerRules.HudOpacity(0, true, 0.15f, true), 0.15f),
    "ADS overrides angle fading; disabling dynamic fading restores full opacity.");
Check(MarkerRules.Count(2.5f) == " ×2.5" && MarkerRules.Count(0) == "" && MarkerRules.Count(float.NaN) == "",
    "Counts retain partial resource uses and exclude empty/invalid quantities.");
foreach (var category in Enum.GetValues<MarkerCategory>())
{
    var defaults = MarkerRules.Defaults(category);
    Check(defaults.Distance > 0 && defaults.Distance <= 100 && defaults.Color.Length == 7 && defaults.Color[0] == '#',
        "Every marker category has bounded range and a distinct configurable RGB entry: " + category);
}
Check(MarkerRules.Defaults(MarkerCategory.Ammo).Distance > MarkerRules.Defaults(MarkerCategory.Consumable).Distance,
    "Important resources are visible farther than ordinary consumables.");
MarkerVisualTests.Run(Check);
CombatStatsTests.Run(Check);
TelemetryTests.Run(Check);
Console.WriteLine($"PASS: {count} regression assertions. Unity/IL2CPP, graphics and multiplayer require in-game validation.");
