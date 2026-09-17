using System.Text;
using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// The three world rules the gate's state obeys besides its options (R12), each of which is about state that
/// outlives one dispatch: the die is the world's own and travels in the checkpoint, a per-instance card's scope
/// budget is asked of the instances that still exist, and a cooldown of zero is refused before a card can book one.
/// </summary>
internal static class GateOptionTests
{
    private const string Id = "example.alpha";

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void RejectCode(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }

        // A cooldown of zero is the option that is not one: it would let every arrival through while still being a
        // clock a card had to name a scope for, so it is refused for every scope that could book it rather than
        // accepted as "no cooldown" in one scope and refused in another.
        foreach (var scope in new[] { "player", "level" })
        {
            var scenario = new Scenario();
            scenario.Register(Id);
            RejectCode(() => scenario.Gated("zero-" + scope, Id, "{\"cooldown\":0,\"scope\":\"" + scope + "\"}"),
                "gate-cooldown", "a " + scope + " cooldown of zero");
        }
        {
            var scenario = new Scenario(subjectMounts: true);
            scenario.Register(Id);
            RejectCode(() => scenario.GatedOnSubject("zero-instance", Id, "{\"cooldown\":0,\"scope\":\"instance\"}"),
                "gate-cooldown", "an instance cooldown of zero");
        }

        // A per-instance card books one window per mounting entity and the budget is read against the instances
        // that still exist: once a thousand and twenty-four of them are gone, the next mounted entity gets its own
        // window instead of being told the card is out of room.
        {
            var scenario = new Scenario(subjectMounts: true); var module = scenario.Register(Id);
            scenario.GatedOnSubject("crowd", Id, "{\"accumulate\":2,\"scope\":\"instance\"}");
            for (var index = 1; index <= 1024; index++)
                Check(Beat(scenario, module, "c" + index, Id + ":" + index).GateRefusals.Single().Code == "gate-accumulate",
                    "instance " + index + " books its own window");
            var full = Beat(scenario, module, "c-full", Id + ":1025");
            Check(full.GateRefusals.Single().Code == "gate-scope",
                "a thousand and twenty-four live instances is the card's whole budget");
            // The level is rebuilt with the same objects and the same plan: every reference the card is holding now
            // names an entity of the previous life, which is exactly what the sweep at the cap has to notice.
            scenario.Life++;
            var fresh = Beat(scenario, module, "c-fresh", Id + ":1025");
            Check(fresh.GateRefusals.Single().Code == "gate-accumulate",
                "the instances of the previous life are swept before the cap refuses a new one");
        }

        // The die belongs to the world: a checkpoint carries the seed, so the world that restores it draws the
        // sequence the saving world would have drawn, and a world begun with another seed draws another one.
        {
            var saved = new Scenario(); var savedModule = saved.Register(Id);
            saved.Gated("rand", Id, "{\"probability\":0.5}");
            saved.Kernel.BeginWorld(2, 111); saved.World = 2;
            for (var index = 0; index < 3; index++) Beat(saved, savedModule, "a" + index);
            var checkpoint = saved.Kernel.CaptureCheckpoint();
            var tailSaved = Tail(saved, savedModule, "a", 3, 5);

            // Any other seed is a different sequence, which is what makes the restored one observable: the probe
            // also says whether this world's own seed would have drawn something else.
            long other = 0; var tailOther = "";
            foreach (var candidate in new[] { 222L, 333, 444, 555, 666, 777, 888, 999 })
            {
                var probe = new Scenario(); var probeModule = probe.Register(Id);
                probe.Gated("rand", Id, "{\"probability\":0.5}");
                probe.Kernel.BeginWorld(2, candidate); probe.World = 2;
                for (var index = 0; index < 3; index++) Beat(probe, probeModule, "p" + index);
                var tail = Tail(probe, probeModule, "p", 3, 5);
                if (tail == tailSaved) continue;
                other = candidate; tailOther = tail; break;
            }
            Check(other != 0, "another seed draws another sequence");

            var restored = new Scenario(); var restoredModule = restored.Register(Id);
            restored.Gated("rand", Id, "{\"probability\":0.5}");
            restored.Kernel.BeginWorld(2, other); restored.World = 2;
            Beat(restored, restoredModule, "b0");
            restored.Kernel.RestoreCheckpoint(checkpoint);
            var tailRestored = Tail(restored, restoredModule, "b", 1, 5);
            Check(tailRestored == tailSaved, "a restored checkpoint carries the world's die and the saving rolls with it");
            Check(tailRestored != tailOther, "the world's own seed would have drawn the sequence of that seed");
        }

        return checks;
    }

    /// <summary>One arrival and the advance that carries the world to the next tick, exactly as the level's own
    /// publisher writes it: the plan's mount accepts the entity the event is about.</summary>
    private static TickResult Beat(Scenario scenario, RuntimeModuleHandle module, string id, string? target = null)
    {
        var tick = scenario.Kernel.CurrentTick + 1;
        module.Publish(new RuntimeEvent(id, Fixture.Trigger(Id), scenario.World, tick, "shared-scope",
            RuntimeJson.From(new Dictionary<string, object>
            {
                ["target"] = new EntityReference(target ?? Id + ":1", scenario.World, scenario.Life)
            })));
        return scenario.Kernel.Advance(tick, true);
    }

    /// <summary>The die's own bit per arrival, read the way a card reads it: a refusal by probability is a failed
    /// roll and every other outcome is a roll that passed. Everything else about the world is held still, so the
    /// string is a function of the seed and of how many rolls the row has already made.</summary>
    private static string Tail(Scenario scenario, RuntimeModuleHandle module, string prefix, int from, int count)
    {
        var text = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            var result = Beat(scenario, module, prefix + (from + index));
            text.Append(result.GateRefusals.Any(refusal => refusal.Code == "gate-probability") ? '0' : '1');
        }
        return text.ToString();
    }
}
