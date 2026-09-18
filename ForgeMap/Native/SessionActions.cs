using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Native bodies for session-wide Map actions. Checkpoint save goes through CheckpointManager's replicated
/// interaction channel; no static StoreCheckpoint side path is used.</summary>
internal sealed class SessionActions
{
    internal const string AuthorityCode = "authority-or-phase";
    internal const string ParticipantsCode = "checkpoint-participants-required";
    internal const string TargetsCode = "too-many-targets";
    internal const string AnchorCode = "checkpoint-anchor-invalid";
    internal const string UnavailableCode = "checkpoint-unavailable";
    internal const string CommittedCode = "checkpoint-store-issued";
    internal const string CommitExceptionCode = "native-commit-exception";

    private readonly Func<bool> _ready;
    private readonly Action<string> _report;

    internal SessionActions(Func<bool> ready, Action<string> report)
    { _ready = ready ?? throw new ArgumentNullException(nameof(ready)); _report = report ?? throw new ArgumentNullException(nameof(report)); }

    internal CommandResult CheckpointSave(CommandContext context)
    {
        if (!context.IsHost || !_ready()) return CommandResult.Rejected(AuthorityCode);
        var participants = context.Inputs.GetProperty("participants").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        if (participants.Length == 0) return CommandResult.Rejected(ParticipantsCode);
        if (participants.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        if (!TryVector(context.Inputs.GetProperty("anchor"), out var anchor)) return CommandResult.Rejected(AnchorCode);
        var manager = CheckpointManager.Current;
        if (manager == null || manager.WasCollected) return CommandResult.Rejected(UnavailableCode);

        try
        {
            manager.AttemptInteract(new pCheckpointInteraction(eCheckpointInteractionType.StoreCheckpoint, anchor));
        }
        catch (Exception error)
        {
            _report("map.checkpoint-save-commit-exception: " + error.GetType().Name);
            var failed = participants.Select(target => new ResultRow(target, CommandStatuses.Failed,
                CommitStates.Unknown, CommitExceptionCode, participants.Length)).ToArray();
            return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode, "",
                RuntimeJson.From(new { results = failed }));
        }

        var rows = participants.Select(target => new ResultRow(target, CommandStatuses.Succeeded,
            CommitStates.Confirmed, CommittedCode, participants.Length)).ToArray();
        return CommandResult.Succeeded(RuntimeJson.From(new { results = rows }));
    }

    private readonly record struct ResultRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);

    private static bool TryVector(JsonElement value, out Vector3 result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) return false;
        var rows = value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (rows.Any(x => !double.IsFinite(x) || x < float.MinValue || x > float.MaxValue)) return false;
        result = new Vector3((float)rows[0], (float)rows[1], (float)rows[2]);
        return true;
    }
}
