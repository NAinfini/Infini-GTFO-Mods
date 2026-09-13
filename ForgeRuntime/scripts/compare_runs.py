#!/usr/bin/env python3
"""Compare two current-format Forge Runtime diagnostics reports without inferring bugs.

Only the one current report format is accepted. There is no version field, no migration and no
fallback: a report that carries schemaVersion, sourceHashStatus or any other retired root field is
rejected instead of reinterpreted. Whether two runs observed the same source is read from
objectReferences.sourceVerification and objectReferences.scanStatus, never guessed from a subject
or a path.
"""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
import json
from pathlib import Path
import sys
from typing import Any


REPORT_FORMAT = "gtfo-forge-diagnostics-report"
MAX_REPORT_BYTES = 16 * 1024 * 1024
MAX_REPORT_ITEMS = 100_000

ROOT_FIELDS = (
    "format",
    "runId",
    "startedAtUtc",
    "exportedAtUtc",
    "outcome",
    "playability",
    "metadata",
    "events",
    "eventAggregates",
    "checks",
    "issues",
    "overflow",
    "objectReferences",
)
REFERENCE_FIELDS = (
    "worldEpoch",
    "simulationTick",
    "scope",
    "scanStatus",
    "sourceVerification",
    "groups",
    "overflow",
)
REFERENCE_GROUP_FIELDS = (
    "expeditionId",
    "kind",
    "authorId",
    "locator",
    "status",
    "reasonCode",
    "observedCandidateCount",
    "candidatesTruncated",
    "candidates",
)
REFERENCE_OVERFLOW_FIELDS = ("droppedNativeObjects", "droppedCandidates", "droppedAreas")
REPORT_OVERFLOW_FIELDS = (
    "droppedMetadata",
    "droppedEvents",
    "sampledDetailEvents",
    "droppedAggregateEvents",
    "droppedChecks",
    "droppedIssues",
    "droppedEventFields",
    "truncatedStrings",
)
SCAN_STATUSES = ("not_requested", "pending", "complete", "partial", "cancelled", "rejected")
SOURCE_VERIFICATIONS = ("not_provided", "pending", "matched", "mismatch", "unavailable", "cancelled")
# Retired metadata keys of the previous report generation. Current source state is read from the
# objectReferences receipt, so a report still carrying one of these is rejected, never reinterpreted.
RETIRED_METADATA_KEYS = ("sourcehashstatus", "confighash", "resourcehash")
LOCATION_KEYS = ("region", "zone", "area")
IDENTITY_KEYS = {
    "gameversion": "gameVersion",
    "unityversion": "unityVersion",
    "seed": "seed",
    "hostseed": "hostSeed",
    "sessionseed": "sessionSeed",
    "mainlayout": "mainLayout",
    "secondarylayout": "secondaryLayout",
    "thirdlayout": "thirdLayout",
}


def normalized_key(value: str) -> str:
    return "".join(character for character in value.lower() if character.isalnum())


def require_mapping(value: Any, path: str, fields: tuple[str, ...]) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError(f"{path} must be an object")
    missing = [field for field in fields if field not in value]
    if missing:
        raise ValueError(f"{path} is missing required field {missing[0]}")
    unknown = sorted(set(value) - set(fields))
    if unknown:
        raise ValueError(f"{path} has unknown field {unknown[0]}; this tool reads the current report format only")
    return value


def require_string(value: Any, path: str) -> str:
    if not isinstance(value, str) or not value:
        raise ValueError(f"{path} must be a non-empty string")
    return value


