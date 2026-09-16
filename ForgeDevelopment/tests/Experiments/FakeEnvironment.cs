using System.Text.Json;
using ForgeDevelopment.Native;

internal sealed class FakeEnvironment : ExperimentEnvironment
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal)
    { ["m_lastShieldVal"] = 42f, ["m_x"] = 0f };
    private readonly HashSet<string> _traces = new(StringComparer.Ordinal);

    internal long Frames;
    internal double ReadDelayMilliseconds;
    internal int Reads;
    internal int Screenshots;
    internal int Clones;
    internal string TargetError = "";
    internal int TraceAfterFrame = -1;
    internal int ChangeAfterFrame = -1;
    private object? _changedValue = "changed";

    public long Frame { get; internal set; }
    public double Seconds { get; internal set; }
    public bool IsHost => true;
    public object? LastResult { get; set; }

    public ExperimentTargetResult Resolve(ExperimentTargetSpec spec) =>
        TargetError.Length != 0 ? ExperimentTargetResult.Failed(TargetError) : ExperimentTargetResult.One("fake " + spec.Raw, this);

    public ExperimentCallResult Call(ExperimentTargetResult target, ExperimentStep step)
    {
        if (step.Name != "UpdateShield") return ExperimentCallResult.Failed("no method named '" + step.Name + "'");
        var arguments = string.Join(", ", step.Arguments.Select(argument => argument.GetRawText()));
        return ExperimentCallResult.Done(step.Name + "(" + arguments + ") -> ok", "result-of-" + step.Name);
    }

    public ExperimentCloneResult Clone(ExperimentTargetResult target, string path, string parent, string text)
    {
        Clones++;
        return path == "m_missing"
            ? ExperimentCloneResult.Failed("no property or field named 'm_missing'")
            : ExperimentCloneResult.Done("cloned " + path + " under " + parent + (text.Length == 0 ? "" : " text=" + text), "clone-" + Clones);
    }

    public ExperimentValue.ReadResult Read(ExperimentTargetResult target, string path)
    {
        Reads++;
        if (ReadDelayMilliseconds > 0) Thread.Sleep((int)Math.Round(ReadDelayMilliseconds));
        return _values.TryGetValue(path, out var value)
            ? new ExperimentValue.ReadResult(true, value, "")
            : new ExperimentValue.ReadResult(false, null, "no property or field named '" + path + "'");
    }

    public ExperimentValue.WriteResult Write(ExperimentTargetResult target, string path, JsonElement literal)
    {
        if (!_values.ContainsKey(path)) return new ExperimentValue.WriteResult(false, "no property or field named '" + path + "'");
        _values[path] = literal.ValueKind == JsonValueKind.Number ? (object)literal.GetSingle() : literal.GetRawText();
        return new ExperimentValue.WriteResult(true, "");
    }

    public ExperimentWaitResult WaitForPath(ExperimentTargetResult target, string path, bool changed, object? baseline)
    {
        if (ChangeAfterFrame >= 0 && Frame >= ChangeAfterFrame && _changedValue != null)
            return ExperimentWaitResult.Complete("changed to " + ExperimentValue.Format(_changedValue));
        return ExperimentWaitResult.Pending("still " + ExperimentValue.Format(baseline));
    }

    public string WorldEvent(JsonElement fields) => "Type=" + (fields.TryGetProperty("Type", out var type) ? type.GetRawText() : "?");

    public string Screenshot(string reason) => "shots/" + ++Screenshots + ".png";

    public bool TraceSeen(string typeName, string methodName)
    {
        if (TraceAfterFrame < 0) return false;
        if (Frame >= TraceAfterFrame) _traces.Add(typeName + "." + methodName);
        return _traces.Contains(typeName + "." + methodName);
    }
}

