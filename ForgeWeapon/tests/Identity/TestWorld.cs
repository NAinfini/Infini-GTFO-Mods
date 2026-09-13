using ForgeRuntime.Framework;
using ForgeWeapon;

// Synthetic observation inputs; the session/index and SDK are compiled production code.
// These probes do not call GTFO, and no resource or damage implementation is faked.
internal sealed class TestWorld : IDisposable
{
    internal RuntimeKernel Kernel { get; }
    internal EquipmentIdentitySession Session { get; }
    internal bool NativeCurrent = true, OwnerCurrent = true;
    internal int NativeReads, OwnerReads;
    internal Action? DuringProbe;
    internal EntityReference Owner => new("fixture.player:a", Kernel.WorldEpoch, 1);
    internal TestWorld(bool start = true, int maxActive = 1024, int maxHistory = 8192)
    {
        Kernel = new RuntimeKernel(new RuntimeIdentity("forge.weapon.test", "0.1.0",
            RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(7);
        Session = new EquipmentIdentitySession(Kernel, _ =>
        {
            NativeReads++; DuringProbe?.Invoke(); return NativeCurrent;
        }, _ => { OwnerReads++; return OwnerCurrent; }, maxActive, maxHistory);
        if (start) Start();
    }
    internal void Start()
    { Kernel.StartRuntime(() => { }); Kernel.Advance(0, true); }
    internal EquipmentObservation Item(string id = "a", string slot = "GearStandard", long life = 1)
        => new(new EntityReference("gtfo.equipment:" + id, Kernel.WorldEpoch, life),
            "fixture.rifle", "r1", Owner, slot, EquipmentLocation.Inventory, true, true);
    internal EquipmentUseTicket Track(EquipmentObservation? value = null)
    {
        var item = value ?? Item(); Session.Record(item);
        return Session.CaptureOwnedUse(item.Entity, item.Owner!, true);
    }
    public void Dispose() => Session.Dispose();
}
