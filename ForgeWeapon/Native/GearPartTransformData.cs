using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>Authored gear-part poses, read once at plugin start.
///
/// A package writes one file per equipment block at `plugins/&lt;package&gt;/forge/gear-parts/&lt;blockId&gt;.json`,
/// the same package-directory rule plans use. The data is presentation only: it names a native transform and the
/// absolute local values to write onto it, and every number is checked here so a hostile or broken file can only
/// ever be refused, never applied. The snapshot is immutable once loaded — there is no watch, no reload and no
/// second source: a bad file is refused on its own, with one diagnostic carrying the first reason found.</summary>
internal static class GearPartTransformData
{
    /// <summary>Cap on one discovered file, enforced on the file's own length before it is read.</summary>
    internal const int MaximumFileBytes = 256 * 1024;
    /// <summary>Per-axis absolute position, in Unity metres.</summary>
    internal const float MaximumPosition = 1f;
    /// <summary>Per-axis scale, as a multiplier of the part's own size.</summary>
    internal const float MinimumScale = 0.05f, MaximumScale = 4f;
    /// <summary>Part entries in one file.</summary>
    internal const int MaximumParts = 64;
    /// <summary>Segments in one child path counted from the part down through every parent segment: nesting may not
    /// buy extra depth, so the limit is the finished path's length, not each node's own one.</summary>
    internal const int MaximumPathSegments = 8;

