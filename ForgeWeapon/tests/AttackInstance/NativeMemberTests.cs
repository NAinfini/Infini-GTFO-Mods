using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace ForgeWeapon.Tests.AttackInstance;

/// <summary>The hook-to-native mapping, checked against the interop metadata rather than against the doubles this
/// project compiles: every `HarmonyPatch` declaration in the slice's hook set has to name a type and a method this
/// build's `Modules-ASM.dll` really declares. A member this build does not have fails here, which is the check a
/// hook set assembled by attribute cannot make for itself at compile time.
///
/// The hook set is read as source, not compiled: its bodies reach `WeaponNativeSession`, whose registration
/// belongs to the package's own plugin, and a second copy of that session inside this fixture would check nothing.
/// The declarations are the part with a native dependency, and they are read verbatim from the file that installs
/// them. The interop assembly is read as metadata with Mono.Cecil, exactly as the package's own `NativeLayout`
/// suite reads it; no game assembly is loaded and no patch is applied.</summary>
public sealed class NativeMemberTests
{
    /// <summary>Every patch declaration the hook set installs, grouped by the type it is installed on. Read from
    /// the source so the list cannot drift from what the plugin installs.</summary>
    private static readonly (string Type, string Method)[] Patched = Declarations();

    /// <summary>The natives the module and the hooks read by name, each on the type that declares it: the
    /// weapon's own burst length, and the weapon the archetype was set up on.</summary>
    private static readonly (string Type, string Member)[] Read =
    {
        ("Gear.BulletWeapon", "m_burstMax"),
        ("Gear.BulletWeaponArchetype", "m_weapon")
    };

    [Fact]
    public void EveryPatchedMemberExistsInThisBuildsInterop()
    {
        Assert.NotEmpty(Patched);
        using var assembly = Interop();
        foreach (var (type, method) in Patched)
        {
            var target = Type(assembly, type);
            var found = target.Methods.Where(candidate => candidate.Name == method).ToArray();
            Assert.True(found.Length != 0, type + "." + method + " is not declared by this build's interop.");
            // A patch on a static member is not a patch Harmony can attach to an instance body, and every member
            // this slice patches is one.
            Assert.All(found, candidate =>
                Assert.False(candidate.IsStatic, type + "." + method + " is not an instance method."));
        }
    }

    /// <summary>Every type the hook set patches is a type this build declares: a renamed or removed family fails
    /// here rather than at plugin load.</summary>
    [Fact]
    public void EveryPatchedTypeExistsInThisBuildsInterop()
    {
        using var assembly = Interop();
        foreach (var type in Patched.Select(patch => patch.Type).Distinct(StringComparer.Ordinal))
            Assert.NotNull(Type(assembly, type));
    }

    /// <summary>The empty-clip and burst-sequence members the hooks are installed on are declared on the two
    /// burst-capable archetypes, and the semi-burst archetype declares no sequence end of its own — which is why
    /// it carries no burst hook.</summary>
    [Fact]
    public void TheBurstArchetypesDeclareBothEndsAndTheEmptyClipPath()
    {
        using var assembly = Interop();
        foreach (var type in new[] { "Gear.BWA_Burst", "Gear.BWA_Auto" })
        {
            var archetype = Type(assembly, type);
            Assert.Contains(archetype.Methods, candidate => candidate.Name == "OnStartFiring");
            Assert.Contains(archetype.Methods, candidate => candidate.Name == "OnStopFiring");
            Assert.Contains(archetype.Methods, candidate => candidate.Name == "OnFireShotEmptyClip");
        }
        var semiBurst = Type(assembly, "Gear.BWA_SemiBurst");
        Assert.DoesNotContain(semiBurst.Methods, candidate => candidate.Name == "OnStopFiring");
        Assert.DoesNotContain(semiBurst.Methods, candidate => candidate.Name == "OnFireShotEmptyClip");
    }

    /// <summary>The weapon members the module and the hooks read are declared with the types they read them as.
    /// The interop generator exposes a native field as a property of the field's own name, so the member is looked
    /// up as either and its declared type is what the read is checked against.</summary>
    [Fact]
    public void TheMembersTheModuleReadsAreDeclaredWithTheirTypes()
    {
        using var assembly = Interop();
        Assert.Equal("System.Int32", Member(assembly, "Gear.BulletWeapon", "m_burstMax").Type);
        Assert.Equal("Gear.BulletWeapon", Member(assembly, "Gear.BulletWeaponArchetype", "m_weapon").Type);
        foreach (var (type, member) in Read) Member(assembly, type, member);
    }

