using System.Text;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.LoadoutPolicy;

/// <summary>The policy loader: discovery, the strict canonical byte contract, the three pins and the rundown
/// conflict rule. Every case runs against a temp install, so no profile and no game is read.</summary>
public sealed class LoadoutPolicyTests
{
    /// <summary>The code of one diagnostic line, `weapon.&lt;code&gt; file=&lt;relative&gt;: reason`.</summary>
    private static string Code(string diagnostic)
    {
        const string prefix = "weapon.";
        int end = diagnostic.IndexOf(' ', prefix.Length);
        return end < 0 ? diagnostic[prefix.Length..] : diagnostic[prefix.Length..end];
    }

    private static string Describe(IReadOnlyList<string> lines) => lines.Count == 0 ? "<none>" : string.Join(" | ", lines);

    private static void RequireRejected(IReadOnlyList<string> rejected, string code, string label)
    {
        Assert.True(rejected.Count == 1, label + ": expected exactly one rejection, got " + Describe(rejected));
        Assert.Equal(code, Code(rejected[0]));
    }

    private static string CodeOf(IReadOnlyList<string> rejected, string package)
    {
        foreach (var line in rejected)
            if (line.Contains("file=plugins/" + package + "/forge/loadout.json:", StringComparison.Ordinal)) return Code(line);
        return "<no diagnostic for " + package + ">";
    }

    /// <summary>The whole byte format, hand-written: one document the loader has to accept exactly as it stands,
    /// with no help from the fixture's own canonical writer.</summary>
    [Fact]
    public void a_hand_written_canonical_policy_is_accepted()
    {
        using var install = new PolicyInstall();
        var golden = "{\"format\":\"gtfo-forge-loadout\",\"projectId\":\"TestProject\",\"rundownId\":12345,"
            + "\"gameAssemblySha256\":\"" + install.GameSha + "\","
            + "\"plugins\":[{\"guid\":\"NAinfini.ForgeMap\",\"sha256\":\"" + install.MapSha + "\"},"
            + "{\"guid\":\"NAinfini.ForgeRuntime\",\"sha256\":\"" + install.RuntimeSha + "\"}],"
            + "\"sources\":[{\"path\":\"plugins/Pack/gear.json\",\"sha256\":\"" + install.GearSha + "\"}],"
            + "\"slots\":{\"GearStandard\":[" + PolicyInstall.Entry(10001) + "],"
            + "\"GearSpecial\":[" + PolicyInstall.Entry(10002) + "],"
            + "\"GearClass\":[" + PolicyInstall.Entry(10003) + "]}}\n";
        install.WritePolicy("Pack", golden);

        var snapshot = install.LoadWritten(out var rejected);

        Assert.Empty(rejected);
        Assert.Equal(1, snapshot.PolicyCount);
        Assert.True(snapshot.TryGet(PolicyInstall.Rundown, out var policy), "The accepted policy was not keyed by its rundown.");
        Assert.Equal(PolicyInstall.Project, policy.ProjectId);
        Assert.Equal(PolicyInstall.Rundown, policy.RundownId);
        Assert.Equal(install.GameSha, policy.GameAssemblySha256);
        Assert.Equal(new[] { 10001u }, policy.Slot(LoadoutSlot.GearStandard).OfflineGearIds);
        Assert.Equal(new[] { 10002u }, policy.Slot(LoadoutSlot.GearSpecial).OfflineGearIds);
        Assert.Equal(new[] { 10003u }, policy.Slot(LoadoutSlot.GearClass).OfflineGearIds);
        Assert.Equal(2, policy.Plugins.Length);
        Assert.Single(policy.Sources);
        Assert.False(snapshot.TryGet(999, out _), "A rundown the install never pinned answered with a policy.");
    }

