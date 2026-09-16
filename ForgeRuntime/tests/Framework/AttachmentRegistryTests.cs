using ForgeRuntime.Framework;

/// <summary>
/// Removal of a module's own mount matchers. One kind has exactly one owner, so a matcher left behind by an
/// unregistered provider would refuse the replacement provider's claim as a conflict, and a plan mounted on
/// that kind would be accepted for a module that is gone. The cases here are both halves of that rule: the
/// same kernel accepts the provider again, and while no provider holds the kind a plan mounted on it is
/// refused by name.
/// </summary>
internal static class AttachmentRegistryTests
{
    private const string Id = "example.attachments";
    private const string Other = "example.attachments.other";

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

        // The two mounts a plan can name here: one matched against the event's subject, one judged from the
        // mount target alone. No kind belongs to the kernel, so both need an owner to be loadable.
        object MapMount(string reference) => new { kind = "map-object", category = "zone", reference };
        var level = (object)new { kind = "level", reference = "fixture-level" };
        // A provider that claims a mount kind declares it as an attachment matcher, either subject-matched or
        // subject-free; the matcher itself answers nothing here, because what is under test is the registry's
        // ownership of the kind, not the matching.
        RuntimeModule Claiming(string id, bool mounts) => Fixture.Module(id) with
        {
            AttachmentMatchers = mounts ? new Dictionary<string, AttachmentMatcherRegistration>
            {
                ["map-object"] = AttachmentMatcherRegistration.BySubject((_, _, _) => false),
                ["level"] = AttachmentMatcherRegistration.ByScope((_, _) => false)
            } : null
        };

        var kernel = new RuntimeKernel(Fixture.Identity);
        kernel.BeginWorld(1);
        // A plan's pin table comes from the live manifest, so every plan below is written while the provider it
        // names is registered, and loaded after the case has changed the registry.
        var provider = kernel.RegisterModule(Claiming(Id, true), RuntimeLogLevel.Off);
        var held = Fixture.Plan(kernel, "mount-held", Id, attachments: new[] { level, MapMount("z1") });
        kernel.LoadPlan(held);
        Check(true, "a plan mounted on the claimed kinds loads while its provider holds them");
        provider.Dispose();

        // The second registration under the same provider id is the whole point: a matcher left behind by the
        // first one turns this claim into an `attachment-kind` conflict instead.
        var replacement = kernel.RegisterModule(Claiming(Id, true), RuntimeLogLevel.Off);
        Check(replacement.IsRegistered, "the same kind can be claimed again after its provider unregistered");
        replacement.Dispose();

        // With that provider gone its own bindings are gone too, so a plan of its own is refused on the binding
        // lock before the mount kind is ever read. A plan of a *registered* provider is the case where the mount
        // kind is the only thing wrong with it.
        var other = kernel.RegisterModule(Claiming(Other, false), RuntimeLogLevel.Off);
        var unowned = Fixture.Plan(kernel, "mount-unowned", Other, attachments: new[] { level, MapMount("z1") });
        var onlyLevel = Fixture.Plan(kernel, "mount-level", Other, attachments: new[] { level });
        Check(other.IsRegistered, "a provider that claims no mount kind registers");
        RejectCode(() => kernel.LoadPlan(unowned), RuntimeAbiCodes.AttachmentKind, "a kind no registered provider holds is refused at load");
        Check(true, "the mixed mount was refused on the subject-free kind its provider does not hold");
        // A plan whose only mount is the subject-free kind is refused the same way: no kind is exempt, so a plan
        // can never be accepted for a target nothing in the process can match.
        RejectCode(() => kernel.LoadPlan(onlyLevel), RuntimeAbiCodes.AttachmentKind,
            "a subject-free kind no registered provider holds is refused at load");
        return checks;
    }
}
