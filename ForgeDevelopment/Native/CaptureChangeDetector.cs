using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace ForgeDevelopment.Native;

/// <summary>What one change pass cost and what it found. The budget is enforced here rather than by the caller, so the
/// same numbers appear in the `changes` record and in the per-frame report.</summary>
internal sealed class CaptureChangeResult
{
    internal int Rows { get; set; }
    internal int Fields { get; set; }
    internal int Added { get; set; }
    internal int Removed { get; set; }
    internal int Changed { get; set; }
    internal bool BudgetExhausted { get; set; }
    internal double Milliseconds { get; set; }
    /// <summary>Rows whose identity was not in the compared sections and is therefore new in this pass.</summary>
    internal List<CaptureRow> ChangedRows { get; } = new();
}

/// <summary>
/// The per-frame change detector. It diffs the rows of the sections a change pass asked for, keyed by identity, and
/// answers only the rows whose signature moved. The budget is a row count and a stopwatch: the pass stops at whichever
/// comes first and says so, because a detector that silently drops frames is indistinguishable from a game stutter.
/// </summary>
internal sealed class CaptureChangeDetector
{
    private readonly Dictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _kinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _current = new(StringComparer.Ordinal);
    private readonly List<string> _vanished = new();

    internal CaptureChangeDetector(int maxRows, int maxMilliseconds)
    {
        MaxRows = Math.Max(1, maxRows);
        MaxMilliseconds = Math.Max(1, maxMilliseconds);
    }

    /// <summary>Rows one pass may compare before it stops. A frame that produces more than this is a frame whose
    /// change record would cost more than the change it reports.</summary>
    internal int MaxRows { get; }

    internal int MaxMilliseconds { get; }

    /// <summary>Rows seen by the pass that seeded the detector; the first pass reports nothing because everything is
    /// new relative to nothing.</summary>
    internal int Seeded { get; private set; }

    internal CaptureChangeResult Compare(IEnumerable<CaptureSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var stopwatch = Stopwatch.StartNew();
        var result = new CaptureChangeResult();
        _current.Clear();
        foreach (var section in sections)
        {
            foreach (var row in section.Rows)
            {
                if (result.Rows >= MaxRows || stopwatch.ElapsedMilliseconds >= MaxMilliseconds)
                {
                    result.BudgetExhausted = true;
                    break;
                }
                result.Rows++;
                _current.Add(row.Identity);
                var signature = row.Signature;
                result.Fields += row.Fields.Count + 1;
                if (!_signatures.TryGetValue(row.Identity, out var previous))
                {
                    // The first pass over an identity is a seed, not a change: the previous value does not exist and
                    // reporting it would make every snapshot after a level load look like a burst of changes.
                    _signatures[row.Identity] = signature;
                    _kinds[row.Identity] = row.Kind;
                    if (Seeded == 0) continue;
                    result.Added++;
                    result.ChangedRows.Add(row);
                    continue;
                }
                if (string.Equals(previous, signature, StringComparison.Ordinal)) continue;
                _signatures[row.Identity] = signature;
                result.Changed++;
                result.ChangedRows.Add(row);
            }
            if (result.BudgetExhausted) break;
        }
        if (!result.BudgetExhausted)
        {
            _vanished.Clear();
            foreach (var identity in _signatures.Keys)
                if (!_current.Contains(identity)) _vanished.Add(identity);
            foreach (var identity in _vanished)
            {
                _signatures.Remove(identity);
                _kinds.TryGetValue(identity, out var kind);
                _kinds.Remove(identity);
                result.Removed++;
                result.ChangedRows.Add(new CaptureRow(kind ?? "unknown", identity, identity).Field("present", "false"));
            }
        }
        if (Seeded == 0 && _signatures.Count != 0) Seeded = _signatures.Count;
        result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>Drops every remembered signature: a level change or a checkpoint restore makes the previous pass
    /// meaningless, and diffing across it would report the whole level as changed.</summary>
    internal void Reset()
    {
        _signatures.Clear();
        _kinds.Clear();
        _current.Clear();
        Seeded = 0;
    }

    internal string Describe()
        => "rows=" + MaxRows.ToString(CultureInfo.InvariantCulture) + " budgetMs=" + MaxMilliseconds.ToString(CultureInfo.InvariantCulture)
            + " tracked=" + _signatures.Count.ToString(CultureInfo.InvariantCulture) + " seeded=" + Seeded.ToString(CultureInfo.InvariantCulture);
}
