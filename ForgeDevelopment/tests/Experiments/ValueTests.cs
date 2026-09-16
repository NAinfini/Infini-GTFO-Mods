using System.Text.Json;
using ForgeDevelopment.Native;

internal static class ValueTests
{
    internal static void Run(Suite suite)
    {
        var root = new Holder { Number = 5f, Name = "shield", Child = new Leaf(Nested: 3), List = new List<Leaf> { new(Nested: 11) } };
        var number = ExperimentValue.Read(root, "Number");
        suite.Check("value.readField", number.Ok && (float)number.Value! == 5f, number.Error);
        var nested = ExperimentValue.Read(root, "Child.Nested");
        suite.Check("value.readNested", nested.Ok && (int)nested.Value! == 3, nested.Error);
        var hidden = ExperimentValue.Read(root, "Child.Hidden");
        suite.Check("value.readPrivate", hidden.Ok && (int)hidden.Value! == root.Child!.HiddenValue, hidden.Error);
        suite.Check("value.writePrivate", ExperimentValue.Write(root, "Child.Hidden", Json.Element("12")).Ok && root.Child!.HiddenValue == 12);
        var list = ExperimentValue.Read(root, "List.0.Nested");
        suite.Check("value.readIndexed", list.Ok && (int)list.Value! == 11, list.Error);
        var missing = ExperimentValue.Read(root, "Child.Missing");
        suite.Check("value.readMissing", !missing.Ok && missing.Error.Contains("Missing"), missing.Error);
        var throughNull = ExperimentValue.Read(root, "Nothing.Deeper");
        suite.Check("value.readThroughNull", !throughNull.Ok, throughNull.Error);

        suite.Check("value.writeField", ExperimentValue.Write(root, "Number", Json.Element("9.5")).Ok && root.Number == 9.5f);
        suite.Check("value.writeNested", ExperimentValue.Write(root, "Child.Nested", Json.Element("4")).Ok && root.Child!.Nested == 4);
        var readOnly = ExperimentValue.Write(root, "Computed", Json.Element("4"));
        suite.Check("value.writeReadOnly", !readOnly.Ok, readOnly.Error);
        var badValue = ExperimentValue.Write(root, "Child.Nested", Json.Element("\"abc\""));
        suite.Check("value.writeBadValue", !badValue.Ok && badValue.Error.Contains("is not a"), badValue.Error);
        var badPath = ExperimentValue.Write(root, "Nothing.Deeper", Json.Element("1"));
        suite.Check("value.writeThroughNull", !badPath.Ok, badPath.Error);

        suite.Check("value.formatFloat", ExperimentValue.Format(1.5f) == "1.5");
        suite.Check("value.formatBool", ExperimentValue.Format(true) == "true");
        suite.Check("value.formatEnum", ExperimentValue.Format(LevelGeneration.LG_LayerType.MainLayer) == "0/MainLayer");
        suite.Check("value.formatNull", ExperimentValue.Format(null) == "null");
    }

    private sealed class Holder
    {
        internal float Number;
        internal string Name = "";
        internal Leaf? Child;
        internal Leaf? Nothing;
        internal List<Leaf> List = new();
        internal int Computed => 1;
    }

    private sealed class Leaf
    {
        internal Leaf(int Nested) => this.Nested = Nested;

        internal int Nested;
        private int Hidden = 7;
        internal int HiddenValue => Hidden;
        internal void SetHidden(int value) => Hidden = value;
    }
}