def require_integer(value: Any, path: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ValueError(f"{path} must be a non-negative integer")
    return value


def require_choice(value: Any, path: str, allowed: tuple[str, ...]) -> str:
    if value not in allowed:
        raise ValueError(f"{path} must be one of {', '.join(allowed)}")
    return str(value)


def require_list(value: Any, path: str) -> list[Any]:
    if not isinstance(value, list) or len(value) > MAX_REPORT_ITEMS:
        raise ValueError(f"{path} must be an array of at most {MAX_REPORT_ITEMS} entries")
    return value


def load_report(path: Path, maximum_bytes: int) -> dict[str, Any]:
    try:
        size = path.stat().st_size
    except OSError as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    if size > maximum_bytes:
        raise ValueError(f"{path} is {size} bytes; the report limit is {maximum_bytes} bytes")
    try:
        report = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    if not isinstance(report, dict):
        raise ValueError(f"{path} must contain a JSON object")
    if "schemaVersion" in report:
        raise ValueError(f"{path} carries schemaVersion; this tool reads the current report format only")
    try:
        validate_report(report, str(path))
    except (ValueError, RecursionError) as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    return report


def require_timestamp(value: Any, path: str) -> str:
    # The root timestamps are ISO-8601 strings for every producer, including the log import tool,
    # and the website report reader accepts nothing else. A missing field is rejected by the root
    # field check and null is rejected here.
    text = require_string(value, path)
    try:
        datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError as error:
        raise ValueError(f"{path} must be an ISO-8601 timestamp: {error}") from error
    return text


def validate_report(report: dict[str, Any], label: str) -> None:
    if report.get("format") != REPORT_FORMAT:
        raise ValueError(f"{label} does not declare format {REPORT_FORMAT}")
    require_mapping(report, f"{label} root", ROOT_FIELDS)
    require_string(report["runId"], f"{label}.runId")
    require_timestamp(report["startedAtUtc"], f"{label}.startedAtUtc")
    require_timestamp(report["exportedAtUtc"], f"{label}.exportedAtUtc")
    require_string(report["outcome"], f"{label}.outcome")
    require_string(report["playability"], f"{label}.playability")
    metadata = report["metadata"]
    if not isinstance(metadata, dict):
        raise ValueError(f"{label}.metadata must be an object")
    for key, value in metadata.items():
        if not isinstance(key, str) or not isinstance(value, str):
            raise ValueError(f"{label}.metadata must map strings to strings")
        if normalized_key(key) in RETIRED_METADATA_KEYS:
            raise ValueError(f"{label}.metadata.{key} is a retired field; read the current objectReferences receipt instead")
    for name in ("events", "eventAggregates", "checks", "issues"):
        for index, entry in enumerate(require_list(report[name], f"{label}.{name}")):
            if not isinstance(entry, dict):
                raise ValueError(f"{label}.{name}[{index}] must be an object")
    overflow = require_mapping(report["overflow"], f"{label}.overflow", REPORT_OVERFLOW_FIELDS)
    for field in REPORT_OVERFLOW_FIELDS:
        require_integer(overflow[field], f"{label}.overflow.{field}")
    receipt = require_mapping(report["objectReferences"], f"{label}.objectReferences", REFERENCE_FIELDS)
    require_integer(receipt["worldEpoch"], f"{label}.objectReferences.worldEpoch")
    tick = receipt["simulationTick"]
    if tick is not None:
        require_integer(tick, f"{label}.objectReferences.simulationTick")
    if receipt["scope"] != "generated-floor":
        raise ValueError(f"{label}.objectReferences.scope must be generated-floor")
    require_choice(receipt["scanStatus"], f"{label}.objectReferences.scanStatus", SCAN_STATUSES)
    require_choice(receipt["sourceVerification"], f"{label}.objectReferences.sourceVerification", SOURCE_VERIFICATIONS)
    for index, group in enumerate(require_list(receipt["groups"], f"{label}.objectReferences.groups")):
        path = f"{label}.objectReferences.groups[{index}]"
        require_mapping(group, path, REFERENCE_GROUP_FIELDS)
        require_string(group["expeditionId"], f"{path}.expeditionId")
        require_string(group["kind"], f"{path}.kind")
        require_string(group["authorId"], f"{path}.authorId")
        if not isinstance(group["locator"], dict):
            raise ValueError(f"{path}.locator must be an object")
        require_string(group["status"], f"{path}.status")
        require_string(group["reasonCode"], f"{path}.reasonCode")
        require_integer(group["observedCandidateCount"], f"{path}.observedCandidateCount")
        if not isinstance(group["candidatesTruncated"], bool):
            raise ValueError(f"{path}.candidatesTruncated must be a boolean")
        require_list(group["candidates"], f"{path}.candidates")
    receipt_overflow = require_mapping(receipt["overflow"], f"{label}.objectReferences.overflow", REFERENCE_OVERFLOW_FIELDS)
    for field in REFERENCE_OVERFLOW_FIELDS:
        require_integer(receipt_overflow[field], f"{label}.objectReferences.overflow.{field}")


def runtime_identity(metadata: dict[str, Any]) -> dict[str, str]:
    found: dict[str, str] = {}
    for key, value in metadata.items():
        raw_key = str(key)
        normalized = normalized_key(raw_key)
        if normalized in IDENTITY_KEYS:
            found[IDENTITY_KEYS[normalized]] = str(value)
        elif raw_key.lower().startswith("plugin:"):
            found[raw_key] = str(value)
    return found


def identity_comparison(before: dict[str, Any], after: dict[str, Any]) -> dict[str, Any]:
    before_identity = runtime_identity(before["metadata"])
    after_identity = runtime_identity(after["metadata"])
    required = set(IDENTITY_KEYS.values())
    before_plugins = {key for key in before_identity if key.lower().startswith("plugin:")}
    after_plugins = {key for key in after_identity if key.lower().startswith("plugin:")}
    missing_before = sorted(required - before_identity.keys())
    missing_after = sorted(required - after_identity.keys())
    if not before_plugins:
        missing_before.append("plugin:*")
    if not after_plugins:
        missing_after.append("plugin:*")
    missing_before.extend(sorted(after_plugins - before_plugins))
    missing_after.extend(sorted(before_plugins - after_plugins))
    shared = before_identity.keys() & after_identity.keys()
    differences = [
        {"key": key, "before": before_identity[key], "after": after_identity[key]}
        for key in sorted(shared)
        if before_identity[key] != after_identity[key]
    ]
    if differences:
        status = "different_runtime_identity"
    elif missing_before or missing_after:
        status = "not_fully_verified"
    else:
        status = "fully_verified"
    return {
        "status": status,
        "before": before_identity,
        "after": after_identity,
        "missingBefore": missing_before,
        "missingAfter": missing_after,
        "differences": differences,
    }


def source_state_comparison(before: dict[str, Any], after: dict[str, Any]) -> dict[str, Any]:
    before_receipt, after_receipt = before["objectReferences"], after["objectReferences"]
    before_status = str(before_receipt["sourceVerification"])
    after_status = str(after_receipt["sourceVerification"])
    before_scan = str(before_receipt["scanStatus"])
    after_scan = str(after_receipt["scanStatus"])
    both_complete = before_scan == "complete" and after_scan == "complete"
    both_matched = before_status == "matched" and after_status == "matched"
    if both_complete and both_matched:
        assessment = "matched_and_complete"
    elif before_status != after_status or before_scan != after_scan:
        assessment = "different_source_state"
    else:
        assessment = "incomplete_source_state"
    return {
        "assessment": assessment,
        "scope": "sourceVerification and scanStatus of the native object reference receipt; a rejected or unverified scan never counts as a matched source",
        "before": {"sourceVerification": before_status, "scanStatus": before_scan},
        "after": {"sourceVerification": after_status, "scanStatus": after_scan},
        "bothComplete": both_complete,
        "bothMatched": both_matched,
    }


def reference_group_key(group: dict[str, Any]) -> str:
    # The declared author identity and its locator, never a subject or path guess.
    return json.dumps(
        {
            "expeditionId": group["expeditionId"],
            "kind": group["kind"],
            "authorId": group["authorId"],
            "locator": group["locator"],
        },
        sort_keys=True,
        ensure_ascii=False,
    )


def reference_group_facts(group: dict[str, Any]) -> dict[str, Any]:
    # Native candidate identity only: kind plus instance ID. Locator geometry is declared data and
    # belongs to the reference, not to a guess about where an issue happened.
    candidates = [
        {"kind": candidate.get("kind"), "instanceId": candidate.get("instanceId")}
        for candidate in group["candidates"]
    ]
    return {
        "status": str(group["status"]),
        "reasonCode": str(group["reasonCode"]),
        "observedCandidateCount": group["observedCandidateCount"],
        "candidatesTruncated": group["candidatesTruncated"],
        "candidates": candidates,
    }


def reference_overflow_facts(receipt: dict[str, Any]) -> dict[str, int]:
    return {field: receipt["overflow"][field] for field in REFERENCE_OVERFLOW_FIELDS}


def reference_group_changes(before: dict[str, Any], after: dict[str, Any]) -> list[dict[str, Any]]:
    before_groups = {reference_group_key(group): group for group in before["objectReferences"]["groups"]}
    after_groups = {reference_group_key(group): group for group in after["objectReferences"]["groups"]}
    changes: list[dict[str, Any]] = []
    for key in sorted(before_groups.keys() | after_groups.keys()):
        old = before_groups.get(key)
        new = after_groups.get(key)
        if old is not None and new is not None:
            before_facts, after_facts = reference_group_facts(old), reference_group_facts(new)
            if before_facts == after_facts:
                continue
            changes.append({"reference": json.loads(key), "before": before_facts, "after": after_facts})
        elif old is not None:
            changes.append({"reference": json.loads(key), "before": reference_group_facts(old), "after": None})
        else:
            changes.append({"reference": json.loads(key), "before": None, "after": reference_group_facts(new)})
    return changes


def reference_overflow_changes(before: dict[str, Any], after: dict[str, Any]) -> list[dict[str, Any]]:
    before_overflow = reference_overflow_facts(before["objectReferences"])
    after_overflow = reference_overflow_facts(after["objectReferences"])
    return [
        {"key": field, "before": before_overflow[field], "after": after_overflow[field], "delta": after_overflow[field] - before_overflow[field]}
        for field in REFERENCE_OVERFLOW_FIELDS
        if before_overflow[field] != after_overflow[field]
    ]


def object_reference_comparison(before: dict[str, Any], after: dict[str, Any]) -> dict[str, Any]:
    group_changes = reference_group_changes(before, after)
    overflow_changes = reference_overflow_changes(before, after)
    return {
        "groupChanges": group_changes,
        "overflowChanges": overflow_changes,
        "hasChanges": bool(group_changes or overflow_changes),
    }


def changed_mapping(before: dict[str, Any], after: dict[str, Any]) -> list[dict[str, Any]]:
    changes = []
    for key in sorted(before.keys() | after.keys()):
        if before.get(key) != after.get(key):
            changes.append({"key": key, "before": before.get(key), "after": after.get(key)})
    return changes


def event_signature(event: dict[str, Any]) -> tuple[str, str, str, str, str, str] | None:
    fields = event.get("fields")
    if not isinstance(fields, dict):
        return None
    lowered = {normalized_key(str(key)): str(value) for key, value in fields.items()}
    region = next((lowered[key] for key in LOCATION_KEYS if key in lowered), "")
    geomorph = lowered.get("geomorph", lowered.get("prefab", ""))
    seed = lowered.get("seed", "")
    random_before = lowered.get("randombefore", "")
    random_after = lowered.get("randomafter", "")
    if not any((region, geomorph, seed, random_before, random_after)):
        return None
    return (str(event.get("stage", "")), region, geomorph, seed, random_before, random_after)


def signatures(report: dict[str, Any]) -> Counter[tuple[str, str, str, str, str, str]]:
    values: Counter[tuple[str, str, str, str, str, str]] = Counter()
    for event in report["events"]:
        if isinstance(event, dict) and (signature := event_signature(event)) is not None:
            values[signature] += 1
    return values


def signature_dict(signature: tuple[str, str, str, str, str, str], count: int) -> dict[str, Any]:
    return dict(zip(("stage", "region", "geomorph", "seed", "randomBefore", "randomAfter"), signature)) | {"count": count}


def counter_difference(before: Counter[Any], after: Counter[Any], convert) -> dict[str, list[dict[str, Any]]]:
    return {
        "removed": [convert(key, count) for key, count in sorted((before - after).items())],
        "added": [convert(key, count) for key, count in sorted((after - before).items())],
    }


def regions(report: dict[str, Any]) -> set[str]:
    found: set[str] = set()
    for event in report["events"]:
        fields = event.get("fields", {}) if isinstance(event, dict) else {}
        if not isinstance(fields, dict):
            continue
        lowered = {normalized_key(str(key)): str(value) for key, value in fields.items()}
        found.update(lowered[key] for key in LOCATION_KEYS if key in lowered)
    return found


def issues(report: dict[str, Any]) -> Counter[tuple[str, str, str]]:
    found: Counter[tuple[str, str, str]] = Counter()
    for issue in report["issues"]:
        if not isinstance(issue, dict):
            continue
        try:
            count = int(issue.get("count", 0))
        except (TypeError, ValueError):
            count = 0
        # Native identity stays part of the aggregation key, exactly like the report writer.
        native = issue.get("nativeObject")
        native_key = "null" if native is None else f"{native['kind']}:{native['instanceId']}"
        found[(str(issue.get("type", "")), str(issue.get("source", "")), native_key)] += max(0, count)
    return found


def issue_changes(before: Counter[tuple[str, str, str]], after: Counter[tuple[str, str, str]]) -> list[dict[str, Any]]:
    result = []
    for key in sorted(before.keys() | after.keys()):
        if before[key] != after[key]:
            result.append({
                "type": key[0],
                "source": key[1],
                "nativeObject": None if key[2] == "null" else key[2],
                "before": before[key],
                "after": after[key],
                "delta": after[key] - before[key],
            })
    return result


def lifecycle_counts(report: dict[str, Any]) -> dict[str, float]:
    counts: Counter[str] = Counter()
    for event in report["events"]:
        if not isinstance(event, dict):
            continue
        category = str(event.get("category", ""))
        stage = str(event.get("stage", ""))
        if "lifecycle" not in (category + " " + stage).lower():
            continue
        subject = str(event.get("subject", ""))
        counts[f"event:{stage}:{subject}"] += 1
        fields = event.get("fields", {})
        if isinstance(fields, dict):
            for key, value in fields.items():
                try:
                    counts[f"field:{stage}:{subject}:{key}"] += float(value)
                except (TypeError, ValueError):
                    pass
    for check in report["checks"]:
        if not isinstance(check, dict) or "lifecycle" not in str(check.get("kind", "")).lower():
            continue
        counts[f"check:{check.get('kind', '')}:{check.get('subject', '')}:{check.get('status', '')}"] += 1
    return dict(counts)


def numeric_changes(before: dict[str, float], after: dict[str, float]) -> list[dict[str, Any]]:
    result = []
    for key in sorted(before.keys() | after.keys()):
        old, new = before.get(key, 0), after.get(key, 0)
        if old != new:
            result.append({"key": key, "before": old, "after": new, "delta": new - old})
    return result


def comparison_readiness(identity: dict[str, Any], source_state: dict[str, Any], references: dict[str, Any]) -> tuple[str, list[str]]:
    reasons: list[str] = []
    different_inputs = False
    if identity["status"] == "different_runtime_identity":
        reasons.append("different_runtime_identity")
        different_inputs = True
    elif identity["status"] == "not_fully_verified":
        reasons.append("runtime_identity_not_fully_verified")
    if source_state["assessment"] == "different_source_state":
        reasons.append("different_source_state")
        different_inputs = True
    elif source_state["assessment"] == "incomplete_source_state":
        reasons.append("source_state_incomplete")
    if references["groupChanges"]:
        reasons.append("object_reference_group_changes")
    if references["overflowChanges"]:
        reasons.append("object_reference_overflow_changes")
    if not reasons:
        return "strict_same_inputs", reasons
    return ("different_inputs" if different_inputs else "not_fully_verified"), reasons


def compare(before: dict[str, Any], after: dict[str, Any]) -> dict[str, Any]:
    identity = identity_comparison(before, after)
    source_state = source_state_comparison(before, after)
    references = object_reference_comparison(before, after)
    readiness, readiness_reasons = comparison_readiness(identity, source_state, references)
    strictly_comparable = readiness == "strict_same_inputs"

    before_signatures, after_signatures = signatures(before), signatures(after)
    signature_changes = counter_difference(before_signatures, after_signatures, signature_dict)
    random_changed = bool(signature_changes["removed"] or signature_changes["added"])
    # This assessment is about observed event signatures only. Reference group and overflow
    # differences are reported as their own change lists, so they never masquerade as a signature
    # finding and never turn "no observed difference" into a bug claim.
    if not random_changed:
        random_assessment = "no_observed_difference"
    elif strictly_comparable:
        random_assessment = "same_verified_inputs_difference_requires_investigation"
    elif identity["status"] == "different_runtime_identity":
        random_assessment = "different_runtime_identity_no_bug_inference"
    elif source_state["assessment"] == "different_source_state":
        random_assessment = "different_source_state_no_bug_inference"
    elif source_state["assessment"] == "incomplete_source_state":
        random_assessment = "source_state_incomplete_no_bug_inference"
    else:
        random_assessment = "not_fully_verified_no_bug_inference"

    before_regions, after_regions = regions(before), regions(after)
    return {
        "beforeRunId": before["runId"],
        "afterRunId": after["runId"],
        "runtimeIdentity": identity,
        "sourceState": source_state,
        "objectReferences": references,
        "readiness": readiness,
        "readinessReasons": readiness_reasons,
        "eventCoverage": {
            "before": before["overflow"],
            "after": after["overflow"],
            "scope": "Event signatures and per-object lifecycle comparisons use retained details only. Aggregated or dropped details cannot prove identical generation.",
        },
        "eventAggregates": {"before": before["eventAggregates"], "after": after["eventAggregates"]},
        "metadataChanges": changed_mapping(before["metadata"], after["metadata"]),
        "regions": {"removed": sorted(before_regions - after_regions), "added": sorted(after_regions - before_regions)},
        "seedGeomorph": signature_changes,
        "randomAssessment": random_assessment,
        "bugInferred": False,
        "issueChanges": issue_changes(issues(before), issues(after)),
        "lifecycleChanges": numeric_changes(lifecycle_counts(before), lifecycle_counts(after)),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("-o", "--output", type=Path)
    parser.add_argument("--max-bytes", type=int, default=MAX_REPORT_BYTES)
    args = parser.parse_args(argv)
    try:
        result = compare(load_report(args.before, args.max_bytes), load_report(args.after, args.max_bytes))
        rendered = json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
        if args.output:
            args.output.write_text(rendered, encoding="utf-8")
        else:
            sys.stdout.write(rendered)
        return 0
    except (OSError, ValueError) as error:
        print(f"compare_runs: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