    /// <summary>Every other spelling of the same document: the loader re-serializes what it read and compares the
    /// bytes, so whitespace, another field order or another list order is a different file and is refused.</summary>
    [Fact]
    public void every_other_spelling_of_one_document_is_refused()
    {
        using var install = new PolicyInstall();
        var canonical = install.Canonical();
        var map = "{\"guid\":\"NAinfini.ForgeMap\",\"sha256\":\"" + install.MapSha + "\"}";
        var runtime = "{\"guid\":\"NAinfini.ForgeRuntime\",\"sha256\":\"" + install.RuntimeSha + "\"}";
        var variants = new (string Label, string Text)[]
        {
            ("an indented document", canonical.Replace("{\"format\"", "{\n  \"format\"")),
            ("a space after a colon", canonical.Replace("\"format\":", "\"format\": ")),
            ("no trailing newline", canonical[..^1]),
            ("a second trailing newline", canonical + "\n"),
            ("another root field order", canonical.Replace("\"projectId\":\"TestProject\",\"rundownId\":12345",
                "\"rundownId\":12345,\"projectId\":\"TestProject\"")),
            ("another plugin order", canonical.Replace(map + "," + runtime, runtime + "," + map)),
            ("another slot entry order", canonical.Replace(PolicyInstall.Entry(10001) + "," + PolicyInstall.Entry(10002),
                PolicyInstall.Entry(10002) + "," + PolicyInstall.Entry(10001)))
        };
        foreach (var (label, text) in variants)
        {
            Assert.NotEqual(canonical, text);
            install.WritePolicy("Pack", text);
            var snapshot = install.LoadWritten(out var rejected);
            Assert.True(snapshot.PolicyCount == 0 && rejected.Count == 1 && Code(rejected[0]) == "loadout-policy-canonical",
                label + ": expected one canonical rejection, got " + Describe(rejected));
        }
    }

    /// <summary>A number the contract writes as its own text. `12345.0` is another JSON spelling of the same value,
    /// so it is refused rather than normalized; which check names it is this loader's own business.</summary>
    [Fact]
    public void a_number_spelled_other_than_its_own_text_is_refused()
    {
        using var install = new PolicyInstall();
        install.WritePolicy("Pack", install.Canonical().Replace("\"rundownId\":12345", "\"rundownId\":12345.0"));
        var snapshot = install.LoadWritten(out var rejected);
        Assert.Equal(0, snapshot.PolicyCount);
        Assert.True(rejected.Count == 1 && rejected[0].StartsWith("weapon.loadout-policy-", StringComparison.Ordinal),
            "A decimal spelling of a rundown id was not refused: " + Describe(rejected));
    }

    [Fact]
    public void a_bom_and_invalid_utf8_are_refused()
    {
        using var install = new PolicyInstall();
        install.WritePolicy("Pack", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(install.Canonical())).ToArray());
        var snapshot = install.LoadWritten(out var rejected);
        Assert.Equal(0, snapshot.PolicyCount);
        RequireRejected(rejected, "loadout-policy-bom", "a BOM");

