#!/usr/bin/env python3
"""Import a historical GTFO text log as a bounded, current-format Forge Runtime report.

The text log carries no native object identity, run seed, per-error region or start time, so the
report says exactly that: the native object receipt is a rejected epoch-0 scan, the run seed and
playability checks stay not_checked, and no issue ever claims a native object.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import tempfile
from typing import TextIO


REPORT_FORMAT = "gtfo-forge-diagnostics-report"
MAX_SOURCE_BYTES = 16 * 1024 * 1024
MAX_ISSUES = 1024
MAX_MESSAGE = 4096
MAX_STACK = 16384
CULLING_MESSAGE = "A renderer has been destroyed that is included in a C_CullingCluster"
EXCEPTION = re.compile(r"(?P<type>[A-Za-z_][\w.]*Exception):\s*(?P<message>.*)")
BEPIN_ERROR = re.compile(r"^\[Error\s*:\s*([^\]]+)\]\s*(.*)$", re.IGNORECASE)
STACK_SOURCE = re.compile(r"^([A-Za-z_][\w.+`]*(?:\.[A-Za-z_][\w.+`]*)*:[A-Za-z_][\w.+`]*)\s*\(")


@dataclass
class Pending:
    issue_type: str
    message: str
    explicit_source: str | None
    stack: list[str]


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def source_from_stack(lines: list[str]) -> str:
    fallback = "unknown"
    for line in lines:
        match = STACK_SOURCE.match(line.strip())
        if not match:
            continue
        candidate = match.group(1)
        fallback = candidate
        if not candidate.startswith(("UnityEngine.Logger:", "UnityEngine.Debug:")):
            return candidate
    return fallback


def rejected_object_references() -> dict:
    """The epoch-0 receipt: no native scan ran, so nothing may look scanned or verified."""
    return {
        "worldEpoch": 0,
        "simulationTick": None,
        "scope": "generated-floor",
        "scanStatus": "rejected",
        "sourceVerification": "not_provided",
        "groups": [],
        "overflow": {"droppedNativeObjects": 0, "droppedCandidates": 0, "droppedAreas": 0},
    }


def import_stream(stream: TextIO, source_name: str, source_hash: str, run_id: str | None = None) -> dict:
    # The report expresses this import session. The game start time is unknown and is reported as
    # unknown in metadata; it is never invented and never replaces the root timestamp.
    started_at = utc_now()
    issues: dict[tuple[str, str], dict] = {}
    pending: Pending | None = None
    dropped_issues = 0
    truncated_strings = 0
    total_lines = 0

    def clip(value: str, maximum: int) -> str:
        nonlocal truncated_strings
        if len(value) <= maximum:
            return value
        truncated_strings += 1
        return value[:maximum]

    def add_issue(issue_type: str, source: str, message: str, stack: list[str]) -> None:
        nonlocal dropped_issues
        key = (clip(issue_type, 128), clip(source, 4096))
        if key in issues:
            issues[key]["count"] += 1
            if not issues[key]["representativeStack"] and stack:
                issues[key]["representativeStack"] = clip("\n".join(stack), MAX_STACK)
            return
        if len(issues) >= MAX_ISSUES:
            dropped_issues += 1
            return
        issues[key] = {
            "type": key[0],
            "source": key[1],
            "message": clip(message, MAX_MESSAGE),
            "representativeStack": clip("\n".join(stack), MAX_STACK),
            "count": 1,
            # The source log carries no timestamp, and the report contract requires ISO-8601
            # strings here, not null: this is the import session that observed the error text,
            # never a claim about when the game produced it.
            "firstSeenUtc": started_at,
            "lastSeenUtc": started_at,
            # A text log never identifies the native object an error came from.
            "nativeObject": None,
        }

    def finish_pending() -> None:
        nonlocal pending
        if pending is None:
            return
        source = pending.explicit_source or source_from_stack(pending.stack)
        add_issue(pending.issue_type, source, pending.message, pending.stack)
        pending = None

    for raw_line in stream:
        total_lines += 1
        line = raw_line.rstrip("\r\n")
        if CULLING_MESSAGE in line:
            finish_pending()
            pending = Pending("DestroyedRendererInCullingCluster", CULLING_MESSAGE, "CullingSystem.C_CullingCluster:HideSafe", [])
            continue

        exception = EXCEPTION.search(line)
        error = BEPIN_ERROR.match(line)
        unity_error = line.startswith("ERROR : ")
        if exception:
            finish_pending()
            pending = Pending(exception.group("type").split(".")[-1], clip(exception.group("message"), MAX_MESSAGE), None, [])
        elif error:
            finish_pending()
            pending = Pending("LogError", clip(error.group(2).strip(), MAX_MESSAGE), error.group(1).strip(), [])
        elif unity_error:
            finish_pending()
            pending = Pending("UnityLogError", clip(line.removeprefix("ERROR : ").strip(), MAX_MESSAGE), None, [])
        elif pending is not None:
            if not line.strip() or line.startswith("[") or len(pending.stack) >= 32:
                finish_pending()
            else:
                pending.stack.append(clip(line.strip(), MAX_MESSAGE))
    finish_pending()

    exported_at = utc_now()
    run_id = run_id or f"imported-{source_hash[:12]}"
    return {
        "format": REPORT_FORMAT,
        "runId": run_id,
        "startedAtUtc": started_at,
        "exportedAtUtc": exported_at,
        "outcome": "historical_log_imported",
        "playability": "not_assessed",
        "metadata": {
            "imported": "true",
            "sourceName": source_name,
            "sourceSha256": source_hash,
            "sourceLineCount": str(total_lines),
            "sourceTimeAvailability": "not_available_in_source",
            "gameStartTimeAvailability": "not_available_in_source",
            "runSeedAvailability": "not_available_in_source",
        },
        "events": [],
        "eventAggregates": [],
        "checks": [
            {
                "timestampUtc": exported_at,
                "kind": "historical_import",
                "subject": source_name,
                "status": "observed",
                "detail": f"Errors were aggregated from the supplied log ({total_lines} lines).",
                "nativeObject": None,
            },
            {
                "timestampUtc": exported_at,
                "kind": "run_seed",
                "subject": run_id,
                "status": "not_checked",
                "detail": "The source log does not provide a reliable run seed.",
                "nativeObject": None,
            },
            {
                "timestampUtc": exported_at,
                "kind": "object_references",
                "subject": run_id,
                "status": "not_checked",
                "detail": "The source log carries no native object identity, so the object reference scan stays rejected.",
                "nativeObject": None,
            },
            {
                "timestampUtc": exported_at,
                "kind": "playability",
                "subject": run_id,
                "status": "not_checked",
                "detail": "A historical error log cannot establish that a level is completable.",
                "nativeObject": None,
            },
        ],
        "issues": sorted(issues.values(), key=lambda issue: (issue["type"], issue["source"])),
        "overflow": {
            "droppedMetadata": 0,
            "droppedEvents": 0,
            "sampledDetailEvents": 0,
            "droppedAggregateEvents": 0,
            "droppedChecks": 0,
            "droppedIssues": dropped_issues,
            "droppedEventFields": 0,
            "truncatedStrings": truncated_strings,
        },
        "objectReferences": rejected_object_references(),
    }


def hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require_source_size(path: Path, maximum: int) -> None:
    size = path.stat().st_size
    if size > maximum:
        raise ValueError(f"{path} is {size} bytes; the log import limit is {maximum} bytes")


def write_atomic(path: Path, report: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(report, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, path)
    finally:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--run-id")
    parser.add_argument("--max-bytes", type=int, default=MAX_SOURCE_BYTES)
    args = parser.parse_args(argv)
    try:
        require_source_size(args.source, args.max_bytes)
        source_hash = hash_file(args.source)
        with args.source.open("r", encoding="utf-8", errors="replace") as stream:
            report = import_stream(stream, args.source.name, source_hash, args.run_id)
        write_atomic(args.output, report)
        print(args.output)
        return 0
    except (OSError, ValueError) as error:
        print(f"import_log: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
