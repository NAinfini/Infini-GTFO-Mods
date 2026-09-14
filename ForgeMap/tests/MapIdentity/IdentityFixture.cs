using ForgeMap;
using ForgeRuntime.Framework;

// Only this adapter is synthetic. Identity/session logic comes from the compiled Map DLL.
sealed class IdentityFixture : IDisposable
{
    internal readonly RuntimeKernel Kernel = new(new RuntimeIdentity("map.identity.tests", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));
    internal readonly HashSet<MapNativeIdentity> Native = new();
    internal readonly Dictionary<MapNativeIdentity, MapCreationTicket> Incarnations = new();
    internal readonly MapIdentitySession Session;
    internal Action<MapNativeIdentity>? DuringProbe;
    internal bool ThrowProbe;
    internal int ProbeCalls;
    internal IdentityFixture(int limit = 4096, bool start = true)
    {
        Kernel.BeginWorld(1);
        Session = new MapIdentitySession(Kernel, RuntimeLogLevel.Off, Probe, limit);
        if (start && !Kernel.StartRuntime(() => {})) throw new InvalidOperationException("Test startup failed.");
    }
    private bool Probe(MapCreationTicket ticket, MapNativeIdentity value)
    {
        ProbeCalls++;
        DuringProbe?.Invoke(value);
        if (ThrowProbe) throw new InvalidOperationException("Synthetic probe failure");
        return Native.Contains(value) && Incarnations.TryGetValue(value, out var current)
            && ReferenceEquals(ticket, current);
    }
    internal static MapObjectAddress Address(string placement = "placement-a")
        => new("layout", "layout-revision", 0, 0, 7, placement, "source-area-a", MapObjectKind.Area);
    internal static MapSourceLock Source()
        => new("room-resource", "room-revision", new string('a', 64), "source.assets", "9223372036854775807");
    internal static MapNativeIdentity Key(int number = 1) => new(100000 + number, number);
    internal MapCreationTicket Begin(MapObjectAddress? address = null, MapSourceLock? source = null)
        => Session.BeginCreation(address ?? Address(), source ?? Source());
    internal MapIdentityReceipt Bind(MapCreationTicket ticket, MapNativeIdentity? key = null)
    {
        var native = key ?? Key(); Native.Add(native); Incarnations[native] = ticket;
        return Callback(ticket, native);
    }
    // Callback-only delivery must not change the simulated physical incarnation.
    internal MapIdentityReceipt Callback(MapCreationTicket ticket, MapNativeIdentity? native = null)
        => Session.ObserveCreated(ticket, new(ticket.Value.Address, ticket.Value.Source, native ?? Key()));
    internal bool Current(EntityReference entity) => Session.TryResolve(entity, out _, out _);
    public void Dispose() { DuringProbe = null; ThrowProbe = false; Session.Dispose(); }
}
