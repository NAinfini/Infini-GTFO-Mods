using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;
using ForgeTrigger.Targeting;

// A real public-SDK composition using synthetic observations, never a gameplay commit.
internal static class WeightedCompositionTests
{
    internal static void Run(Action<bool,string> check)
    {
        var receivers = new[] { "test.receiver.heal", "test.receiver.damage" };
        RuntimeEntitySnapshot Snapshot(string id, string kind, string faction, long life = 1, bool health = true)
            => new(new EntityReference(id,1,life), kind, faction, "alive", Array.Empty<string>(),
                health ? receivers : Array.Empty<string>(), new[] {0d,0d,0d});
        var owner = Snapshot("test.luck:owner", "enemy", "blue");
        var source = Snapshot("test.luck:source", "player", "red");
        var ally = Snapshot("test.luck:ally", "enemy", "blue");
        var hostile = Snapshot("test.luck:hostile", "player", "red");
        var decoy = Snapshot("test.luck:decoy", "enemy", "red", health:false);
        using var world = new ObservationWorld(new[] {owner,source,ally,hostile,decoy});
        var kernel = world.Kernel;
        var actors = new RuntimeActorContext(new Dictionary<string,EntityReference>
            { ["owner"]=owner.Ref, ["source"]=source.Ref });
        var relations = new RuntimeFactionRelations(new RuntimeFactionRelation[]
            { new("blue","blue","ally"), new("blue","red","hostile"), new("red","blue","ally") });
        JsonElement Policy(string relation) => RuntimeJson.From(new {schemaVersion=1,anchor="owner",kinds="any",
            relations=new[] {relation},lifeStates=new[] {"alive"},requireTags=Array.Empty<string>(),
            excludeTags=Array.Empty<string>(),sort="stable-id",maxTargets=16});
        var candidates = new[] {decoy.Ref,hostile.Ref,ally.Ref,hostile.Ref};
        foreach (var receiver in receivers)
        foreach (var relation in new[] {"ally","hostile"})
        {
            var expected = relation=="ally" ? ally.Ref : hostile.Ref;
            var filtered = ObservedRecipientFilter.Select(kernel,candidates,actors,relations,Policy(relation),new[] {receiver});
            check(filtered.Selected.SequenceEqual(new[] {expected}), "weighted composition explicit relation/receiver "+relation+" "+receiver);
            var observations = world.Observations;
            var weights = filtered.Selected.Select(target=>new WeightedCandidate(target,3)).ToArray();
            var selected = WeightedSampling.Sample(weights,5,42,WeightedSamplingMode.WithoutReplacement);
            check(selected.Selected.SequenceEqual(new[] {expected}) && selected.UnfilledCount==4,
                "filtered weighted shortfall is not a five-target commit");
            check(world.Observations==observations, "pure weighting does not invent extra world queries");
            var current = ObservedEntityNodes.RequireReceivers(kernel,selected.Selected,receiver);
            check(current.Count==1 && current[0].Ref==expected, "selected occurrence passes explicit fresh receiver check");
            var repeated = WeightedSampling.Sample(weights,5,42,WeightedSamplingMode.WithReplacement);
            check(repeated.SelectedCount==5 && repeated.Selected.Distinct().Count()==1,
                "with-replacement preserves occurrences rather than inventing five entities");
        }
        void Reject(string code, Action action)
        {
            try { action(); check(false,"weighted composition accepted "+code); }
            catch (RuntimeContractException error) { check(error.Code==code,"weighted composition rejected "+code); }
        }
        var sourceOnly = new RuntimeActorContext(new Dictionary<string,EntityReference> { ["source"]=source.Ref });
        Reject("actor-missing",()=>ObservedRecipientFilter.Select(kernel,candidates,sourceOnly,relations,Policy("hostile"),receivers));
        var chosen = WeightedSampling.Sample(new[] {new WeightedCandidate(hostile.Ref,1)},1,7,WeightedSamplingMode.WithoutReplacement);
        var before = world.Observations;
        Reject("weighted-entropy-budget",()=>WeightedSampling.Sample(new[] {
            new WeightedCandidate(ally.Ref,double.Epsilon),new WeightedCandidate(hostile.Ref,double.MaxValue)},
            1,0,WeightedSamplingMode.WithReplacement,1));
        check(world.Observations==before && kernel.QueuedEvents==0, "sampling rejection does not observe or commit gameplay");
        world.Entities[hostile.Ref.Id] = Snapshot(hostile.Ref.Id,"player","red",health:false);
        Reject("receiver-unsupported",()=>ObservedEntityNodes.RequireReceivers(kernel,chosen.Selected,receivers[0]));
        check(chosen.Selected[0]==hostile.Ref, "old selection is immutable but not a lasting receiver permission");
        world.Entities[hostile.Ref.Id] = Snapshot(hostile.Ref.Id,"player","blue");
        var changed = ObservedRecipientFilter.Select(kernel,chosen.Selected,actors,relations,Policy("hostile"),receivers);
        check(changed.Selected.Count==0 && changed.Excluded.Single().Code=="relation-filter",
            "current relation is rechecked even without a life change");
        var empty = WeightedSampling.Sample(changed.Selected.Select(r=>new WeightedCandidate(r,1)).ToArray(),
            5,42,WeightedSamplingMode.WithReplacement);
        check(empty.SelectedCount==0 && empty.Code=="no-positive-weight", "empty filtered pool cannot fall back to unfiltered targets");
        world.Entities[hostile.Ref.Id] = Snapshot(hostile.Ref.Id,"player","red",life:2);
        Reject("entity-query-incomplete",()=>ObservedEntityNodes.RequireReceivers(kernel,chosen.Selected,receivers[0]));
        var currentLife = hostile.Ref with {LifeEpoch=2};
        check(ObservedEntityNodes.RequireReceivers(kernel,new[] {currentLife},receivers[0]).Single().Ref==currentLife,
            "new life requires an explicitly new reference");
        check(kernel.LoadedPlans==0 && kernel.QueuedEvents==0, "selection and revalidation are never gameplay commits");
        kernel.BeginWorld(2); kernel.Advance(0,true);
        Reject("entity-query-incomplete",()=>ObservedRecipientFilter.Select(kernel,new[] {currentLife},actors,relations,Policy("hostile"),receivers));
        check(kernel.QueuedEvents==0, "world transition cannot turn sampled references into queued effects");
    }
}
