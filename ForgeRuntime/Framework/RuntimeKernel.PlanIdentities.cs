using System;
using System.Collections.Generic;

namespace ForgeRuntime.Framework;

/// <summary>
/// One loaded plan's identity, as a peer compares it: the planId, the resource it was compiled from and the binding
/// pins it closes over. Plan files are never serialized for this; identity is what tells two peers apart.
/// </summary>
internal readonly record struct RuntimePlanIdentity(string PlanId, string ResourceId, string ResourceRevision, string BindingPins);

/// <summary>
/// The loaded plan set, in a form the host's network boundary can map onto its own plan identity. Read-only by
/// construction: the plan table belongs to the kernel and this exposes a snapshot of it, never a view another
/// assembly could write through.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>
    /// The plans loaded now, ordered ordinally by planId with their binding pins in ordinal order, so two processes
    /// with the same plan set produce the same summary. An empty list means "nothing loaded yet", which is exactly
    /// what a handshake cannot compare.
    /// </summary>
    internal IReadOnlyList<RuntimePlanIdentity> PlanIdentities
    {
        get
        {
            var identities = new List<RuntimePlanIdentity>(plans.Count);
            foreach (var loaded in plans.Values)
                identities.Add(new RuntimePlanIdentity(loaded.Plan.Id, loaded.Plan.ResourceId, loaded.Plan.ResourceRevision, Pins(loaded.Plan.Bindings)));
            identities.Sort((left, right) => string.CompareOrdinal(left.PlanId, right.PlanId));
            return identities.AsReadOnly();
        }
    }

    /// <summary>The pin closure as one comparison text. The order is this side's, not the hash set's: a plan's pins
    /// reach the wire as text, so the text has to be the same on both sides for the same closure.</summary>
    private static string Pins(IReadOnlySet<string> bindings)
    {
        var sorted = new List<string>(bindings);
        sorted.Sort(StringComparer.Ordinal);
        return string.Join(",", sorted);
    }
}
