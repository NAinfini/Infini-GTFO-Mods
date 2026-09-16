#!/usr/bin/env python3
"""Query a ForgeDevelopment recorder session offline.

The recorder writes one JSONL record per line into a session directory (BepInEx/ForgeReports/rec-*), optionally
gzipped and split into several segments. This tool reads those sessions, filters them and prints a table or JSON.
It never opens the game, never writes into the game's directory and never needs a third-party package.

Examples:
    python ForgeDevelopment/scripts/rec-query.py BepInEx/ForgeReports/rec-20260916T101500Z-host-0
    python ForgeDevelopment/scripts/rec-query.py <session> --channel tracer --method Open --limit 20
    python ForgeDevelopment/scripts/rec-query.py <session> --around 12.5 --window 5 --json
    python ForgeDevelopment/scripts/rec-query.py <session> --list-channels --list-kinds
"""

from __future__ import annotations

import argparse
import glob
import gzip
import json
import os
import sys
from typing import Any, Iterable, Iterator


def session_files(session: str) -> list[str]:
    """Every segment of a session, in segment order. A directory may also be a parent holding several sessions."""
    if os.path.isfile(session):
        return [session]
    files = sorted(glob.glob(os.path.join(session, "*.jsonl")) + glob.glob(os.path.join(session, "*.jsonl.gz")))
    if not files:
        raise SystemExit(f"no JSONL segments under {session}")
    return files


def records(files: Iterable[str]) -> Iterator[dict[str, Any]]:
    """Yields every record of every segment. A line that is not an object is reported and skipped: a truncated
    session is exactly the case this tool exists for, and refusing to read the rest of it would hide the answer."""
    for path in files:
        opener = gzip.open if path.endswith(".gz") else open
        with opener(path, "rt", encoding="utf-8") as handle:
            for number, line in enumerate(handle, start=1):
                line = line.strip()
                if not line:
                    continue
                try:
                    value = json.loads(line)
                except json.JSONDecodeError as error:
                    print(f"{os.path.basename(path)}:{number}: unreadable record: {error}", file=sys.stderr)
                    continue
                if isinstance(value, dict):
                    yield value
                else:
                    print(f"{os.path.basename(path)}:{number}: not a record object", file=sys.stderr)


def read_index(session: str) -> dict[str, Any] | None:
    if not os.path.isdir(session):
        return None
    path = os.path.join(session, "index.json")
    if not os.path.exists(path):
        return None
    with open(path, "rt", encoding="utf-8") as handle:
        return json.load(handle)


def body_of(record: dict[str, Any]) -> dict[str, Any]:
    body = record.get("body")
    return body if isinstance(body, dict) else {}


def text_of(record: dict[str, Any], key: str) -> str:
    value = body_of(record).get(key)
    return value if isinstance(value, str) else ""


def matches(record: dict[str, Any], args: argparse.Namespace) -> bool:
    if args.channel and record.get("channel") not in args.channel:
        return False
    if args.kind and record.get("kind") not in args.kind:
        return False
    if args.role and record.get("role") != args.role:
        return False
    body = body_of(record)
    if args.type and args.type.lower() not in str(body.get("type", "")).lower():
        return False
    if args.method and args.method.lower() not in str(body.get("method", "")).lower():
        return False
    if args.contains:
        line = json.dumps(record, ensure_ascii=False)
        if args.contains.lower() not in line.lower():
            return False
    if args.since is not None or args.until is not None:
        snet = record.get("snetTime")
        snapped = snet if isinstance(snet, (int, float)) else None
        if snapped is None:
            return False
        if args.since is not None and snapped < args.since:
            return False
        if args.until is not None and snapped > args.until:
            return False
    if args.from_ms is not None or args.to_ms is not None:
        stamp = record.get("t")
        if not isinstance(stamp, (int, float)):
            return False
        if args.from_ms is not None and stamp < args.from_ms:
            return False
        if args.to_ms is not None and stamp > args.to_ms:
            return False
    return True


