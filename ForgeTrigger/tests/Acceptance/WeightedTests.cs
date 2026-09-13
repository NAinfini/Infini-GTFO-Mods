using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

internal sealed class WeightedTests : IAcceptanceGroup
{
    public WeightedTests() { }
    public void Run(string directory, Action<bool,string> check)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"weighted-reference.json")));
        var data=document.RootElement;
        check(data.GetProperty("algorithm").GetString()==WeightedSampling.Algorithm,"exact weighted algorithm version");
        check(data.GetProperty("runtimeBinding").ValueKind==JsonValueKind.Null,"weighted reference does not claim a binding");
        var index=0;
        foreach(var row in data.GetProperty("cases").EnumerateArray())
        {
            var candidates=row.GetProperty("candidates").EnumerateArray().Select(r=>new WeightedCandidate(RuntimeJson.Entity(r.GetProperty("target")),r.GetProperty("weight").GetDouble())).ToArray();
            var count=row.GetProperty("count").GetInt32(); var seed=row.GetProperty("seed").GetInt64();
            var mode=row.GetProperty("mode").GetString()=="with-replacement"?WeightedSamplingMode.WithReplacement:WeightedSamplingMode.WithoutReplacement;
            var expected=row.GetProperty("expected");
            var expectedRefs=expected.GetProperty("selected").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
            var actual=WeightedSampling.Sample(candidates,count,seed,mode);
            check(actual.Selected.SequenceEqual(expectedRefs),"weighted cross-language result "+index);
            check(actual.EntropyWords==expected.GetProperty("entropyWords").GetInt32(),"weighted exact entropy accounting "+index);
            check(actual.EligibleCount==expected.GetProperty("eligible").GetInt32(),"weighted eligibility "+index);
            check(actual.UnfilledCount==count-expectedRefs.Length,"weighted explicit shortfall "+index);
            check(WeightedSampling.Sample(candidates.AsEnumerable().Reverse().ToArray(),count,seed,mode).Selected.SequenceEqual(actual.Selected),"weighted enumeration independence "+index);
            index++;
        }
        Boundaries(check);
    }
    private static void Boundaries(Action<bool,string> check)
    {
        var a=new EntityReference("test.weight:a",1,1); var b=a with {Id="test.weight:b"};
        void Reject(string code, Action action)
        {
            try {action();check(false,"weighted accepted "+code);}
            catch(RuntimeContractException error){check(error.Code==code,"weighted rejection "+code);}
        }
        var one=new[]{new WeightedCandidate(a,1)};
        foreach(var weight in new[]{-1d,double.NaN,double.PositiveInfinity,double.NegativeInfinity})
            Reject("weighted-weight",()=>WeightedSampling.Sample(new[]{new WeightedCandidate(a,weight)},1,0,WeightedSamplingMode.WithoutReplacement));
        Reject("weighted-candidate-budget",()=>WeightedSampling.Sample(null!,1,0,WeightedSamplingMode.WithoutReplacement));
        Reject("weighted-candidate-null",()=>WeightedSampling.Sample(new WeightedCandidate[]{null!},1,0,WeightedSamplingMode.WithoutReplacement));
        Reject("weighted-duplicate-candidate",()=>WeightedSampling.Sample(new[]{one[0],one[0]},1,0,WeightedSamplingMode.WithReplacement));
        Reject("weighted-count",()=>WeightedSampling.Sample(one,0,0,WeightedSamplingMode.WithReplacement));
        Reject("weighted-count",()=>WeightedSampling.Sample(one,257,0,WeightedSamplingMode.WithReplacement));
        Reject("weighted-mode",()=>WeightedSampling.Sample(one,1,0,(WeightedSamplingMode)999));
        Reject("pure-seed",()=>WeightedSampling.Sample(Array.Empty<WeightedCandidate>(),1,-1,WeightedSamplingMode.WithReplacement));
        Reject("weighted-entropy-budget",()=>WeightedSampling.Sample(one,1,0,WeightedSamplingMode.WithReplacement,0));
        Reject("weighted-candidate-budget",()=>WeightedSampling.Sample(Enumerable.Repeat(one[0],4097).ToArray(),1,0,WeightedSamplingMode.WithReplacement));
        Reject("weighted-entropy-budget",()=>WeightedSampling.Sample(new[]{new WeightedCandidate(a,double.Epsilon),new WeightedCandidate(b,double.MaxValue)},1,0,WeightedSamplingMode.WithReplacement,1));
        var repeated=WeightedSampling.Sample(one,5,42,WeightedSamplingMode.WithReplacement);
        var unique=WeightedSampling.Sample(one,5,42,WeightedSamplingMode.WithoutReplacement);
        check(repeated.SelectedCount==5 && repeated.UnfilledCount==0 && repeated.EntropyWords==0,"replacement explicitly allows five occurrences");
        check(unique.SelectedCount==1 && unique.UnfilledCount==4 && unique.Code=="shortfall","no-replacement explicitly reports shortage");
        var zero=WeightedSampling.Sample(new[]{new WeightedCandidate(a,0)},5,42,WeightedSamplingMode.WithReplacement);
        check(zero.SelectedCount==0 && zero.Code=="no-positive-weight" && zero.ZeroWeightCount==1,"zero weights do not silently become uniform");
        var extremes=new[]{new WeightedCandidate(a,double.Epsilon),new WeightedCandidate(b,double.MaxValue)};
        var both=WeightedSampling.Sample(extremes,2,42,WeightedSamplingMode.WithoutReplacement);
        check(both.Selected.Count==2 && both.Selected.Contains(a) && both.Selected.Contains(b),"positive subnormal mass is never discarded");
        var life=a with{LifeEpoch=2};
        check(WeightedSampling.Sample(new[]{one[0],new WeightedCandidate(life,1)},2,0,WeightedSamplingMode.WithoutReplacement).Selected.Count==2,"new life is a distinct weighted identity");
        var inputs=new[]{new WeightedCandidate(a,1),new WeightedCandidate(b,3)}; var original=inputs.ToArray();
        var sample=WeightedSampling.Sample(inputs,32,7,WeightedSamplingMode.WithReplacement);
        check(inputs.SequenceEqual(original),"weighted sampling leaves input unchanged");
        check(sample.Selected.SequenceEqual(WeightedSampling.Sample(new[]{new WeightedCandidate(a,2),new WeightedCandidate(b,6)},32,7,WeightedSamplingMode.WithReplacement).Selected),"exact ratio scaling preserves the random sequence");
        inputs[0]=new WeightedCandidate(life,9);
        check(!sample.Selected.Contains(life),"selection is not backed by mutable candidate storage");
        try {((IList<EntityReference>)sample.Selected)[0]=life;check(false,"weighted result was mutable");}
        catch(NotSupportedException){check(true,"weighted result is read-only");}
        Reject("weighted-entropy-budget",()=>WeightedSampling.Sample(new[]{new WeightedCandidate(a,1),new WeightedCandidate(b,1)},2,0,WeightedSamplingMode.WithReplacement,1));
        Reject("pure-seed",()=>WeightedSampling.Sample(one,1,(long)uint.MaxValue+1,WeightedSamplingMode.WithReplacement));
        Reject("invalid-string",()=>WeightedSampling.Sample(new[]{new WeightedCandidate(a with{Id=" invalid"},1)},1,0,WeightedSamplingMode.WithReplacement));
        var bCount=0;
        for(var seed=0;seed<4096;seed++)
            if(WeightedSampling.Sample(original,1,seed,WeightedSamplingMode.WithReplacement).Selected[0]==b)bCount++;
        check(bCount>2800 && bCount<3400,"deterministic 1:3 weight sanity, not a proof of all-seed uniformity");
        var maximum=Enumerable.Range(0,4096).Select(i=>new WeightedCandidate(a with{Id="test.weight:"+i},1)).ToArray();
        var upper=WeightedSampling.Sample(maximum,256,42,WeightedSamplingMode.WithoutReplacement);
        check(upper.SelectedCount==256 && upper.Selected.Distinct().Count()==256,"maximum supported candidate/selection budgets");
        check(ForgeTrigger.ModuleDefinition.Create().BindingSupport.Count==0,"weighted helper grants no runtime binding or authority");
    }
}
