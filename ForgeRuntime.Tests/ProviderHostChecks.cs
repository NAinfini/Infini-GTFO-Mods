using Infini.ForgeRuntime;

internal static class ProviderHostChecks
{
    public static void Run()
    {
        var host = new ForgeRuntimeHost(() => true);
        host.RegisterProvider("adapter.eec", ForgeProviderKind.Adapter);
        host.RegisterProvider("portalmod", ForgeProviderKind.Extension);

        var canonicalSeen = 0;
        using var canonical = host.ObserveTrigger("adapter.eec", "forge.trigger.door.opened", _ => canonicalSeen++);
        var first = host.ReportTrigger("adapter.eec", "forge.trigger.door.opened", "door-42-open-1", "door:42", 1);
        Equal(ForgeDispatchStatus.Accepted, first.Status);
        Equal(1, canonicalSeen);
        Equal(ForgeDispatchStatus.Duplicate,
            host.ReportTrigger("adapter.eec", "forge.trigger.door.opened", "door-42-open-1", "door:42", 2).Status);

        host.RegisterExtensionTrigger("portalmod", "portalmod.trigger.entered");
        var portalSeen = false;
        using var portal = host.ObserveTrigger("adapter.eec", "portalmod.trigger.entered", _ => portalSeen = true);
        Equal(ForgeDispatchStatus.Accepted,
            host.ReportTrigger("portalmod", "portalmod.trigger.entered", "portal-A-enter-7", "portal:A", 7).Status);
        True(portalSeen);

        Throws<InvalidOperationException>(() =>
            host.ReportTrigger("adapter.eec", "portalmod.trigger.entered", "steal", "portal:A", 8));
        Throws<InvalidOperationException>(() =>
            host.RegisterExtensionTrigger("adapter.eec", "adapter.eec.trigger.custom"));
        Throws<InvalidOperationException>(() =>
            host.RegisterExtensionTrigger("portalmod", "forge.trigger.door.opened"));
        Throws<ArgumentException>(() =>
            host.RegisterExtensionTrigger("portalmod", "other.trigger.entered"));
        Throws<InvalidOperationException>(() =>
            host.RegisterAction("adapter.eec", "forge.action.enemy.spawn", (_, _) => { }));

        host.RegisterAction("portalmod", "portalmod.action.create", (_, _) => { });
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected {expected}, got {actual}");
    }

    private static void True(bool value)
    {
        if (!value) throw new Exception("expected true");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"expected {typeof(T).Name}");
    }
}