    internal static GearPartTransformSnapshot Load(string bepInExRoot, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(rejected);
        var parsed = new List<(string Relative, GearPartTransformBlock Block)>();
        foreach (var (relative, path, length) in Discover(bepInExRoot))
        {
            if (length > MaximumFileBytes)
            {
                rejected(Diagnostic(relative, "gear-part-size", "The file is " + Number(length) + " bytes; the cap is " + Number(MaximumFileBytes) + "."));
                continue;
            }
            string text;
            try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                rejected(Diagnostic(relative, "gear-part-unreadable", error.Message));
                continue;
            }
            if (!TryParse(text, FileNameBlockId(relative), out var block, out var reason, out var code))
            {
                rejected(Diagnostic(relative, code!, reason!));
                continue;
            }
            parsed.Add((relative, block!));
        }
        // One block has one file for the whole install. When two or more files claim the same block the block is
        // withdrawn whole — the first file is not kept as a winner — so which pose an install ends up with never
        // depends on the order the files were found, and every claiming file is named once.
        var claims = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, block) in parsed)
            claims[block.BlockId] = claims.TryGetValue(block.BlockId, out var count) ? count + 1 : 1;
        var blocks = new Dictionary<string, GearPartTransformBlock>(StringComparer.Ordinal);
        foreach (var (relative, block) in parsed)
        {
            if (claims[block.BlockId] > 1)
            {
                rejected(Diagnostic(relative, "gear-part-duplicate-block", "Block " + block.BlockId + " is claimed by "
                    + Number(claims[block.BlockId]) + " files; the block is left unposed."));
                continue;
            }
            blocks.Add(block.BlockId, block);
        }
        return new GearPartTransformSnapshot(blocks);
    }

    /// <summary>Every `plugins/&lt;one directory&gt;/forge/gear-parts/&lt;name&gt;.json`, ordinally sorted so one install
    /// always scans the same way. `plugins/` missing is not an error, and the directory is never recursed: a
    /// package directory is exactly one level deep, like the plan scanner's.</summary>
    private static List<(string Relative, string Path, long Length)> Discover(string bepInExRoot)
    {
        var hits = new List<(string Relative, string Path, long Length)>();
        var pluginsRoot = Path.Combine(bepInExRoot, "plugins");
        if (!Directory.Exists(pluginsRoot)) return hits;
        foreach (var pluginDir in Directory.EnumerateDirectories(pluginsRoot))
        {
            var partsDir = Path.Combine(pluginDir, "forge", "gear-parts");
            if (!Directory.Exists(partsDir)) continue;
            foreach (var file in Directory.EnumerateFiles(partsDir))
            {
                if (!file.EndsWith(".json", StringComparison.Ordinal)) continue;
                var relative = Path.GetRelativePath(bepInExRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                long length;
                try { length = new FileInfo(file).Length; }
                catch (IOException) { continue; }
                hits.Add((relative, file, length));
            }
        }
        hits.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        return hits;
    }

    /// <summary>The block id the file name itself claims. Null when the name carries none — that file can never be
    /// accepted, because the name and the content have to spell the same block.</summary>
    private static string? FileNameBlockId(string relative)
    {
        var name = Path.GetFileName(relative);
        var block = name.Substring(0, name.Length - ".json".Length);
        return IsBlockId(block) ? block : null;
    }

    private static bool TryParse(string text, string? fileNameBlockId, out GearPartTransformBlock? block, out string? reason, out string? code)
    {
        block = null; reason = null; code = null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error) { code = "gear-part-json"; reason = error.Message; return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { code = "gear-part-json"; reason = "The file's root is not an object."; return false; }
            if (!Text(root, "blockId", out var idText))
            { code = "gear-part-id"; reason = "The file carries no `blockId` text."; return false; }
            // One spelling of the block id everywhere: the file name, the `blockId` field and the catalog reference
            // are the same digits. A second spelling (leading zero, sign, radix prefix) is refused, never parsed.
            if (!IsBlockId(idText!)) { code = "gear-part-id"; reason = "`blockId` \"" + idText + "\" is not a plain decimal block id."; return false; }
            if (fileNameBlockId == null || !string.Equals(fileNameBlockId, idText, StringComparison.Ordinal))
            {
                code = "gear-part-id";
                reason = "The file name claims block " + (fileNameBlockId ?? "<not a block id>") + " but its `blockId` is " + idText + ".";
                return false;
            }
            if (!root.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            { code = "gear-part-parts"; reason = "The file carries no `parts` array."; return false; }
            if (parts.GetArrayLength() > MaximumParts)
            { code = "gear-part-limit"; reason = "The file carries " + Number(parts.GetArrayLength()) + " parts; the cap is " + Number(MaximumParts) + "."; return false; }
            var parsed = new List<GearPartTransform>(parts.GetArrayLength());
            var seen = new HashSet<GearPartSlot>();
            int index = 0;
            foreach (var entry in parts.EnumerateArray())
            {
                if (!TryParseTransform(entry, "", index, out var part, out reason, out code)) return false;
                if (!seen.Add(part!.Slot))
                { code = "gear-part-component"; reason = "`" + part.Slot + "` is configured more than once in this file."; return false; }
                parsed.Add(part);
                index++;
            }
            block = new GearPartTransformBlock(idText!, parsed.ToArray());
            return true;
        }
    }

    /// <summary>One part or one child node. `parentPath` is where this node sits below its part: empty for a
    /// top-level entry, which also has to name a part slot the holder really has, and the already validated path
    /// of the node above it otherwise.</summary>
    private static bool TryParseTransform(JsonElement entry, string parentPath, int index, out GearPartTransform? transform, out string? reason, out string? code)
    {
        transform = null; reason = null; code = null;
        if (entry.ValueKind != JsonValueKind.Object)
        { code = "gear-part-json"; reason = "Entry " + Number(index) + " is not an object."; return false; }
        GearPartSlot slot = default;
        uint? partId = null;
        if (parentPath.Length == 0)
        {
            if (!Text(entry, "component", out var component) || !Enum.TryParse(component, ignoreCase: false, out slot) || !Enum.IsDefined(slot))
            { code = "gear-part-component"; reason = "Entry " + Number(index) + " does not name a gear part component."; return false; }
            if (entry.TryGetProperty("partId", out var id))
            {
                if (id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var value) || value == 0)
                { code = "gear-part-id"; reason = "`" + slot + "` carries a `partId` that is not a positive block id."; return false; }
                partId = value;
            }
        }
        bool? enabled = null;
        if (entry.TryGetProperty("enabled", out var enabledValue))
        {
            if (enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            { code = "gear-part-number"; reason = "`enabled` is not a boolean."; return false; }
            enabled = enabledValue.GetBoolean();
        }
        if (!Vector(entry, "localPosition", out var position, out reason, out code)
            || !Vector(entry, "localEulerAngles", out var euler, out reason, out code)
            || !Vector(entry, "localScale", out var scale, out reason, out code)) return false;
        var children = Array.Empty<GearPartChild>();
        if (entry.TryGetProperty("children", out var childArray))
        {
            if (childArray.ValueKind != JsonValueKind.Array)
            { code = "gear-part-json"; reason = "`children` is not an array."; return false; }
            var parsed = new List<GearPartChild>(childArray.GetArrayLength());
            foreach (var child in childArray.EnumerateArray())
            {
                if (!Text(child, "path", out var path) || !TryPath(parentPath, path!, out var fullPath))
                { code = "gear-part-path"; reason = "A child path is not a relative transform path inside the part."; return false; }
                if (!TryParseTransform(child, fullPath!, index, out var node, out reason, out code)) return false;
                parsed.Add(new GearPartChild(fullPath!, node!));
            }
            children = parsed.ToArray();
        }
        transform = new GearPartTransform(slot, partId, enabled, position, euler, scale, children);
        return true;
    }

    private static bool Vector(JsonElement entry, string field, out Vector3? value, out string? reason, out string? code)
    {
        value = null; reason = null; code = null;
        if (!entry.TryGetProperty(field, out var element)) return true;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
        { code = "gear-part-number"; reason = "`" + field + "` is not three numbers."; return false; }
        Span<float> axes = stackalloc float[3];
        int axis = 0;
        foreach (var component in element.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number || !component.TryGetSingle(out var number) || !float.IsFinite(number))
            { code = "gear-part-number"; reason = "`" + field + "` carries a value that is not a finite number."; return false; }
            axes[axis++] = number;
        }
        // The authored bounds, one spelling each: position per axis within a metre, scale within its multiplier
        // band, and euler angles normalized into [0, 360) so the same pose has exactly one authored form.
        if (string.Equals(field, "localPosition", StringComparison.Ordinal))
        {
            if (Math.Abs(axes[0]) > MaximumPosition || Math.Abs(axes[1]) > MaximumPosition || Math.Abs(axes[2]) > MaximumPosition)
            { code = "gear-part-limit"; reason = "`localPosition` exceeds " + Number(MaximumPosition) + " m on an axis."; return false; }
            value = new Vector3(axes[0], axes[1], axes[2]);
            return true;
        }
        if (string.Equals(field, "localScale", StringComparison.Ordinal))
        {
            if (axes[0] < MinimumScale || axes[1] < MinimumScale || axes[2] < MinimumScale
                || axes[0] > MaximumScale || axes[1] > MaximumScale || axes[2] > MaximumScale)
            { code = "gear-part-limit"; reason = "`localScale` is outside [" + Number(MinimumScale) + ", " + Number(MaximumScale) + "] on an axis."; return false; }
            value = new Vector3(axes[0], axes[1], axes[2]);
            return true;
        }
        value = new Vector3(Normalize(axes[0]), Normalize(axes[1]), Normalize(axes[2]));
        return true;
    }

    /// <summary>Every child path is one or more `/`-separated names, each non-empty, without a leading, trailing or
    /// doubled separator and never `.` or `..`: the loader hands the text to the game's own hierarchy lookup, so a
    /// path that could climb out of the part is refused here instead of being attempted there. The depth is counted
    /// on the finished path, this node's own segments plus every segment above it, so a nested node cannot push the
    /// whole path past the authored limit one level at a time.</summary>
    private static bool TryPath(string parentPath, string path, out string? full)
    {
        full = null;
        if (string.IsNullOrEmpty(path)) return false;
        var fullPath = parentPath.Length == 0 ? path : parentPath + "/" + path;
        var segments = fullPath.Split('/');
        if (segments.Length > MaximumPathSegments) return false;
        foreach (var segment in segments)
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
        full = fullPath;
        return true;
    }

    private static float Normalize(float degrees) => degrees - 360f * MathF.Floor(degrees / 360f);

    /// <summary>The canonical block id text: digits that parse to a `uint` and are that value's own text again,
    /// which is the spelling the catalog uses. Empty text, a sign, surrounding space, a leading zero, a radix
    /// prefix, a decimal point and anything above `UInt32.MaxValue` are other spellings and are refused.</summary>
    internal static bool IsBlockId(string? text)
        => text != null && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && text == value.ToString(CultureInfo.InvariantCulture);

    private static bool Text(JsonElement entry, string field, out string? value)
    {
        value = null;
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(field, out var element)
            || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value != null;
    }

    private static string Diagnostic(string relative, string code, string reason)
        => "weapon." + code + " file=" + relative + ": " + reason;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>One package's pose for one equipment block, keyed by the catalog's block id text.</summary>
