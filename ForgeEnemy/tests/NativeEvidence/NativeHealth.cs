using System.Reflection.PortableExecutable;
using Iced.Intel;

internal static class NativeHealth
{
    // Offline evidence offsets, never production function pointers or invoked game methods.
    private const string SupportedHash = "C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55";

    // The only signatures whose native bodies this auditor decodes. The RVA-to-method binding was read from an
    // Il2CppDumper map of this build; the auditor proves instruction shapes at the address, not that binding.
    internal static readonly IReadOnlyDictionary<string, int> ReviewedRvas = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["System.Void Dam_SyncedDamageBase::SendSetHealth(System.Single)"] = 0x161F790,
        ["System.Void Dam_EnemyDamageBase::ReceiveSetHealth(pSetHealthData)"] = 0x1380D50,
    };

    internal static void Verify(string path, string hash, Action<string, bool, string> check)
    {
        if (hash != SupportedHash)
        {
            check("native.health.unsupported", false, "No disassembly attempted for an unreviewed native build.");
            return;
        }
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        string[] Decode(int rva, int length)
        {
            var bytes = pe.GetSectionData(rva).GetContent(0, length).ToArray();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = pe.PEHeaders.PEHeader!.ImageBase + (uint)rva;
            ulong end = decoder.IP + (uint)length;
            var lines = new List<string>();
            while (decoder.IP < end) lines.Add(decoder.Decode().ToString());
            return lines.ToArray();
        }
        var send = Decode(ReviewedRvas["System.Void Dam_SyncedDamageBase::SendSetHealth(System.Single)"], 0x150);
        var receive = Decode(ReviewedRvas["System.Void Dam_EnemyDamageBase::ReceiveSetHealth(pSetHealthData)"], 0x30);
        check("native.health.host-check", send.Any(l => l.Contains("cmp byte ptr [rax+0B9h],0")),
            "Build-specific SendSetHealth host-check instruction; not proof of multiplayer behavior.");
        check("native.health.send-receive", send.Any(l => l.Contains("call 0000000181633510h")) && send.Any(l => l == "call rax"),
            "Build-specific packet-send and virtual local-receive call shapes at SendSetHealth RVA 0x161F790.");
        check("native.health.readback", receive.Any(l => l.Contains("call 0000000181BB5AE0h"))
            && receive.Any(l => l.Contains("movss dword ptr [rbx+20h],xmm0")),
            "ReceiveSetHealth RVA 0x1380D50 decodes SFloat16 then stores the health field; no method was executed.");
    }
}
