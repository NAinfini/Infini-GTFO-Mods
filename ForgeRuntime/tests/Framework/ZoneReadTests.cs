using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// The kernel's zone read: which of the two things a null answer used to mean is which. A provider that answered
/// has placed the entity — in a zone, or in no zone at all — and "this entity stands outside every zone" is an
/// answer a selector can exclude by name, while a read nobody could make is refused with a code. The cases below
/// drive the kernel's own entry point with responder doubles, because the two outcomes are decided there and not
/// in any one package's table.
/// </summary>
internal static class ZoneReadTests
{
    private const string Kind = "example.zone.subject";
    private static readonly string[] NoPermissions = Array.Empty<string>();

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }

        // One provider whose responder is the case's own answer, registered on the kind it owns.
        RuntimeKernel Kernel(Func<EntityReference, EntityReference?> responder, out EntityReference subject)
        {
            var kernel = new RuntimeKernel(Fixture.Identity);
            var module = new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
                new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
                new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal) { [Kind] = _ => true })
            {
                EntityZones = responder == null
                    ? null
                    : new Dictionary<string, Func<EntityReference, EntityReference?>>(StringComparer.Ordinal) { [Kind] = responder }
            };
            kernel.RegisterModule(module, RuntimeLogLevel.Off);
            kernel.BeginWorld(1);
            subject = new EntityReference(Kind + ":1", 1, 1);
            return kernel;
        }

        // A responder that names a zone answers it, and the code says which answer this was.
        {
            var zone = new EntityReference(RuntimeZones.EntityKind + ":0:0:1", 1, 1);
            var kernel = Kernel(_ => zone, out var subject);
            var answer = kernel.ZoneOfEntity(subject);
            Check(answer.Answered && answer.Zone == zone && answer.Code == EntityZoneResolution.InZoneCode,
                "a placed entity answers its zone [" + answer.Code + "]");
            kernel.StopRuntime();
        }

        // A responder that answers null has answered that the entity stands in no zone of this world. That is an
        // answer and not a refusal, which is the whole point of the seam: a reader can tell it from "nobody could
        // read this" without guessing.
        {
            var kernel = Kernel(_ => null, out var subject);
            var answer = kernel.ZoneOfEntity(subject);
            Check(answer.Answered && answer.Zone == null && answer.Code == EntityZoneResolution.OutsideCode,
                "an entity in no zone is answered, not refused [" + answer.Code + "]");
            kernel.StopRuntime();
        }

        // A provider that cannot place an entity it owns refuses in its own words: the code it threw travels, so
        // "not current", "no course node" and "collected mid-read" stay tellable apart from the answer above.
        {
            var kernel = Kernel(_ => throw new RuntimeContractException("example.zone-cannot-place", "not current"), out var subject);
            var answer = kernel.ZoneOfEntity(subject);
            Check(!answer.Answered && answer.Zone == null && answer.Code == "example.zone-cannot-place",
                "a provider's own refusal keeps its code [" + answer.Code + "]");
            kernel.StopRuntime();
        }

        // A responder that failed without naming a reason is the kernel's own failure code, and it is not the
        // outside answer either.
        {
            var kernel = Kernel(_ => throw new InvalidOperationException("fixture native failure"), out var subject);
            var answer = kernel.ZoneOfEntity(subject);
            Check(!answer.Answered && answer.Code == EntityZoneResolution.FailedCode,
                "a failing responder is refused by name [" + answer.Code + "]");
            kernel.StopRuntime();
        }

        // A kind whose owner registered no responder is unavailable: no answer about the entity was given at all.
        {
            var kernel = Kernel(null, out var subject);
            var answer = kernel.ZoneOfEntity(subject);
            Check(!answer.Answered && answer.Code == EntityZoneResolution.UnavailableCode,
                "a kind with no responder is unavailable [" + answer.Code + "]");
            kernel.StopRuntime();
        }

        // A responder that answers a reference which is not a zone is refused rather than handed back: a
        // reference of another kind would route a later read to the wrong owner.
        {
            var kernel = Kernel(_ => new EntityReference(Kind + ":2", 1, 1), out var subject);
            var answer = kernel.ZoneOfEntity(subject);
            Check(!answer.Answered && answer.Code == EntityZoneResolution.KindCode,
                "a non-zone answer is refused [" + answer.Code + "]");
            kernel.StopRuntime();
        }

        // A reference the owning resolver no longer knows is refused before any responder is asked, so a stale
        // entity never reaches a provider's table.
        {
            var kernel = Kernel(_ => new EntityReference(RuntimeZones.EntityKind + ":0:0:1", 1, 1), out var subject);
            var answer = kernel.ZoneOfEntity(subject with { WorldEpoch = 9 });
            Check(!answer.Answered && answer.Code == "stale-world", "a reference from another world is refused");
            kernel.StopRuntime();
        }

        return checks;
    }

    /// <summary>One provider of the kind the cases read, with no capability of its own: the zone responder is a
    /// registration fact like any other and needs no row.</summary>
    private static string Registry()
        => RuntimeJson.From(new
        {
            providers = new[] { new { id = "example.zone", kind = "extension", version = "1.0.0", dependencies = NoPermissions } },
            capabilities = Array.Empty<object>(),
            bindings = Array.Empty<object>()
        }).GetRawText();
}
