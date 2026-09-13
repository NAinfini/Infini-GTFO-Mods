using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

internal sealed class VariadicTests : IAcceptanceGroup
{
    public VariadicTests() { }
    public void Run(string directory, Action<bool,string> check)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"variadic-reference.json")));
        var data = document.RootElement;
        check(data.GetProperty("kind").GetString()=="test-only-variadic-values", "variadic fixture provenance");
        var index = 0;
        foreach (var row in data.GetProperty("cases").EnumerateArray())
        {
            check(row.GetProperty("capabilityVersion").GetString()=="1.1.0", "variadic exact version");
            try
            {
                var expected = row.GetProperty("expected"); var value = Evaluate(row);
                bool Equal(object output) => output is bool flag ? flag==expected.GetBoolean()
                    : output is double number ? number==expected.GetDouble()
                    : ((IReadOnlyList<EntityReference>)output).SequenceEqual(expected.EnumerateArray().Select(RuntimeJson.Entity));
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
            "union" => VariadicNodes.Union(Lists(values)),
            "intersection" => VariadicNodes.Intersection(Lists(values)),
            var name => VariadicNodes.Reduce(name switch
            {
                "add"=>ScalarOperation.Add, "multiply"=>ScalarOperation.Multiply,
                "minimum"=>ScalarOperation.Minimum, "maximum"=>ScalarOperation.Maximum,
                _=>throw new InvalidOperationException("Unknown fixture operation")
            },values.EnumerateArray().Select(v=>v.GetDouble()).ToArray())
        };
    }
    private static IReadOnlyList<EntityReference>[] Lists(JsonElement values)
        => values.EnumerateArray().Select(list=>(IReadOnlyList<EntityReference>)list.EnumerateArray().Select(RuntimeJson.Entity).ToArray()).ToArray();
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
        Reject("pure-variadic-null",()=>VariadicNodes.Union(null!));
        var a = new EntityReference("test.variadic:a",1,1);
        Reject("pure-collection-null",()=>VariadicNodes.Intersection(new IReadOnlyList<EntityReference>[] {Array.Empty<EntityReference>(),new[] {a},null!}));
        var original = new[] {1d,2d,3d}; var before = original.ToArray();
        check(VariadicNodes.Reduce(ScalarOperation.Add,original)==6 && original.SequenceEqual(before),"variadic input not mutated");
        check(VariadicNodes.Reduce(ScalarOperation.Add,new[] {1e16,1,-1e16})==0,"ordered reduction does not reassociate");
        check(VariadicNodes.Reduce(ScalarOperation.Add,new[] {1e16,-1e16,1})==1,"different author order is preserved");
        var life = a with {LifeEpoch=2};
        var lists = new IReadOnlyList<EntityReference>[] {new[] {a,life},new[] {life},new[] {a,life}};
        check(VariadicNodes.Intersection(lists).SequenceEqual(new[] {life}),"intersection includes every input and full life identity");
        var large = Enumerable.Range(0,4096).Select(i=>a with {Id="test.variadic:"+i}).ToArray();
        Reject("pure-collection-output-budget",()=>VariadicNodes.Union(new IReadOnlyList<EntityReference>[] {large,new[] {a}}));
        check(ForgeTrigger.ModuleDefinition.Create().Handlers.Count==0,"pure variadic methods do not install runtime handlers");
    }
}
