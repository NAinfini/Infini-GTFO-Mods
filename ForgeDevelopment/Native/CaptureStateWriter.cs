using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>
/// The snapshot serializer. The shape of a state record lives here and nowhere else: reason, scope, counts and one
/// array per section. A row's fields are written as strings because a snapshot is read back by a person and diffed by
/// text; a value that could not be read is written as null and never dropped, so a missing field is visible rather
/// than absent. It holds no session, so the shape can be produced and read back in a process with no recorder.
/// </summary>
internal static partial class CaptureStateWriter
{
    internal const string Channel = "state";
    internal const string SnapshotKind = "snapshot";
    internal const string ChangesKind = "changes";

    /// <summary>The body of a snapshot record.</summary>
    internal static void WriteSnapshot(Utf8JsonWriter json, CaptureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(snapshot);
        json.WriteString("reason", snapshot.Reason);
        json.WriteString("scope", snapshot.Scope);
        json.WriteNumber("tick", snapshot.Tick);
        json.WriteNumber("rows", snapshot.RowCount);
        json.WriteNumber("sections", snapshot.Sections.Count);
        if (snapshot.Skipped.Count != 0)
        {
            json.WritePropertyName("skipped");
            json.WriteStartArray();
            foreach (var name in snapshot.Skipped) json.WriteStringValue(name);
            json.WriteEndArray();
        }
        if (snapshot.Notes.Count != 0)
        {
            json.WritePropertyName("notes");
            json.WriteStartArray();
            foreach (var note in snapshot.Notes) json.WriteStringValue(note);
            json.WriteEndArray();
        }
        json.WritePropertyName("sections");
        json.WriteStartObject();
        foreach (var section in snapshot.Sections)
        {
            json.WritePropertyName(section.Name);
            json.WriteStartObject();
            json.WriteString("source", section.Source);
            json.WriteNumber("rows", section.Rows.Count);
            json.WritePropertyName("items");
            json.WriteStartArray();
            foreach (var row in section.Rows) WriteRow(json, row);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    private static void WriteRow(Utf8JsonWriter json, CaptureRow row)
    {
        json.WriteStartObject();
        json.WriteString("kind", row.Kind);
        json.WriteString("id", row.Identity);
        json.WriteString("name", row.Name);
        if (row.Zone != null) json.WriteString("zone", row.Zone);
        if (row.Group != null) json.WriteString("group", row.Group);
        json.WritePropertyName("fields");
        json.WriteStartObject();
        foreach (var field in row.Fields)
        {
            if (field.Value == null) json.WriteNull(field.Key);
            else json.WriteString(field.Key, field.Value);
        }
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>The body of a change record, separate from the channel write for the same reason a snapshot's is.</summary>
    internal static void WriteChangeBody(Utf8JsonWriter json, CaptureChangeResult result, long tick, string scope)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(result);
        json.WriteString("scope", scope);
        json.WriteNumber("tick", tick);
        json.WriteNumber("rows", result.Rows);
        json.WriteNumber("fields", result.Fields);
        json.WriteNumber("added", result.Added);
        json.WriteNumber("removed", result.Removed);
        json.WriteNumber("changed", result.Changed);
        json.WriteBoolean("budgetExhausted", result.BudgetExhausted);
        json.WriteNumber("ms", Math.Round(result.Milliseconds, 3));
        json.WritePropertyName("items");
        json.WriteStartArray();
        foreach (var row in result.ChangedRows) WriteRow(json, row);
        json.WriteEndArray();
    }

    /// <summary>The sections a per-frame change pass diffs. They are the ones a change record is defined over: door
    /// and generator state, player life, enemy AI state and objective state.</summary>
    internal static readonly string[] ChangeSections =
    {
        CaptureSections.Doors, CaptureSections.Generators, CaptureSections.Players, CaptureSections.Enemies,
        CaptureSections.Objectives, CaptureSections.ChainedPuzzles
    };

    internal static IEnumerable<CaptureSection> Select(CaptureSnapshot snapshot, IReadOnlyList<string> names)
    {
        foreach (var name in names)
            foreach (var section in snapshot.Sections)
                if (string.Equals(section.Name, name, StringComparison.Ordinal))
                {
                    yield return section;
                    break;
                }
    }

    /// <summary>True for a section a change record is defined over. The snapshot uses it to mark the sections a
    /// per-frame pass must not skip, so the two lists cannot drift apart.</summary>
    internal static bool IsChangeSection(string name)
    {
        foreach (var candidate in ChangeSections)
            if (string.Equals(candidate, name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The per-frame budget: how many rows one change pass may touch and how long it may take. Both are
    /// written into the session record so a session says what its change detection actually ran with.</summary>
    internal const int DefaultChangeRowsPerFrame = 4096;
    internal const int DefaultChangeMillisecondsPerFrame = 2;

    internal static string Counters(CaptureChangeResult result)
        => result.Changed.ToString(CultureInfo.InvariantCulture) + "/" + result.Rows.ToString(CultureInfo.InvariantCulture);
}