internal sealed class GearPartTransformBlock
{
    internal GearPartTransformBlock(string blockId, GearPartTransform[] parts) { BlockId = blockId; Parts = parts; }
    internal string BlockId { get; }
    internal GearPartTransform[] Parts { get; }
}

/// <summary>One part slot's pose, plus the optional block id of the part the author expects in that slot.</summary>
internal sealed class GearPartTransform
{
    internal GearPartTransform(GearPartSlot slot, uint? partId, bool? enabled, Vector3? position, Vector3? eulerAngles,
        Vector3? scale, GearPartChild[] children)
    {
        Slot = slot; PartId = partId; Enabled = enabled; Position = position; EulerAngles = eulerAngles; Scale = scale; Children = children;
    }
    internal GearPartSlot Slot { get; }
    internal uint? PartId { get; }
    /// <summary>Null means the file said nothing about visibility, which is not the same as "make it visible".</summary>
    internal bool? Enabled { get; }
    internal Vector3? Position { get; }
    internal Vector3? EulerAngles { get; }
    internal Vector3? Scale { get; }
    internal GearPartChild[] Children { get; }
}

/// <summary>A node below a part, either a direct child or another node's child. The path is the one the loader
/// hands to the game's own hierarchy lookup, and it is resolved against the part's transform.</summary>
internal sealed class GearPartChild
{
    internal GearPartChild(string path, GearPartTransform node) { Path = path; Node = node; }
    internal string Path { get; }
    internal GearPartTransform Node { get; }
}