def bookmarks(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [row for row in rows if row.get("channel") == "mark"]


def around(rows: list[dict[str, Any]], bookmark: dict[str, Any], window: float) -> list[dict[str, Any]]:
    """Records within `window` seconds of a bookmark. The comparison uses snetTime when both carry one, because a
    bookmark is taken by a player and the machine clock is not the game's clock; it falls back to the process clock."""
    snap, stamp = bookmark.get("snetTime"), bookmark.get("t")
    picked = []
    for row in rows:
        other_snap, other_stamp = row.get("snetTime"), row.get("t")
        if isinstance(snap, (int, float)) and isinstance(other_snap, (int, float)):
            if abs(other_snap - snap) <= window:
                picked.append(row)
        elif isinstance(stamp, (int, float)) and isinstance(other_stamp, (int, float)):
            if abs(other_stamp - stamp) <= window * 1000:
                picked.append(row)
    return picked


def summarize(record: dict[str, Any]) -> str:
    body = body_of(record)
    parts = []
    for key in ("profile", "type", "method", "code", "message", "label", "reason", "path", "status", "count", "dropped"):
        value = body.get(key)
        if value not in (None, ""):
            parts.append(f"{key}={value}")
    for key in ("args", "result"):
        if key in body:
            parts.append(f"{key}={json.dumps(body[key], ensure_ascii=False)[:80]}")
    return " ".join(parts)


def print_table(rows: list[dict[str, Any]], width: int) -> None:
    header = f"{'seq':>7}  {'t(ms)':>8}  {'snet':>8}  {'frame':>7}  {'channel':<8}  {'kind':<14}  detail"
    print(header)
    print("-" * len(header))
    for row in rows:
        snet = row.get("snetTime")
        detail = summarize(row)
        if len(detail) > width:
            detail = detail[: width - 1] + "…"
        print(
            f"{row.get('seq', 0):>7}  {row.get('t', 0):>8}  "
            f"{(f'{snet:.3f}' if isinstance(snet, (int, float)) else '-'):>8}  "
            f"{row.get('frame', '-'):>7}  {str(row.get('channel', ''))[:8]:<8}  {str(row.get('kind', ''))[:14]:<14}  {detail}"
        )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Query a Forge recorder session.")
    parser.add_argument("session", help="session directory (rec-*) or one JSONL/JSONL.GZ segment")
    parser.add_argument("--channel", action="append", help="channel to keep; repeatable")
    parser.add_argument("--kind", action="append", help="record kind to keep; repeatable")
    parser.add_argument("--type", help="substring of the traced type name")
    parser.add_argument("--method", help="substring of the traced method name")
    parser.add_argument("--role", help="host or client")
    parser.add_argument("--contains", help="substring anywhere in the record")
    parser.add_argument("--since", type=float, help="keep records with snetTime at or after this value")
    parser.add_argument("--until", type=float, help="keep records with snetTime at or before this value")
    parser.add_argument("--from-ms", type=float, help="keep records with process milliseconds at or after this value")
    parser.add_argument("--to-ms", type=float, help="keep records with process milliseconds at or before this value")
    parser.add_argument("--around", type=float, help="keep records within --window seconds of the bookmark with this snetTime")
    parser.add_argument("--window", type=float, default=5.0, help="seconds around --around (default 5)")
    parser.add_argument("--limit", type=int, default=0, help="print at most this many records (0 means all)")
    parser.add_argument("--width", type=int, default=120, help="maximum detail column width in the table")
    parser.add_argument("--json", action="store_true", help="print the selected records as one JSON array")
    parser.add_argument("--list-channels", action="store_true", help="count records per channel and exit")
    parser.add_argument("--list-kinds", action="store_true", help="count records per channel/kind and exit")
    parser.add_argument("--index", action="store_true", help="print the session's index.json and exit")
    args = parser.parse_args(argv)

    if args.index:
        index = read_index(args.session)
        if index is None:
            print(f"no index.json under {args.session}", file=sys.stderr)
            return 2
        print(json.dumps(index, indent=2, ensure_ascii=False))
        return 0

    rows = list(records(session_files(args.session)))

    if args.list_channels or args.list_kinds:
        counts: dict[str, int] = {}
        for row in rows:
            key = str(row.get("channel", "")) if args.list_channels else f"{row.get('channel', '')}/{row.get('kind', '')}"
            counts[key] = counts.get(key, 0) + 1
        for key in sorted(counts):
            print(f"{counts[key]:>8}  {key}")
        print(f"{len(rows):>8}  total")
        return 0

    selected = [row for row in rows if matches(row, args)]

    if args.around is not None:
        marks = [row for row in rows if row.get("channel") == "mark"]
        if not marks:
            print("no bookmark in this session", file=sys.stderr)
            return 2
        nearest = min(marks, key=lambda row: abs(float(row.get("snetTime") or row.get("t") or 0) - args.around))
        selected = around(selected or rows, nearest, args.window)
        print(f"# around bookmark {text_of(nearest, 'label')!r} on {nearest.get('role')} at "
              f"snetTime={nearest.get('snetTime')} t={nearest.get('t')}ms", file=sys.stderr)

    if args.limit:
        selected = selected[: args.limit]

    if args.json:
        print(json.dumps(selected, ensure_ascii=False, indent=2))
    else:
        print_table(selected, args.width)
        print(f"{len(selected)} of {len(rows)} records")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
