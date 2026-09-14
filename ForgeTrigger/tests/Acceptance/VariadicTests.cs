using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

internal sealed class VariadicTests : IAcceptanceGroup
{
    public VariadicTests() { }
    public void Run(string directory, Action<bool,string> check)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"variadic-reference.json")));
        using var registrations = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"authoring-registration-cases.json")));
        // The exact version is whatever the website catalog row declares, recorded once by the authoring audit.
        // Metadata cases repeat an id; every row for one id must still name one version.
        var versions = registrations.RootElement.GetProperty("cases").EnumerateArray()
            .GroupBy(row=>row.GetProperty("id").GetString()!)
            .ToDictionary(group=>group.Key, group=>group.Select(row=>row.GetProperty("version").GetString()!).Distinct().ToArray());
        var data = document.RootElement;
        check(data.GetProperty("kind").GetString()=="test-only-variadic-values", "variadic fixture provenance");
        var index = 0;
        foreach (var row in data.GetProperty("cases").EnumerateArray())
        {
            var id = row.GetProperty("capabilityId").GetString()!;
            check(versions.TryGetValue(id,out var version) && version.Length==1 && row.GetProperty("capabilityVersion").GetString()==version[0], "variadic exact version "+id);
            try
            {
                var expected = row.GetProperty("expected"); var value = Evaluate(row);
                bool Equal(object output) => output is bool flag ? flag==expected.GetBoolean() : (double)output==expected.GetDouble();
                check(Equal(value), "variadic shared result "+index);
                check(Equal(Evaluate(row)), "variadic deterministic repeat "+index);
            }
            catch (Exception error) { check(false,"variadic case "+index+": "+error.Message); }
            index++;
        }
        Boundaries(check);
    }
    private static object Evaluate(JsonElement row)
    {
        var values = row.GetProperty("values");
        return row.GetProperty("name").GetString() switch
        {
            "all" => VariadicNodes.All(values.EnumerateArray().Select(v=>v.GetBoolean()).ToArray()),
            "any" => VariadicNodes.Any(values.EnumerateArray().Select(v=>v.GetBoolean()).ToArray()),
            var name => VariadicNodes.Reduce(name switch
            {
                "add"=>ScalarOperation.Add, "multiply"=>ScalarOperation.Multiply,
                "minimum"=>ScalarOperation.Minimum, "maximum"=>ScalarOperation.Maximum,
                _=>throw new InvalidOperationException("Unknown fixture operation")
            },values.EnumerateArray().Select(v=>v.GetDouble()).ToArray())
        };
    }
    private static void Boundaries(Action<bool,string> check)
    {
        void Reject(string code, Action action)
        {
            try { action(); check(false,"accepted "+code); }
            catch (RuntimeContractException error) { check(error.Code==code,"rejection "+code); }
        }
        foreach (var count in new[] {0,1,33})
        {
            Reject("pure-variadic-count",()=>VariadicNodes.Reduce(ScalarOperation.Add,new double[count]));
            Reject("pure-variadic-count",()=>VariadicNodes.All(new bool[count]));
            Reject("pure-variadic-count",()=>VariadicNodes.Any(new bool[count]));
        }
        Reject("pure-variadic-operation",()=>VariadicNodes.Reduce(ScalarOperation.Divide,new[] {1d,2d}));
        Reject("pure-invalid-number",()=>VariadicNodes.Reduce(ScalarOperation.Multiply,new[] {0d,1d,double.NaN}));
        Reject("pure-nonfinite-result",()=>VariadicNodes.Reduce(ScalarOperation.Add,new[] {double.MaxValue,double.MaxValue}));
        var original = new[] {1d,2d,3d}; var before = original.ToArray();
        check(VariadicNodes.Reduce(ScalarOperation.Add,original)==6 && original.SequenceEqual(before),"variadic input not mutated");
        check(VariadicNodes.Reduce(ScalarOperation.Add,new[] {1e16,1,-1e16})==0,"ordered reduction does not reassociate");
        check(VariadicNodes.Reduce(ScalarOperation.Add,new[] {1e16,-1e16,1})==1,"different author order is preserved");
        check(!VariadicNodes.Any(new[] {false,false,false}) && VariadicNodes.Any(new[] {false,false,true}),"any reads its last input");
        check(ForgeTrigger.ModuleDefinition.Create().Handlers.Count==0,"pure variadic methods do not install runtime handlers");
    }
}