/// <summary>The part slots a pose may name: the twenty-one components `GearPartHolder` itself exposes a
/// `GameObject` for. The value is the game's own `eGearComponent` member, so the applier's slot-to-property table
/// is an explicit switch over this enum and never a reflected property name. The numeric values are the build's
/// own and the layout suite checks every one of them against `Modules-ASM`; a pose names the member, so a wrong
/// number here would silently pose another slot.</summary>
internal enum GearPartSlot : byte
{
    FrontPart = 12,
    FrontPartAttachmentA = 13,
    FrontPartAttachmentB = 14,
    ReceiverPart = 16,
    ReceiverPartAttachment = 17,
    StockPart = 19,
    SightPart = 21,
    MagPart = 23,
    FlashlightPart = 25,
    ToolMainPart = 27,
    ToolMainPartAttachment = 29,
    ToolGripPart = 30,
    ToolDeliveryPart = 33,
    ToolDeliveryPartAttachment = 35,
    ToolPayloadPart = 37,
    ToolTargetingPart = 40,
    ToolScreenPart = 42,
    MeleeHeadPart = 44,
    MeleeNeckPart = 46,
    MeleeHandlePart = 48,
    MeleePommelPart = 50
}

/// <summary>The immutable startup snapshot: every accepted block by its canonical id text.</summary>
internal sealed class GearPartTransformSnapshot
{
    internal static readonly GearPartTransformSnapshot Empty = new(new Dictionary<string, GearPartTransformBlock>(StringComparer.Ordinal));
    private readonly IReadOnlyDictionary<string, GearPartTransformBlock> _blocks;
    internal GearPartTransformSnapshot(IReadOnlyDictionary<string, GearPartTransformBlock> blocks) { _blocks = blocks; }
    internal int BlockCount => _blocks.Count;
    internal bool TryGet(string blockId, out GearPartTransformBlock block) => _blocks.TryGetValue(blockId, out block!);
}
