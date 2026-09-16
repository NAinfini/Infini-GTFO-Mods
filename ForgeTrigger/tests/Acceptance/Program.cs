using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

if (args.Length != 2 || args[0] is not ("export" or "check"))
    throw new ArgumentException("Usage: AcceptanceTests <export|check> <evidence directory>");
var output = Path.GetFullPath(args[1]);
if (args[0] == "export")
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "independent-acceptance-no-game"));
    using var combat = kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
    using var trigger = kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
    File.WriteAllText(Path.Combine(output, "sdk-canonical-manifest.json"), kernel.ExportManifest());
    return 0;
}
var assertions = 0; var failures = new List<string>();
void Check(bool value, string name)
{
    assertions++;
    if (!value) { failures.Add(name); Console.WriteLine("FAIL: " + name); }
}
void Group(string name, Action action)
{
    try { action(); }
    catch (Exception error) { Check(false, name + ": " + error.GetType().Name + ": " + error.Message); }
}
Group("authoring", () => AuthoringContractTests.Run(Path.Combine(output, "authoring-registration-cases.json"), Check));
var groupTypes = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && typeof(IAcceptanceGroup).IsAssignableFrom(t))
    .OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
Check(groupTypes.Length > 0, "independent behavior tests were discovered");
foreach (var type in groupTypes)
    Group(type.Name, () => ((IAcceptanceGroup)Activator.CreateInstance(type)!).Run(output, Check));
var result = new {status=failures.Count==0?"passed":"failed", scope="independent-authoring-and-pure-computation",
    gameVerified=false, publicationReady=false, assertions, groups=groupTypes.Select(t=>t.Name), failures};
File.WriteAllText(Path.Combine(output,"acceptance-result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(result));
return failures.Count==0?0:1;

internal interface IAcceptanceGroup
{
    void Run(string evidenceDirectory, Action<bool,string> check);
}