    /// <summary>The patch declarations as the hook source spells them: `[HarmonyPatch(typeof(Target), nameof(Target.Member))]`.
    /// A declaration in any other shape is reported as unreadable rather than skipped, so the scan cannot pass by
    /// finding nothing.</summary>
    private static (string Type, string Method)[] Declarations()
    {
        var source = File.ReadAllText(HookSource);
        var matches = Regex.Matches(source,
            @"\[HarmonyPatch\(typeof\((?<type>[\w\.]+)\),\s*nameof\((?<owner>[\w\.]+)\.(?<member>\w+)\)\)\]");
        Assert.NotEmpty(matches);
        var declarations = new List<(string, string)>();
        foreach (Match match in matches)
        {
            var type = match.Groups["type"].Value;
            var owner = match.Groups["owner"].Value;
            // The first argument names the patched type in full and the `nameof` names the same type by its short
            // name, so a declaration whose two halves disagree is a typo this catches.
            Assert.Equal(owner, type.Split('.')[^1]);
            declarations.Add((type, match.Groups["member"].Value));
        }
        // Every hook class in the file is a patch: one declaration per class, so the count is what keeps a hook
        // added without its own attribute from being missed.
        var classes = Regex.Matches(source, @"internal static class \w+").Count;
        Assert.Equal(classes, declarations.Count);
        return declarations.Distinct().ToArray();
    }

    /// <summary>The hook source, found by walking up from this test assembly to the repository that holds it.
    /// The build output lives under an artifacts path the caller chooses — by default outside the repository
    /// entirely — so the search starts from the compile-time path of this very file, which is the one location
    /// that is always inside the working tree, and falls back to walking up from the test assembly.</summary>
    private static string HookSource
    {
        get
        {
            var stamped = typeof(NativeMemberTests).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "AttackHooksSource")?.Value;
            if (stamped == null)
                throw new InvalidOperationException("The hook source path was not stamped into this assembly.");
            if (!File.Exists(stamped))
                throw new InvalidOperationException("The hook source was not found at " + stamped + ".");
            return stamped;
        }
    }

    /// <summary>One type of this build's interop by its full name, nested types included: the generator keeps a
    /// type's nested members inside it, and a lookup that only walked the top level would miss them.</summary>
    private static TypeDefinition Type(AssemblyDefinition assembly, string name)
        => Walk(assembly.MainModule.Types).SingleOrDefault(candidate => candidate.FullName == name)
            ?? throw new InvalidOperationException("This build's interop declares no " + name + ".");

    private static IEnumerable<TypeDefinition> Walk(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in Walk(type.NestedTypes)) yield return nested;
        }
    }

    /// <summary>One native member, read as the game's interop declares it. The generator exposes a field as a
    /// property of the field's own name, so a member that is not a field is looked up as a property before the
    /// read is reported missing.</summary>
    private static (string Type, string Name, string DeclaredAs) Member(AssemblyDefinition assembly, string type, string name)
    {
        var owner = Type(assembly, type);
        var field = owner.Fields.SingleOrDefault(candidate => candidate.Name == name);
        if (field != null) return (field.FieldType.FullName, name, "field");
        var property = owner.Properties.SingleOrDefault(candidate => candidate.Name == name);
        if (property != null) return (property.PropertyType.FullName, name, "property");
        throw new InvalidOperationException("This build's interop declares no " + type + "." + name + ".");
    }

    /// <summary>The interop assembly this build ships, read as metadata. The path comes from the same
    /// `GTFO_BEPINEX_PATH` every other project in this package compiles against.</summary>
    private static AssemblyDefinition Interop()
    {
        var root = Environment.GetEnvironmentVariable("GTFO_BEPINEX_PATH")
            ?? throw new InvalidOperationException("GTFO_BEPINEX_PATH is not set.");
        return AssemblyDefinition.ReadAssembly(Path.Combine(root, "interop", "Modules-ASM.dll"));
    }
}
