using System.Text.Json;
using ForgeMap;

namespace ForgeMap.Tests;

/// <summary>The one authored wave-tuning field's grammar: it is a level field of the map data document, so what
/// these cases pin is that the field the website writes reads as the values the native half writes, and that
/// everything else is refused by name rather than half-applied. There is no `schemaVersion` and no `fixes`: the
/// field belongs to a level's own map data and the source mod's game-behaviour switches are system fixes, not
/// author data.</summary>
public sealed class WaveTuningDataTests
{
    private const string Minimal = """{ "maxHeat": 3 }""";

    private static WaveTuningData Read(string json)
    {
        Assert.True(WaveTuningData.TryRead(Field(json), out var data, out var code, out var reason),
            "the field was refused: " + code + ": " + reason);
        return data!;
    }

    private static (string Code, string Reason) Refuse(string json)
    {
        Assert.False(WaveTuningData.TryRead(Field(json), out var data, out var code, out var reason),
            "the field was accepted: " + JsonSerializer.Serialize(data));
        Assert.NotNull(code);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        return (code!, reason!);
    }

    private static JsonElement Field(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void OneValueLeavesEveryOtherFieldOnTheGameOwnValues()
    {
        var data = Read(Minimal);
        Assert.Equal(3f, data.MaxHeat);
        Assert.Null(data.AllowedTotalCost);
        Assert.Null(data.HeatCooldownSpeed);
        Assert.Null(data.BaseWeights);
        Assert.Null(data.HeatAtStart);
        Assert.Null(data.TypeCostTowardsCap);
        Assert.False(data.IsEmpty);
    }

    [Fact]
    public void EveryFieldReads()
    {
        var data = Read("""
        {
          "allowedTotalCost": 12,
          "heatCooldownSpeed": 0.5,
          "maxHeat": 4,
          "baseWeights": [ 1, 2, 3, 4, 5 ],
          "heatAtStart": [ 0, 0, 0, 0, 0 ],
          "typeCostTowardsCap": [ 5, 4, 3, 2, 1 ]
        }
        """);
        Assert.Equal(12f, data.AllowedTotalCost);
        Assert.Equal(0.5f, data.HeatCooldownSpeed);
        Assert.Equal(4f, data.MaxHeat);
        Assert.Equal(new float[] { 1, 2, 3, 4, 5 }, data.BaseWeights);
        Assert.Equal(new float[] { 0, 0, 0, 0, 0 }, data.HeatAtStart);
        Assert.Equal(new float[] { 5, 4, 3, 2, 1 }, data.TypeCostTowardsCap);
    }

    [Fact]
    public void ATableOfTheWrongLengthIsRefusedRatherThanPadded()
    {
        var (code, _) = Refuse("""{ "baseWeights": [ 1, 2, 3, 4 ] }""");
        Assert.Equal("wave-tuning-table", code);
        Assert.Equal(5, WaveTuningData.TypeCount);
    }

    [Fact]
    public void ANegativeOrNonFiniteNumberIsRefused()
    {
        Assert.Equal("wave-tuning-value", Refuse("""{ "maxHeat": -1 }""").Code);
        Assert.Equal("wave-tuning-value", Refuse("""{ "heatAtStart": [ 1, 1, 1, 1, "x" ] }""").Code);
        Assert.Equal("wave-tuning-value", Refuse("""{ "allowedTotalCost": true }""").Code);
    }

    [Fact]
    public void AnUnknownMemberOrTheDeletedFixesAreRefused()
    {
        Assert.Equal("wave-tuning-field", Refuse("""{ "max_heat": 3 }""").Code);
        Assert.Equal("wave-tuning-field", Refuse("""{ "maxHeat": 3, "fixes": { "boss": true } }""").Code);
        Assert.Equal("wave-tuning-field", Refuse("""{ "schemaVersion": 1, "maxHeat": 3 }""").Code);
    }

    [Fact]
    public void AFieldThatNamesNothingIsRefused()
    {
        Assert.Equal("wave-tuning-empty", Refuse("""{ }""").Code);
        // The deleted `fixes` block is an unknown member now, which is what keeps it from being read as tuning.
        Assert.Equal("wave-tuning-field", Refuse("""{ "fixes": {} }""").Code);
    }

    [Fact]
    public void AFieldOfAnotherKindIsRefusedWithItsOwnCode()
    {
        Assert.Equal("wave-tuning-json", Refuse("[ 1, 2 ]").Code);
        Assert.Equal("wave-tuning-json", Refuse("3").Code);
    }

    [Fact]
    public void TheFieldsOwnNameIsTheMapDocumentsCamelCaseMember()
    {
        Assert.Equal("waveTuning", WaveTuningData.FieldName);
    }
}
