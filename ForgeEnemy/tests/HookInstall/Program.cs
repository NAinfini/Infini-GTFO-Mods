using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// The one invariant `EnemyHookInstall` exists for, asserted against the assembly that ships: the plugin installs
// that table, the table is exactly the union of the hook families the package declares, every Harmony patch class
// in `ForgeEnemy.Native.dll` is in it, and nothing is listed twice. A hook family written but left out of the
// table — the way a declared-but-never-installed family happens — fails here instead of at run time in the game.
//
// The suite is reflection over the built assembly rather than a second compilation of the hook sources: a list
// this file could compile would only agree with itself. The package is loaded with probe directories because
// reading the attributes and base types of the classes the table names needs the BepInEx profile the plugin ships
// against.
if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: HookInstall <BepInEx> <ForgeEnemy.Native.dll> <report.json> [probeDir...]");
    return 2;
}

var bepinex = Path.GetFullPath(args[0]);
var enemyPath = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args[2]);
var probes = new List<string>
{
    Path.GetDirectoryName(enemyPath)!, Path.Combine(bepinex, "core"), Path.Combine(bepinex, "interop"),
    Path.Combine(bepinex, "plugins")
};
probes.AddRange(args.Skip(3).Select(Path.GetFullPath));

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in probes)
    {
        var candidate = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
    }
    return null;
};

var checks = new List<CheckRow>();
var hookNames = new List<string>();
void Check(string id, bool passed, string detail = "") => checks.Add(new CheckRow(id, passed, detail));
string Names(IEnumerable<Type> types) => string.Join(",", types.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(enemyPath);
const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

try { assembly.GetTypes(); Check("enemy.all-types-loaded", true); }
catch (ReflectionTypeLoadException error)
{
    Check("enemy.all-types-loaded", false, error.LoaderExceptions.Length + " type(s) could not be loaded: "
        + string.Join(" | ", error.LoaderExceptions.Take(3).Select(e => e?.Message)));
}

var install = assembly.GetType("ForgeEnemy.Native.EnemyHookInstall", throwOnError: false);
Check("enemy.install-table-present", install != null);
if (install == null) return Finish();

var declared = ((System.Collections.IEnumerable)install.GetProperty("Declared", statics)!.GetValue(null)!)
    .Cast<Type>().ToArray();
var families = ((System.Collections.IEnumerable)install.GetField("Families", statics)!.GetValue(null)!)
    .Cast<System.Collections.IEnumerable>().SelectMany(f => f.Cast<Type>()).ToArray();

// A table that answered with nothing would make every set comparison below trivially true.
Check("enemy.declared-not-empty", declared.Length > 0, "declared=" + declared.Length);
Check("enemy.install-table-is-the-family-union",
    declared.Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal)
        .SequenceEqual(families.Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal)),
    "table=" + Names(declared) + " families=" + Names(families));
Check("enemy.no-duplicate-declarations", declared.Length == declared.Distinct().Count(),
    "declared=" + declared.Length + " distinct=" + declared.Distinct().Count());

// The direction that catches an omission: a patch class nothing installs is a hook the game never gets.
var patchClasses = assembly.GetTypes().Where(IsHarmony).ToArray();
hookNames.AddRange(patchClasses.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));
var missing = patchClasses.Where(t => !declared.Contains(t)).ToArray();
Check("enemy.patch-classes-installed", missing.Length == 0,
    "installed=" + Names(patchClasses.Where(declared.Contains)) + " missing=" + Names(missing));

// The other direction: a declared class that carries no patch is a name the install loop would fail on.
var notPatches = declared.Where(t => !IsPatch(t)).ToArray();
Check("enemy.declared-are-patch-classes", notPatches.Length == 0, "without-patch=" + Names(notPatches));

// The table only decides anything if the load path walks it, so the assembly is read for that call: `Load` hands
// it to the session as a callback, so the call site is the compiler's own closure method, which is still a method
// of a type nested in `Plugin`. One site and no other.
var installMethod = install.GetMethod("Install", statics)!;
var installSites = new List<MethodInfo>();
foreach (var type in assembly.GetTypes())
    foreach (var method in type.GetMethods(statics | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        if (method.GetMethodBody() != null && CallsMethod(method, installMethod)) installSites.Add(method);
Check("enemy.plugin-installs-the-table",
    installSites.Count == 1
    && installSites[0].DeclaringType!.FullName!.StartsWith("ForgeEnemy.Native.Plugin", StringComparison.Ordinal),
    "call sites=" + string.Join(",", installSites.Select(m => m.DeclaringType?.FullName + "." + m.Name)));

return Finish();

bool IsHarmony(MemberInfo member) => member.GetCustomAttributesData()
    .Any(a => a.AttributeType.Namespace == "HarmonyLib");
bool IsPatch(Type type) => type.GetCustomAttributesData()
    .Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");

/// <summary>Whether one method's body calls the given method, read from the IL rather than from a source copy: a
/// `call`/`callvirt` whose resolved token is that method. The scan is byte-wise, so a hit is a resolved method
/// token, which is what makes it evidence rather than a text match.</summary>
bool CallsMethod(MethodInfo caller, MethodInfo target)
{
    var body = caller.GetMethodBody();
    var il = body?.GetILAsByteArray();
    if (il == null) return false;
    for (int index = 0; index + 4 < il.Length; index++)
    {
        if (il[index] is not (0x28 or 0x6F)) continue;
        try { if (caller.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1)) == target) return true; }
        catch (Exception) { /* an operand that is not a method token is not this call */ }
    }
    return false;
}

int Finish()
{
    int failed = checks.Count(c => !c.Passed), passed = checks.Count - failed;
    var report = new
    {
        schemaVersion = 3,
        verification = "built-enemy-assembly-reflected-for-the-one-harmony-install-table",
        gameExecuted = false,
        utc = DateTimeOffset.UtcNow,
        enemySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(enemyPath))),
        hookClasses = hookNames,
        passed,
        failed,
        checks
    };
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    foreach (var row in checks.Where(c => !c.Passed))
        Console.Error.WriteLine("FAIL " + row.Id + ": " + row.Detail);
    Console.WriteLine($"PASS {passed}/{checks.Count} hook install checks; failed {failed}");
    return failed == 0 ? 0 : 1;
}

internal sealed record CheckRow(string Id, bool Passed, string Detail);
