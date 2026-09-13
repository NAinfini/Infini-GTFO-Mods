using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Iced.Intel;
using Mono.Cecil;
using ForgeRuntime;
using ForgeRuntime.GameBindings;

internal static class NativeEvidence
{
    internal static int Verify(string bepInEx, string compiled, string gameRoot)
    {
        int checks = 0;
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(bepInEx, "core")); resolver.AddSearchDirectory(Path.Combine(bepInEx, "interop"));
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(bepInEx, "interop", "Modules-ASM.dll"), new ReaderParameters { AssemblyResolver = resolver });
        using var net = AssemblyDefinition.ReadAssembly(Path.Combine(bepInEx, "interop", "SNet_ASM.dll"), new ReaderParameters { AssemblyResolver = resolver });
        using var forge = AssemblyDefinition.ReadAssembly(compiled, new ReaderParameters { AssemblyResolver = resolver });
        IEnumerable<TypeDefinition> Walk(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Walk));
        var gameTypes = game.MainModule.Types.SelectMany(Walk).ToArray();
        var forgeTypes = forge.MainModule.Types.SelectMany(Walk).ToArray();
        MethodDefinition Method(string type, string method) => gameTypes.Single(t => t.FullName == type).Methods.Single(m => m.Name == method);
        void Signature(string type, string name, string returns, params string[] parameters)
        {
            var method = Method(type, name);
            Require(method.ReturnType.FullName == returns && method.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters), "Native signature mismatch: " + type + "." + name);
        }
        Signature("Dam_SyncedDamageBase", "SendSetHealth", "System.Void", "System.Single");
        Signature("Dam_EnemyDamageBase", "ReceiveSetHealth", "System.Void", "pSetHealthData");
        Signature("Dam_EnemyDamageBase", "ProcessReceivedDamage", "System.Boolean", "System.Single", "Agents.Agent", "UnityEngine.Vector3", "UnityEngine.Vector3", "ES_HitreactType", "System.Boolean", "System.Int32", "System.Single", "DamageNoiseLevel", "System.UInt32");
        Signature("GameStateManager", "DoChangeState", "System.Void", "eGameStateName");
        Signature("GameStateManager", "OnLevelCleanup", "System.Void");
        Signature("GameStateManager", "OnResetSession", "System.Void");
        Signature("CheckpointManager", "OnStateChange", "System.Void", "pCheckpointState", "pCheckpointState", "System.Boolean");
        Signature("Enemies.EnemySync", "OnSpawn", "System.Void", "Enemies.pEnemySpawnData");
        Signature("Enemies.EnemySync", "OnDespawn", "System.Void");
        Require(Method("Agents.Agent", "get_GlobalID").ReturnType.FullName == "System.UInt16", "Stable network ID type");
        Require(net.MainModule.Types.Single(t => t.FullName == "SNetwork.SNet").Methods.Any(m => m.Name == "get_IsMaster" && m.ReturnType.FullName == "System.Boolean"), "Host predicate signature");
        var float16 = net.MainModule.Types.Single(t => t.FullName == "SNetwork.SFloat16");
        Require(float16.Methods.Any(m => m.Name == "Set" && m.Parameters.Count == 2) && float16.Methods.Any(m => m.Name == "Get" && m.Parameters.Count == 1), "Native signed HP quantization methods");

        bool Patch(CustomAttribute a) => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch";
        var patches = forgeTypes.Where(t => t.CustomAttributes.Any(Patch)).ToArray();
        string[] expectedHost = { "FrameworkStateChanged", "FrameworkWorldCleanup", "FrameworkSessionReset", "FrameworkCheckpointRestore" };
        // Diagnostics live in the optional Development plugin; every host detour is a framework world binding.
        Require(patches.All(t => t.Namespace == "ForgeRuntime.GameBindings")
            && patches.Select(t => t.Name).OrderBy(x => x).SequenceEqual(expectedHost.OrderBy(x => x)), "Host actual compiled patch set changed");
        var load = forgeTypes.Single(t => t.FullName == "ForgeRuntime.Plugin").Methods.Single(m => m.Name == "Load");
        Require(load.Body.Instructions.Any(i => i.Operand is MethodReference method && method.DeclaringType.FullName == "HarmonyLib.Harmony" && method.Name == "PatchAll"), "Plugin does not install its own host patch set");
        foreach (var type in patches)
        {
            var attribute = type.CustomAttributes.Single(Patch);
            var targetType = attribute.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
            var name = attribute.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
            var target = Method(targetType.FullName, name);
            foreach (var hook in type.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix")))
                foreach (var argument in hook.Parameters)
                {
                    if (argument.Name == "__state") continue;
                    var plain = argument.ParameterType is ByReferenceType byRef ? byRef.ElementType : argument.ParameterType;
                    if (argument.Name == "__instance") Require(plain.FullName == target.DeclaringType.FullName, "Native hook instance mismatch " + type.Name);
                    else if (argument.Name.StartsWith("__") && int.TryParse(argument.Name[2..], out var index))
                        Require(plain.FullName == target.Parameters[index].ParameterType.FullName, "Native hook parameter mismatch " + type.Name);
                    else throw new Exception("Unverified injected native argument: " + argument.Name);
                }
        }
        var bridge = forgeTypes.Single(t => t.FullName == "ForgeRuntime.GameBindings.GameRuntimeBridge");
        string expectedHash = (string)bridge.Fields.Single(f => f.Name == "GameAssemblySha256").Constant;
        string gameAssembly = Path.Combine(gameRoot, "GameAssembly.dll");
        using var native = File.OpenRead(gameAssembly);
        using var sha256 = SHA256.Create();
        string hash = Convert.ToHexString(sha256.ComputeHash(native));
        Require(hash == expectedHash, "GameAssembly differs from supported binding hash"); native.Position = 0;
        using var pe = new PEReader(native);
        List<string> Disassemble(int rva, int length)
        {
            var bytes = pe.GetSectionData(rva).GetContent(0, length).ToArray();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = pe.PEHeaders.PEHeader!.ImageBase + (uint)rva;
            var end = decoder.IP + (uint)length; var lines = new List<string>();
            while (decoder.IP < end) { var instruction = decoder.Decode(); lines.Add($"{instruction.IP:X}: {instruction}"); }
            return lines;
        }
        var send = Disassemble(0x161F790, 0x150);
        var receive = Disassemble(0x1380D50, 0x30);
        Require(send.Any(l => l.Contains("cmp byte ptr [rax+0B9h],0")), "Native host check absent");
        Require(send.Any(l => l.Contains("call 0000000181633510h")) && send.Any(l => l.EndsWith("call rax")), "Native packet send and local receive path absent");
        Require(receive.Any(l => l.Contains("call 0000000181BB5AE0h")) && receive.Any(l => l.Contains("movss dword ptr [rbx+20h],xmm0")), "Enemy health readback is not native decoded assignment");
        Console.WriteLine("NATIVE SIGNATURE/PATCH GROUP/HASH CHECKED; not executed in GTFO. GameAssembly SHA256 " + hash);
        Console.WriteLine("Host patches: " + string.Join(", ", patches.Select(t => t.Name)));
        return checks;
    }
}
