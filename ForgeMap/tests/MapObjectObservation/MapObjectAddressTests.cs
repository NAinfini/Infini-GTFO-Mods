using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.MapObjects;

/// <summary>Address format: what an address is built from, what it round-trips to, and what it refuses.</summary>
public sealed class MapObjectAddressTests
{
    [Fact]
    public void DoorAddressRoundTripsThroughItsOwnText()
    {
        // Layer 2 is the native `LG_LayerType` ordinal (MainLayer, SecondaryLayer, ThirdLayer) and zone 7 is
        // `LG_Zone.LocalIndex`: the coordinates of the zone the door is the entrance gate of.
        var address = Assert.IsType<MapObjectReference>(MapObjectDoorAddress.Create(1, 2, 7));
        Assert.Equal("door/1/2/7/security", address.ToString());
        Assert.Equal(address, MapObjectDoorAddress.TryParse(address.ToString()));
        Assert.Equal("door", address.Category);
    }

    [Fact]
    public void TerminalAddressRoundTripsThroughItsOwnText()
    {
        // The key is the terminal's own placement index inside its zone, so a zone with several terminals has
        // one address per placement and no id the level builder hands out.
        var address = Assert.IsType<MapObjectReference>(MapObjectTerminalAddress.Create(1, 1, 2, 1));
        Assert.Equal("terminal/1/1/2/1", address.ToString());
        Assert.Equal(address, MapObjectTerminalAddress.TryParse(address.ToString()));
        Assert.Equal("terminal", address.Category);
    }