        install.WritePolicy("Pack", new byte[] { 0x7B, 0x22, 0xFF, 0x22, 0x7D });
        snapshot = install.LoadWritten(out rejected);
        Assert.Equal(0, snapshot.PolicyCount);
        RequireRejected(rejected, "loadout-policy-utf8", "invalid UTF-8");
    }

    [Fact]
    public void a_policy_over_one_mebibyte_is_refused()
    {
        using var install = new PolicyInstall();
        install.WritePolicy("Pack", install.Canonical().PadRight(LoadoutPolicyData.MaximumFileBytes + 1));
        var snapshot = install.LoadWritten(out var rejected);
        Assert.Equal(0, snapshot.PolicyCount);
        RequireRejected(rejected, "loadout-policy-size", "an oversized policy");
    }

    /// <summary>Every shape the contract refuses, each one field away from an accepted document. The variants the
    /// fixture's own writer can spell are built through it, so the shape rule is what fails and not the bytes.</summary>
    [Fact]
    public void every_shape_the_contract_refuses_is_refused()
    {
        using var install = new PolicyInstall();
        var canonical = install.Canonical();
        var cases = new List<(string Label, string Text)>
        {
            ("one pinned plugin", install.Canonical(plugins: new[] { install.PinnedPlugins[0] })),
            ("no pinned source", install.Canonical(sources: Array.Empty<(string Path, string Sha)>())),
            ("an empty slot", install.Canonical(gearClass: Array.Empty<uint>())),
            ("a repeated offlineGearId", install.Canonical(special: new uint[] { 10001, 10005, 10006, 10007, 10008 })),
            ("more than 256 slot entries", install.Canonical(standard: Enumerable.Range(1, 257).Select(value => (uint)value).ToArray())),
            ("an uppercase game digest", install.Canonical(gameSha: install.GameSha.ToUpperInvariant())),
            ("a repeated plugin GUID in another case", install.Canonical(plugins: new[]
                { install.PinnedPlugins[0], ("nainfini.forgemap", install.MapSha), install.PinnedPlugins[1] })),
            ("a repeated source path in another case", install.Canonical(sources: new[]
                { install.PinnedSources[0], ("PLUGINS/PACK/GEAR.JSON", install.GearSha) })),
            ("another format", canonical.Replace("\"format\":\"gtfo-forge-loadout\"", "\"format\":\"gtfo-forge-loadout-2\"")),
            ("an empty projectId", canonical.Replace("\"projectId\":\"TestProject\"", "\"projectId\":\"\"")),
            ("a projectId with a dash", canonical.Replace("\"projectId\":\"TestProject\"", "\"projectId\":\"Test-Project\"")),
            ("rundownId zero", install.Canonical(rundownId: 0)),
            ("rundownId above a uint", canonical.Replace("\"rundownId\":12345", "\"rundownId\":4294967296")),
            ("a short game digest", canonical.Replace(install.GameSha, install.GameSha[..63])),
            ("a packet digest that is not a digest", canonical.Replace(PolicyInstall.Packet(10001), new string('z', 64))),
            ("an extra root field", canonical.Replace(",\"slots\":{", ",\"packet\":\"x\",\"slots\":{")),
            ("a repeated root field", canonical.Replace("\"projectId\":\"TestProject\"",
                "\"projectId\":\"TestProject\",\"projectId\":\"TestProject\"")),
            ("a missing slot", canonical.Replace("," + PolicyInstall.SlotJson("GearClass", PolicyInstall.ClassIds), "")),
            ("a slot entry without its packet", canonical.Replace(",\"packetSha256\":\"" + PolicyInstall.Packet(10001) + "\"", "")),
            ("a GUID with a space", canonical.Replace("\"guid\":\"NAinfini.ForgeMap\"", "\"guid\":\"NAinfini ForgeMap\"")),
            ("a source path that climbs out", canonical.Replace("\"path\":\"plugins/Pack/gear.json\"",
                "\"path\":\"plugins/Pack/../gear.json\"")),
            ("an absolute source path", canonical.Replace("\"path\":\"plugins/Pack/gear.json\"", "\"path\":\"/gear.json\"")),
            ("a reserved device segment", canonical.Replace("\"path\":\"plugins/Pack/gear.json\"", "\"path\":\"plugins/con/gear.json\"")),
            ("a segment ending in a dot", canonical.Replace("\"path\":\"plugins/Pack/gear.json\"", "\"path\":\"plugins/Pack./gear.json\""))
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (label, text) in cases)
        {
            Assert.True(seen.Add(label), "The case `" + label + "` is listed twice.");
            Assert.NotEqual(canonical, text);
            install.WritePolicy("Pack", text);
            var snapshot = install.LoadWritten(out var rejected);
            Assert.True(snapshot.PolicyCount == 0 && rejected.Count == 1 && Code(rejected[0]) == "loadout-policy-shape",
                label + ": expected one shape rejection, got " + Describe(rejected));
        }
    }

    /// <summary>One bad pin refuses its own file only: the file whose pins all match is still accepted, and each
    /// failure is named by its own code.</summary>
    [Fact]
    public void one_bad_pin_refuses_its_own_file_and_leaves_the_others_alone()
    {
        using var install = new PolicyInstall();
        install.WritePolicy("Good", install.Canonical(20001));
        install.WritePolicy("BadSource", install.Canonical(20002).Replace(install.GearSha, new string('a', 64)));
        install.WritePolicy("BadGame", install.Canonical(20003, gameSha: new string('b', 64)));
        install.WritePolicy("BadPlugin", install.Canonical(20004,
            plugins: new[] { install.PinnedPlugins[0], ("NAinfini.ForgeRuntime", new string('c', 64)) }));
        install.WritePolicy("MissingPlugin", install.Canonical(20005,
            plugins: new[] { install.PinnedPlugins[0], ("NAinfini.NotInstalled", install.RuntimeSha) }));

        var snapshot = install.LoadWritten(out var rejected);

        Assert.Equal(1, snapshot.PolicyCount);
        Assert.True(snapshot.TryGet(20001, out _), "The file whose three pins all matched was not the one accepted.");
        Assert.Equal(4, rejected.Count);
        Assert.Equal("loadout-policy-source-mismatch", CodeOf(rejected, "BadSource"));
        Assert.Equal("loadout-policy-gameassembly-mismatch", CodeOf(rejected, "BadGame"));
        Assert.Equal("loadout-policy-plugin-mismatch", CodeOf(rejected, "BadPlugin"));
        Assert.Equal("loadout-policy-plugin-missing", CodeOf(rejected, "MissingPlugin"));
    }

    /// <summary>A pin whose bytes cannot be read at all is refused the same way as a pin that does not match, one
    /// branch at a time: the injected source is the only thing that changes, and nothing else in the install does.</summary>
    [Fact]
    public void an_unreadable_pin_refuses_its_own_file()
    {
        using var install = new PolicyInstall();
        install.WritePolicy("Pack", install.Canonical());
        var gone = Path.Combine(install.Root, "gone.dll");
        var lines = new List<string>();

        var source = LoadoutPolicyData.Load(install.Root,
            new LoadoutPinSource(gone, _ => gone, _ => null), lines.Add);
        Assert.Equal(0, source.PolicyCount);
        RequireRejected(lines, "loadout-policy-source-missing", "a source that cannot be read");

        lines.Clear();
        var game = LoadoutPolicyData.Load(install.Root, new LoadoutPinSource(gone, _ => null,
            path => path == install.GearSourcePath ? File.ReadAllBytes(path) : null), lines.Add);
        Assert.Equal(0, game.PolicyCount);
        RequireRejected(lines, "loadout-policy-gameassembly-unreadable", "a game assembly that cannot be read");

        lines.Clear();
        var plugin = LoadoutPolicyData.Load(install.Root, new LoadoutPinSource(install.GameAssemblyPath, _ => gone,
            path => path == install.GearSourcePath || path == install.GameAssemblyPath ? File.ReadAllBytes(path) : null), lines.Add);
        Assert.Equal(0, plugin.PolicyCount);
        RequireRejected(lines, "loadout-policy-plugin-unreadable", "a plugin that cannot be read");
    }

    [Fact]
    public void a_rundown_claimed_twice_withdraws_every_claiming_file()
    {
        using var install = new PolicyInstall();
        install.WritePolicy("A", install.Canonical(20001));
        install.WritePolicy("B", install.Canonical(20001));
        install.WritePolicy("C", install.Canonical(20002));

        var snapshot = install.LoadWritten(out var rejected);

        Assert.Equal(1, snapshot.PolicyCount);
        Assert.True(snapshot.TryGet(20002, out _), "The uncontested rundown was not accepted.");
        Assert.False(snapshot.TryGet(20001, out _), "A contested rundown answered with one of its policies.");
        Assert.Equal(2, rejected.Count);
        Assert.All(rejected, line => Assert.Equal("loadout-policy-conflict", Code(line)));
        Assert.All(rejected, line => Assert.Contains("20001", line, StringComparison.Ordinal));
    }

    [Fact]
    public void discovery_is_one_package_level_and_never_recurses()
    {
        using var install = new PolicyInstall();

        var snapshot = install.LoadWritten(out var rejected);
        Assert.Equal(0, snapshot.PolicyCount);
        Assert.Empty(rejected);

        install.WritePolicy("Pack/nested", install.Canonical());
        snapshot = install.LoadWritten(out rejected);
        Assert.Equal(0, snapshot.PolicyCount);
        Assert.Empty(rejected);

        Directory.CreateDirectory(Path.Combine(install.Root, "plugins", "Pack", "forge", "loadout.json"));
        snapshot = install.LoadWritten(out rejected);
        Assert.Equal(0, snapshot.PolicyCount);
        RequireRejected(rejected, "loadout-policy-path", "a directory at the policy path");
    }

    [Fact]
    public void a_policy_reached_through_a_junction_is_refused_and_never_followed()
    {
        using var install = new PolicyInstall();
        var real = Path.GetDirectoryName(install.Write("real/loadout.json", install.Canonical()))!;
        var link = Path.Combine(install.Root, "plugins", "Pack", "forge");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        // A machine that refuses to create a junction has nothing to check here; the case is skipped, never failed.
        if (!PolicyInstall.TryJunction(link, real)) return;

        var snapshot = install.LoadWritten(out var rejected);

        Assert.Equal(0, snapshot.PolicyCount);
        RequireRejected(rejected, "loadout-policy-path", "a policy behind a junction");
    }

    [Fact]
    public void a_pinned_source_reached_through_a_junction_is_refused()
    {
        using var install = new PolicyInstall();
        var outside = Path.Combine(Path.GetTempPath(), "forge-loadout-outside-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(install.Root, "plugins", "Pack", "linked");
        try
        {
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "gear.json"), "gear-fixture");
            if (!PolicyInstall.TryJunction(link, outside)) return;
            install.WritePolicy("Pack", install.Canonical(sources: new[]
                { ("plugins/Pack/linked/gear.json", PolicyInstall.Digest(Encoding.UTF8.GetBytes("gear-fixture"))) }));

            var snapshot = install.LoadWritten(out var rejected);

            Assert.Equal(0, snapshot.PolicyCount);
            RequireRejected(rejected, "loadout-policy-source-path", "a pinned source behind a junction");
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link, false); }
            catch (IOException) { }
            try { Directory.Delete(outside, true); }
            catch (IOException) { }
        }
    }
}
