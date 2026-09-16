using System.Text.Json;

// Stand-ins for everything the recorder core needs to see from its host. The production files under Native/ that
// touch Unity, IL2CPP or BepInEx are deliberately not compiled here: the session, the reader and the tracer's
// decision files reach this surface and nothing else, which is what the project's <Compile Include> list asserts.

namespace ForgeDevelopment.Native
{
    /// <summary>The plugin identity the core's records and its one log line go through. The log target is a real
    /// static so <c>RecDiag</c> finds it by the same reflection the game uses.</summary>
    internal static class Plugin
    {
        internal const string PluginName = "Infini Forge Development";
        internal const string PluginVersion = "1.0.0";
        internal static BepInEx.Logging.ManualLogSource PluginLog { get; } = new("ForgeDevelopment");
    }

    /// <summary>The recorder's settings as a plain object. The session is opened by a test with explicit limits, so
    /// only the queue depth is read from here.</summary>
    internal static class Settings
    {
        internal static readonly TestSetting<int> RecorderQueue = new(65536);
        internal static readonly TestSetting<bool> RecorderEnabled = new(true);
        internal static readonly TestSetting<bool> RecorderGzip = new(false);
        internal static readonly TestSetting<int> RecorderBudgetGiB = new(2);
        internal static readonly TestSetting<int> RecorderSegmentMiB = new(8);
        internal static readonly TestSetting<int> RecorderRecordKiB = new(64);
        internal static readonly TestSetting<bool> RecorderTrace = new(true);
        internal static readonly TestSetting<UnityEngine.KeyCode> RecorderBookmarkKey = new(UnityEngine.KeyCode.F6);
        internal static readonly TestSetting<string> RecorderBookmarkLabel = new("");
        internal static readonly TestSetting<UnityEngine.KeyCode> RecorderSnapshotKey = new(UnityEngine.KeyCode.F7);
        internal static readonly TestSetting<UnityEngine.KeyCode> RecorderScreenshotKey = new(UnityEngine.KeyCode.F8);
        internal static readonly TestSetting<UnityEngine.KeyCode> RecorderPanelKey = new(UnityEngine.KeyCode.F5);
    }

    internal sealed class TestSetting<T>
    {
        internal TestSetting(T value) => Value = value;
        internal T Value { get; set; }
    }

    /// <summary>The context a test session writes with. It is the whole <see cref="IRecSessionContext"/> seam: a
    /// record's frame, network time and aim are whatever this says, and the writer never sees a Unity type.</summary>
    internal sealed class TestContext : IRecSessionContext
    {
        internal string RoleValue = "host";
        internal string SlotValue = "2";
        internal string LevelValue = "Rundown/Test";
        internal long EpochValue = 7;
        internal long TickValue = 42;
        internal int FrameValue;
        internal float? SNetTimeValue = 12.5f;
        internal bool AimValue;
        internal string ScreenshotResult = "shots/test.png";
        internal int Screenshots;

        public string Role => RoleValue;
        public string Slot => SlotValue;
        public string Level => LevelValue;
        public string GameVersion => "test-game";
        public string UnityVersion => "test-unity";
        public long WorldEpoch => EpochValue;
        public long Tick => TickValue;
        public int Frame => FrameValue;
        public float? SNetTime => SNetTimeValue;
        public string WriteScreenshot(string directory, string relative) { Screenshots++; return ScreenshotResult; }
        public (string Name, string Value)[]? Aim() => AimValue ? new[] { ("camera", "test") } : null;

        public void WriteContext(Utf8JsonWriter json, long sequence, string sessionId, string channel, string kind)
        {
            json.WriteString("v", RecSession.SchemaVersion);
            json.WriteNumber("seq", sequence);
            json.WriteString("session", sessionId);
            json.WriteString("channel", channel);
            json.WriteString("kind", kind);
            json.WriteNumber("t", sequence);
            json.WriteNumber("frame", ++FrameValue);
            json.WriteString("role", Role);
            json.WriteString("slot", Slot);
            json.WriteString("level", Level);
            json.WriteNumber("worldEpoch", WorldEpoch);
            json.WriteNumber("tick", Tick);
        }
    }

