using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;
using ForgeTrigger.Targeting;

// A real public-SDK composition using synthetic observations, never a gameplay commit: the relation filter narrows
// an explicit candidate set, the weighted sampler draws from what survived, and the receiver check is the explicit
// action-side question. Each stage is the existing algorithm; nothing here is a second implementation.
internal static class WeightedCompositionTests
{
    internal static void Run(Action<bool,string> check)
    {
        var receivers = new[] { "test.receiver.heal", "test.receiver.damage" };
        RuntimeEntitySnapshot Snapshot(string id, string kind, string faction, long life = 1, bool health = true)
            => new(new EntityReference(id,1,life), kind, faction, "alive", Array.Empty<string>(),
                health ? receivers : Array.Empty<string>(), new[] {0d,0d,0d});
        var self = Snapshot("test.luck:self", "deployable", "blue");
        var ally = Snapshot("test.luck:ally", "enemy", "blue");
        var hostile = Snapshot("test.luck:hostile", "player", "red");
        var decoy = Snapshot("test.luck:decoy", "enemy", "red", health:false);
        using var world = new ObservationWorld(new[] {self,ally,hostile,decoy});
        var kernel = world.Kernel;
        var relations = new RuntimeFactionRelations(new RuntimeFactionRelation[]
            { new("blue","blue","ally"), new("blue","red","hostile") });
        var candidates = new[] {decoy.Ref,hostile.Ref,ally.Ref,hostile.Ref};
        var session = world.Session();
        // The decoy is the same faction as the hostile and carries only the damage receiver. The relation filter
        // keeps it — the row declares no receiver requirement at all — and the receiver question is asked later, by
        // the action that consumes the references, which is exactly the separation this composition is here to show.
        foreach (var (relation, names) in new (string, string[])[]
            { ("ally", new[]{"ally"}), ("hostile", new[]{"decoy","hostile"}) })
        {
            var expected = names.Select(name => name == "ally" ? ally.Ref : name == "decoy" ? decoy.Ref : hostile.Ref).ToArray();
            var filtered = ObservedRecipientFilter.Select(session,candidates,self.Ref,relations,
                RecipientFilterRequest.Read(relation));
            check(filtered.Selected.SequenceEqual(expected), "filtered relation names exactly the "+relation+" targets");
            check(ObservedSpaceNodes.Filter(session,candidates,self.Ref,relations,RecipientFilterRequest.Read("self")).Count==0,
                "the composition's own instance is not answered as an ally or a hostile of itself");
            var observations = world.Observations;
            var weights = filtered.Selected.Select(target=>new WeightedCandidate(target,3)).ToArray();
            var selected = WeightedSampling.Sample(weights,5,42,WeightedSamplingMode.WithoutReplacement);
            check(selected.Selected.SequenceEqual(expected) && selected.UnfilledCount==5-expected.Length,
                "filtered weighted shortfall is not a five-target commit");
            check(world.Observations==observations, "pure weighting does not invent extra world queries");
            // The receiver requirement is the action's own question, asked on the references the plan actually
            // carries, and it is asked separately from the filter: the filter row declares no such parameter.
            check(ObservedEntityNodes.RequireReceivers(kernel,selected.Selected.Where(row=>row!=decoy.Ref).ToArray(),
                    receivers[0]).Count>0,
                "the references that carry the receiver pass the action's own check");
            Reject("receiver-unsupported",()=>ObservedEntityNodes.RequireReceivers(kernel,new[] {decoy.Ref},receivers[0]));
            var repeated = WeightedSampling.Sample(weights,5,42,WeightedSamplingMode.WithReplacement);
            check(repeated.SelectedCount==5 && repeated.Selected.Distinct().Count()==expected.Length,
                "with-replacement preserves occurrences rather than inventing five entities");
        }
        void Reject(string code, Action action)
        {
            try { action(); check(false,"weighted composition accepted "+code); }
            catch (RuntimeContractException error) { check(error.Code==code,"weighted composition rejected "+code); }
        }
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
        var changed = ObservedRecipientFilter.Select(session,chosen.Selected,self.Ref,relations,RecipientFilterRequest.Read("hostile"));
        check(changed.Selected.Count==0, "current relation is rechecked even without a life change");
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
        Reject("entity-query-incomplete",()=>ObservedRecipientFilter.Select(world.Session(),new[] {currentLife},self.Ref,
            relations,RecipientFilterRequest.Read("hostile")));
        check(kernel.QueuedEvents==0, "world transition cannot turn sampled references into queued effects");
    }
}