    [Fact]
    public void TheFirstPlacementIsAddressZero()
    {
        var first = Assert.IsType<MapObjectReference>(MapObjectTerminalAddress.Create(0, 0, 3, 0));
        var second = Assert.IsType<MapObjectReference>(MapObjectTerminalAddress.Create(0, 0, 3, 1));
        Assert.Equal("terminal/0/0/3/0", first.ToString());
        Assert.Equal("terminal/0/0/3/1", second.ToString());
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TwoDoorsOfOneParentZoneDifferByTheZoneEachOneGuards()
    {
        // The address names the guarded zone, not the parent both doors hang under, so two entrance gates of
        // one parent zone are two addresses.
        Assert.NotEqual(MapObjectDoorAddress.Create(0, 0, 4), MapObjectDoorAddress.Create(0, 0, 5));
    }

    [Fact]
    public void ACategoryThatTheAddressDoesNotBelongToIsRefused()
    {
        // The same text parsed as a terminal is not a door address, whatever its segments look like.
        Assert.Null(MapObjectTerminalAddress.TryParse(MapObjectDoorAddress.Create(0, 0, 4)!.ToString()));
        Assert.Null(MapObjectDoorAddress.TryParse(MapObjectTerminalAddress.Create(0, 0, 4, 5)!.ToString()));
    }

    [Fact]
    public void ACoordinateThatWasNotReadNeverBecomesAnAddress()
    {
        // A zone coordinate that did not read leaves the object unaddressed rather than addressed as zero: an
        // address that exists always names a real zone.
        Assert.Null(MapObjectDoorAddress.Create(0, 0, null));
        Assert.Null(MapObjectDoorAddress.Create(null, 0, 4));
        Assert.Null(MapObjectDoorAddress.Create(0, null, 4));
        Assert.Null(MapObjectTerminalAddress.Create(0, 0, 4, null));
        Assert.Null(MapObjectTerminalAddress.Create(0, 0, null, 5));
        Assert.Null(MapObjectTerminalAddress.Create(null, 0, 4, 5));
        Assert.Null(MapObjectTerminalAddress.Create(0, null, 4, 5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("door")]
    [InlineData("door/0/0/4")]
    [InlineData("door/0/0/4/5/6")]
    [InlineData("door/0/0/4/")]
    [InlineData("door/0/0/4/4242")]
    [InlineData("terminal/0/0/4/5")]
    [InlineData("Door/0/0/4/security")]
    [InlineData("door/00/0/4/security")]
    [InlineData("door/0/0/04/security")]
    [InlineData("door/0/0/4/security/")]
    [InlineData("door/?/?/?/security")]
    [InlineData("door/0/0/-4/security")]
    [InlineData("door/0/0/ 4/security")]
    [InlineData("door/0/0/4/security ")]
    [InlineData("door//0/4/security")]
    public void AForeignOrMalformedAddressIsRefused(string text)
        => Assert.Null(MapObjectDoorAddress.TryParse(text));

    /// <summary>The shared vector: the website's authoring-time address and this reader's native reading must
    /// come out as one spelling. A readable row is read through the category's own factory and parser — the same
    /// calls the game-bound readers make — and a refused row must be one this reader would not name, either
    /// because its native members did not read or because the text is not an address this grammar accepts.</summary>
    [Fact]
    public void TheSharedVectorFileAgreesWithThisReader()
    {
        string path = Environment.GetEnvironmentVariable("FORGE_MAP_ADDRESS_VECTORS") ?? "";
        Assert.False(string.IsNullOrWhiteSpace(path),
            "Set FORGE_MAP_ADDRESS_VECTORS to the website's Tests/Viewer/fixtures/map-object-address.json: the "
            + "two repositories prove they spell one address the same way by rendering and parsing the same rows. "
            + "This test fails rather than skipping, because a missing vector would otherwise look like agreement.");
        Assert.True(File.Exists(path), "FORGE_MAP_ADDRESS_VECTORS does not name a readable file: " + path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rows = document.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        Assert.NotEmpty(rows);
        int readable = 0, refused = 0, noCoordinates = 0, levelContext = 0;
        foreach (var row in rows)
        {
            var site = row.GetProperty("website");
            string category = site.GetProperty("category").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("note").GetString()), "A vector row carries no note.");
            string? rendered = Factory(row, category)?.ToString();
            if (row.TryGetProperty("expectedRefusal", out var code))
            {
                Assert.False(string.IsNullOrWhiteSpace(code.GetString()), "A refused row carries no refusal code.");
                // A refused row is one of two things to this reader. Either the row's native members did not
                // read, so there is no address to name at all, or the coordinates do read and the reader
                // refuses them against the level — the start zone has no entrance gate, a reactor or objective
                // terminal is placed after the author's own terminals. The vector cannot carry a level, so a row
                // of the second kind only has to be an address this grammar can name: the reader's own refusal
                // of it is asserted where the level exists, in the native adapter suite. A generator group is a
                // third case: this reader names the group the level built, while the website refuses it at
                // authoring time because an author writes a zone's cluster count and never a group's serial.
                if (rendered == null) noCoordinates++; else levelContext++;
                refused++;
                continue;
            }
            string expected = row.GetProperty("expected").GetString()!;
            Assert.Equal(expected, rendered);
            // The same text has to come back as an address of its own category, or a plan could be written
            // from an address no reader ever produces.
            Assert.Equal(expected, Parse(category, expected)?.ToString());
            Assert.Null(Parse(Other(category), expected));
            readable++;
        }
        Assert.True(readable > 0 && refused > 0 && noCoordinates > 0 && levelContext > 0,
            "The vector must carry readable rows, refused rows whose coordinates did not read and refused rows "
            + "the level refuses: readable=" + readable + " refused=" + refused + " noCoordinates=" + noCoordinates
            + " levelContext=" + levelContext);
    }

    /// <summary>The address a category reads a text as, through that category's own parser: the same call the
    /// game-bound reader makes, so a row is decided by the production grammar rather than by this test.</summary>
    private static MapObjectReference? Parse(string category, string text)
        => category == MapObjectCategories.Door ? MapObjectDoorAddress.TryParse(text)
            : category == MapObjectCategories.Terminal ? MapObjectTerminalAddress.TryParse(text)
            : category == MapObjectGeneratorAddress.Category ? MapObjectGeneratorAddress.TryParse(text) : null;

    /// <summary>The address one vector row's `native` reading names, built through the category's own factory:
    /// the same call the game-bound reader makes. Only the roles this reader addresses are built — a door that is a
    /// zone's `security` entrance, a terminal at a placement index, a generator at the level's serial for it (and a
    /// group under its own key form) — and a section that carries no coordinate stands for the field the game did not
    /// read, which no factory turns into an address.</summary>
    private static MapObjectReference? Factory(JsonElement row, string category)
        => Native(row) is { } native && native.TryGetProperty("role", out var role)
            && Addressed(category, role.GetString())
                ? Build(native, category) : null;

    /// <summary>Whether this reader addresses the object a row's native role names. The roles are the ones the
    /// game-bound readers report, so a row whose role belongs to another category is not an address this reader
    /// produces — which is what makes a foreign row's refusal meaningful rather than incidental.</summary>
    private static bool Addressed(string category, string? role)
        => category == MapObjectCategories.Door ? role == "security"
            : category == MapObjectCategories.Terminal ? role == "terminal"
            : category == MapObjectGeneratorAddress.Category ? role is "generator" or "generator-group"
            : false;

    /// <summary>The row's native reading, or null when the row carries none.</summary>
    private static JsonElement? Native(JsonElement row)
        => row.TryGetProperty("native", out var native) && native.ValueKind == JsonValueKind.Object
            ? native : null;

    private static MapObjectReference? Build(JsonElement section, string category)
    {
        if (category == MapObjectCategories.Door)
        {
            var guard = section.GetProperty("guard");
            return MapObjectDoorAddress.Create(Number(guard, "dimension"), Number(guard, "layer"),
                Number(guard, "zoneLocalIndex"));
        }
        if (category == MapObjectCategories.Terminal)
        {
            var zone = section.GetProperty("zone");
            return MapObjectTerminalAddress.Create(Number(zone, "dimension"), Number(zone, "layer"),
                Number(zone, "zoneLocalIndex"), Number(section, "placementIndex"));
        }
        if (category == MapObjectGeneratorAddress.Category)
        {
            var generator = section.GetProperty("generator");
            int? dimension = Number(generator, "dimension"), layer = Number(generator, "layer"),
                zone = Number(generator, "zoneLocalIndex"), serial = Number(generator, "serial");
            return section.GetProperty("role").GetString() == "generator-group"
                ? MapObjectGeneratorAddress.CreateGroup(dimension, layer, zone, serial)
                : MapObjectGeneratorAddress.Create(dimension, layer, zone, serial);
        }
        return null;
    }

    /// <summary>A coordinate the row carries, or null when it is null or absent: the vector writes an unread
    /// coordinate as `null` rather than omitting it, and both mean the same thing to a factory.</summary>
    private static int? Number(JsonElement section, string name)
        => section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;

    private static string Other(string category)
        => category == MapObjectCategories.Door ? MapObjectCategories.Terminal : MapObjectCategories.Door;

    [Theory]
    [InlineData(0, "none", null)]
    [InlineData(1, "closed", null)]
    [InlineData(3, "closed_locked_with_key_item", null)]
    [InlineData(8, "chained_puzzle_activated", MapObjectPhases.Requested)]
    [InlineData(10, "open", MapObjectPhases.Completed)]
    [InlineData(11, "destroyed", MapObjectPhases.Completed)]
    [InlineData(16, "opening", MapObjectPhases.Started)]
    public void DoorStatusNamesAndPhasesComeFromTheNativeEnum(int status, string name, int? phase)
    {
        Assert.Equal(name, MapObjectDoorStatus.Name(status));
        Assert.Equal(phase, MapObjectDoorStatus.Phase(status));
    }

    /// <summary>An enum port carries the member index of its declared set, so the two index vocabularies this
    /// provider publishes have to be the framework's own sets in the framework's own order.</summary>
    [Fact]
    public void EnumIndexVocabulariesMatchTheFrameworkSets()
    {
        Assert.Equal(new[] { "requested", "started", "completed", "cancelled", "failed" }, Members("interaction_phase"));
        Assert.Equal(new[] { "succeeded", "partial", "rejected", "failed", "cancelled", "expired" }, Members("execution_outcome"));
        Assert.Equal("started", MapObjectPhases.Name(MapObjectPhases.Started));
        Assert.Equal("failed", MapObjectOutcomes.Name(MapObjectOutcomes.Failed));
    }

    private static string[] Members(string set)
    {
        var table = (Dictionary<string, string[]>)typeof(ForgeRuntime.Framework.RuntimeKernel).Assembly
            .GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!
            .GetField("EnumSets", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null)!;
        return table[set];
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(15, true)]
    [InlineData(1, false)]
    [InlineData(10, false)]
    public void OnlyTheLockedNativeStatusesReadAsLocked(int status, bool locked)
        => Assert.Equal(locked, MapObjectDoorStatus.IsLocked(status));

    [Theory]
    [InlineData(3, "data_mining", false, MapObjectOutcomes.Succeeded)]
    [InlineData(4, "hacked", false, MapObjectOutcomes.Succeeded)]
    [InlineData(7, "reactor_error", false, MapObjectOutcomes.Failed)]
    [InlineData(10, "audio_loop_error", false, MapObjectOutcomes.Failed)]
    [InlineData(2, "player_interacting", true, null)]
    [InlineData(5, "code_puzzle", false, null)]
    [InlineData(1, "awake", false, null)]
    [InlineData(9, "do_play_audio_file", false, null)]
    public void TerminalStateNamesOutcomesAndSessionsComeFromTheNativeEnum(int status, string name, bool active, int? outcome)
    {
        Assert.Equal(name, MapObjectTerminalState.Name(status));
        Assert.Equal(active, MapObjectTerminalState.SessionActive(status));
        Assert.Equal(outcome, MapObjectTerminalState.Outcome(status));
    }

    [Theory]
    [InlineData(1, "help")]
    [InlineData(11, "download_data")]
    [InlineData(14, "disable_alarm")]
    [InlineData(43, "info")]
    public void TerminalCommandsArePublishedUnderTheEnumName(int command, string name)
        => Assert.Equal(name, MapObjectTerminalCommand.Name(command));

    [Fact]
    public void AnUnknownNativeValueIsRefusedInsteadOfGuessed()
    {
        Assert.Throws<RuntimeContractException>(() => MapObjectDoorStatus.Name(200));
        Assert.Throws<RuntimeContractException>(() => MapObjectTerminalState.Name(200));
        Assert.Throws<RuntimeContractException>(() => MapObjectTerminalCommand.Name(200));
    }
}