    /// <summary>A managed probe for the reader. It answers exactly what the IL2CPP probe answers — identity,
    /// liveness, a native member — over plain objects, plus one value type of its own so the probe's write path is
    /// exercised without Unity.</summary>
    internal sealed class TestProbe : IRecValueProbe
    {
        internal sealed class Handle
        {
            internal Handle(int id) => Id = id;
            internal int Id { get; }
            public override string ToString() => "handle(" + Id + ")";
        }

        internal sealed class Vector3Like
        {
            private readonly float _x, _y, _z;
            internal Vector3Like(float x, float y, float z) { _x = x; _y = y; _z = z; }
            internal float X => _x;
            internal float Y => _y;
            internal float Z => _z;
        }

        internal readonly Dictionary<object, long> Ids = new(ReferenceEqualityComparer.Instance);
        internal readonly HashSet<object> Destroyed = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<object, Dictionary<string, object?>> NativeMembers = new(ReferenceEqualityComparer.Instance);

        public bool TryInstanceId(object value, out long id) => Ids.TryGetValue(value, out id);
        public bool IsDestroyed(object value) => Destroyed.Contains(value);

        public bool TryReadNativeMember(object target, string name, out object? value)
        {
            if (NativeMembers.TryGetValue(target, out var members) && members.TryGetValue(name, out value)) return true;
            value = null;
            return false;
        }

        public bool Handles(Type type) => type == typeof(Vector3Like);

        public void Write(RecReflect.RecWriter writer, object value, int depth)
        {
            var vector = (Vector3Like)value;
            writer.Json.WriteStartObject();
            writer.Json.WriteString("$type", "TestProbe.Vector3Like");
            writer.WriteNamed(vector.X, "x", depth + 1);
            writer.WriteNamed(vector.Y, "y", depth + 1);
            writer.WriteNamed(vector.Z, "z", depth + 1);
            writer.Json.WriteEndObject();
        }
    }

    /// <summary>The stand-in types the tracer's matching rules are exercised against. The names are chosen so the
    /// wildcard cases read the way a profile reads: a type with a frame-loop method, a getter, a generic method and a
    /// by-ref parameter, plus a gateway type the include/exclude patterns address.</summary>
    internal static class StandIns
    {
        internal sealed class FakeSecurityDoor
        {
            public int m_state;
            private string _label = "door";
            internal string Label => _label;
            public void Open() { }
            public void Update() { }
            public string get_State() => "open";
            public void Configure(ref int value) { }
            public T Echo<T>(T value) => value;
            public void Close(int code) { }
            public void LateUpdate() { }
        }

        internal sealed class FakeTerminal
        {
            public void SendCommand(string command) { }
            private void Login(int user) { }
            public string get_Login() => "none";
            public void OnGUI() { }
        }

        internal sealed class AnotherDoor
        {
            public void Open() { }
        }
    }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigFile { }
}

namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        internal ManualLogSource(string name) => Name = name;
        internal string Name { get; }
        internal readonly List<string> Lines = new();
        public void LogInfo(object message) => Lines.Add("info: " + message);
        public void LogWarning(object message) => Lines.Add("warning: " + message);
        public void LogError(object message) => Lines.Add("error: " + message);
    }
}

namespace BepInEx
{
    public static class Paths
    {
        public static string BepInExRootPath { get; set; } = System.IO.Path.GetTempPath();
    }
}

namespace ForgeRuntime
{
    public enum RuntimeMode { Off = 0, Authoring = 1, Play = 2 }

    public static class Plugin
    {
        public const string PluginVersion = "1.2.0-test";
        public static RuntimeMode ConfiguredMode { get; set; } = RuntimeMode.Authoring;
    }
}

namespace UnityEngine
{
    // The recorder core names one Unity type in its settings surface only; nothing compiled here calls into it.
    public enum KeyCode { None = 0, F5 = 286, F6 = 287, F7 = 288, F8 = 289 }
}
