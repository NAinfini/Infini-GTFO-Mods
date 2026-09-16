using System;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// The suite's entry point. It runs every check group against the API doubles and reports a count; a failure is an
/// exit code, so a build script can gate on it.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var suite = new Suite();
        WireChecks.Run(suite);
        HandshakeChecks.Run(suite);
        HandshakeFlowChecks.Run(suite);
        DedupChecks.Run(suite);
        EpochChecks.Run(suite);
        TransportChecks.Run(suite);
        BindingChecks.Run(suite);
        PresentationChecks.Run(suite);
        OwnerChecks.Run(suite);
        return suite.Report();
    }
}
