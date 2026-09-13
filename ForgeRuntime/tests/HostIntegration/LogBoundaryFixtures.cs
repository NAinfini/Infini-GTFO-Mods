using ForgeRuntime.Framework;

namespace SNetwork
{
    internal sealed class SNet_Player { internal static SNet_Player? Lookup(ulong id) => id == 0 ? null : new SNet_Player(); }
}

/// <summary>Negative and positive controls for <see cref="LogBoundaryProbe"/>; inspected as IL, never executed.</summary>
internal static class LogBoundaryFixtures
{
    internal static RuntimeLogRecord Concatenated(string provider) => new() { Code = "test." + provider, Provider = provider };
    internal static RuntimeLogRecord Interpolated(string provider, int tick) => new() { Code = $"test.{tick}", Provider = provider };
    internal static RuntimeLogRecord Formatted(string provider, int tick) => new() { Code = string.Format("test.{0}", tick), Provider = provider };
    internal static RuntimeLogRecord ThroughLocal(string provider)
    {
        var code = string.Concat("test.", provider);
        if (code.Length == 0) throw new ArgumentException(provider);
        return new() { Code = code, Provider = provider };
    }
    internal static RuntimeLogRecord Clean(string provider, string reason) => new() {
        Code = RuntimeLogCodes.LogDropped, Provider = provider,
        Plan = new RuntimeLogPlan { PlanId = provider, ResourceId = "resource", ResourceRevision = reason },
        Result = new RuntimeLogResult { Status = "failed", Commit = null, Reason = reason }
    };
    internal static object? ResolvesPlayer(ulong id) => SNetwork.SNet_Player.Lookup(id);
}
