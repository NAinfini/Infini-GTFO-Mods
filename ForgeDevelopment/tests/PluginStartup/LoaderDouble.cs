using System.Reflection;

// Test-only model of the shipped loader's dependency gate. It reads the real [BepInPlugin] and
// [BepInDependency] metadata from the compiled production plugin, so the audit follows the shipped
// attributes instead of a copy of them. Two rules decide whether Load is ever called: a missing or
// unsatisfied hard dependency stops the plugin, a soft dependency never does. Assembly scanning,
// config binding and patching stay the host's job and are not modelled here.
internal sealed record LoadDecision(bool Loaded, string Reason)
{
    internal static LoadDecision Load() => new(true, "all hard dependencies are satisfied");
    internal static LoadDecision Skip(string reason) => new(false, reason);
}

internal sealed record DeclaredDependency(string Guid, string? Version, bool Soft)
{
    internal static DeclaredDependency[] Of(Type plugin) => plugin
        .GetCustomAttributes<BepInEx.BepInDependency>(inherit: false)
        .Select(attribute => new DeclaredDependency(attribute.GUID, attribute.Version,
            (attribute.Flags & BepInEx.BepInDependency.DependencyFlags.SoftDependency) != 0))
        .ToArray();
}

internal static class ChainloaderDouble
{
    internal static string IdentityOf(Type plugin) =>
        plugin.GetCustomAttribute<BepInEx.BepInPlugin>(inherit: false)!.GUID;

    // The loader only considers plugin assemblies that are actually installed; an absent assembly
    // contributes no plugin at all, so it is modelled by an empty plugin list.
    internal static IReadOnlyList<(string Guid, LoadDecision Decision)> Consider(
        IEnumerable<Type> plugins, IReadOnlyDictionary<string, string> installed) =>
        plugins.Select(plugin => (IdentityOf(plugin), Decide(plugin, installed))).ToArray();

    internal static LoadDecision Decide(Type plugin, IReadOnlyDictionary<string, string> installed)
    {
        foreach (var dependency in DeclaredDependency.Of(plugin))
        {
            if (dependency.Soft) continue;
            if (!installed.TryGetValue(dependency.Guid, out var version))
                return LoadDecision.Skip("hard dependency " + dependency.Guid + " is not installed");
            if (dependency.Version != null && !Satisfies(dependency.Version, version))
                return LoadDecision.Skip("hard dependency " + dependency.Guid + " " + version +
                    " does not satisfy the declared requirement " + dependency.Version);
        }
        return LoadDecision.Load();
    }

    // Only the requirement forms the shipped plugin declares are modelled. An unrecognised form
    // fails loudly instead of being treated as satisfied.
    internal static bool Satisfies(string requirement, string installed)
    {
        var floor = Version.Parse(installed);
        var text = requirement.Trim();
        if (text.StartsWith(">=", StringComparison.Ordinal)) return floor >= Version.Parse(text[2..]);
        if (text.StartsWith("<=", StringComparison.Ordinal)) return floor <= Version.Parse(text[2..]);
        if (text.StartsWith(">", StringComparison.Ordinal)) return floor > Version.Parse(text[1..]);
        if (text.StartsWith("<", StringComparison.Ordinal)) return floor < Version.Parse(text[1..]);
        if (text.Contains('*', StringComparison.Ordinal) || text.Contains(' ', StringComparison.Ordinal))
            throw new NotSupportedException("The loader double does not model the version range '" + requirement + "'.");
        return floor == Version.Parse(text);
    }
}
