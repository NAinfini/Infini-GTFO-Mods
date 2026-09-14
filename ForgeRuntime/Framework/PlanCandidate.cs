namespace ForgeRuntime.Framework;

/// <summary>One discovered `.plan.json` file (I-PACK D-009), already read or already rejected by the host's filesystem
/// scan. The kernel treats the content as untrusted input regardless of origin; it never touches a filesystem itself, so
/// this is the only way work reaches <see cref="RuntimeKernel.LoadPlans"/>.</summary>
public readonly struct PlanCandidate
{
    /// <summary>The BepInEx-relative path, `/` separated. Carried on every plan.loaded/plan.rejected record for this file.</summary>
    public string Path { get; }
    internal string? Json { get; }
    internal string? RejectedCode { get; }
    internal string? RejectedDetail { get; }

    private PlanCandidate(string path, string? json, string? rejectedCode, string? rejectedDetail)
    { Path = path; Json = json; RejectedCode = rejectedCode; RejectedDetail = rejectedDetail; }

    /// <summary>A file the host read successfully; the kernel still validates its content from scratch.</summary>
    public static PlanCandidate Loaded(string path, string json)
    {
        RuntimeJson.Text(path);
        RuntimeJson.Require(json != null, "invalid-json", "Plan content cannot be null.");
        return new PlanCandidate(path, json, null, null);
    }

    /// <summary>A file the host's own scan already rejected (oversized, linked/escaping path, or discovery budget) before
    /// reading its content. <paramref name="code"/> is the reason recorded in the plan.rejected log line.</summary>
    public static PlanCandidate Rejected(string path, string code, string detail)
    {
        RuntimeJson.Text(path); RuntimeJson.Text(code); RuntimeJson.Text(detail);
        return new PlanCandidate(path, null, code, detail);
    }

    internal bool IsHostRejected => Json == null;
}

/// <summary>Per-file result of <see cref="RuntimeKernel.LoadPlans"/>. The kernel has already written the matching
/// plan.loaded/plan.rejected record (when a log sink is configured) by the time this is returned; callers do not need it
/// to observe the log, only to inspect or test the outcome directly.</summary>
public readonly record struct PlanLoadOutcome(string Path, bool Loaded, string? PlanId, string? Code, string? Detail);
