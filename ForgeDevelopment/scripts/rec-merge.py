#!/usr/bin/env python3
"""Merge recorder sessions from several machines and report what never reached the host.

Two machines running the authoring build write one session each. Both record the same facts twice: once because the
fact happened locally and once because the network delivered it. This tool aligns the sessions on `snetTime` (the
player's own synchronized session time, written into every record), groups the records that describe one fact, and
prints the pairs a machine recorded but its peer never did — which is the question "the client saw it happen, did the
host ever receive it?".

The pairing rules are data, not code: `probes/points.tsv` carries a `merge配对规则` column per verification point, and
this tool reads it. A rule names the record selection and the fields that identify one event, for example
`trace:net/SNet_StateReplicator.OnStateChange#state=instance.fields.m_state`. Without a rule, the tool still merges:
it pairs records by channel/kind/type/method and reports the difference, and says which pairs came from a rule.

Examples:
    python ForgeDevelopment/scripts/rec-merge.py host=rec-...-host-0 client=rec-...-client-1
    python ForgeDevelopment/scripts/rec-merge.py host=<dir> client=<dir> --points ForgeDevelopment/probes/points.tsv
    python ForgeDevelopment/scripts/rec-merge.py host=<dir> client=<dir> --json > merged.json
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any, Iterable

import importlib.util

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load_query_module():
    """rec-query.py owns the session reading rules; importing it keeps one implementation of them."""
    path = os.path.join(_HERE, "rec-query.py")
    spec = importlib.util.spec_from_file_location("rec_query", path)
    if spec is None or spec.loader is None:
        raise SystemExit(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


query = _load_query_module()

PAIRING_COLUMN = "merge配对规则"
DEFAULT_WINDOW = 1.0


class Rule:
    """One pairing rule from points.tsv.

    `select` is `channel/kind` or `channel/kind/type.method`. Each key is a `name=body.path` pair: the name is how the
    report spells it, the path is where the value is read from the record's body. A record that does not carry every
    path is reported as unpairable rather than silently merged with the wrong partner.
    """

    def __init__(self, point: str, select: str, keys: list[tuple[str, str]], window: float) -> None:
        self.point = point
        self.select = select
        self.keys = keys
        self.window = window

    @property
    def names(self) -> list[str]:
        return [name for name, _ in self.keys]

    @property
    def channel(self) -> str:
        return self.select.split("/", 1)[0]

    @property
    def kind(self) -> str:
        parts = self.select.split("/", 1)
        return parts[1] if len(parts) > 1 else ""

    def applies(self, record: dict[str, Any]) -> bool:
        if record.get("channel") != self.channel:
            return False
        kind = self.kind
        if not kind:
            return True
        if "/" in kind:
            wanted_kind, wanted_subject = kind.split("/", 1)
            if record.get("kind") != wanted_kind:
                return False
            subject = f"{query.text_of(record, 'type')}.{query.text_of(record, 'method')}"
            return subject.startswith(wanted_subject) or wanted_subject.startswith(subject)
        return record.get("kind") == kind

    def key_of(self, record: dict[str, Any]) -> tuple | None:
        body = query.body_of(record)
        values = []
        for _, path in self.keys:
            value: Any = body
            for step in path.split("."):
                if not isinstance(value, dict) or step not in value:
                    return None
                value = value[step]
            # A scalar key stays the value itself, so `key=[3]` reads as the state it names; anything structured is
            # compared as its canonical JSON.
            values.append(value if isinstance(value, (str, int, float, bool)) or value is None
                          else json.dumps(value, sort_keys=True, ensure_ascii=False))
        return tuple(values)


def parse_rule(point: str, text: str) -> Rule | None:
    """`trace:<select>#<name>=<body.path>,<name>=<body.path>[;window=<seconds>]`, as points.tsv writes it. A key
    without a `=` names the body field of the same name. Anything else is not a pairing rule and is reported by the
    caller rather than guessed at."""
    text = text.strip()
    if not text or text.startswith("manual:"):
        return None
    if text.startswith("trace:"):
        text = text[len("trace:"):]
    window = DEFAULT_WINDOW
    if ";window=" in text:
        text, _, tail = text.partition(";window=")
        try:
            window = float(tail)
        except ValueError:
            return None
    select, _, key_text = text.partition("#")
    if not key_text.strip():
        # A selection without a key is not a pairing rule: it would pair every record the selection matched with
        # every other one, which is the opposite of what a merge is asked for.
        return None
    keys: list[tuple[str, str]] = []
    for part in key_text.split(","):
        part = part.strip()
        if not part:
            continue
        name, separator, path = part.partition("=")
        keys.append((name, path if separator else name))
    if not keys:
        return None
    return Rule(point, select.strip(), keys, window)


def load_rules(path: str | None) -> tuple[list[Rule], list[str]]:
    if not path:
        return [], []
    if not os.path.exists(path):
        return [], [f"{path} does not exist; pairing falls back to channel/kind/type/method"]
    rules: list[Rule] = []
    skipped: list[str] = []
    with open(path, "rt", encoding="utf-8") as handle:
        header: list[str] | None = None
        for number, line in enumerate(handle, start=1):
            line = line.rstrip("\n")
            if not line.strip() or line.startswith("#"):
                continue
            columns = line.split("\t")
            if header is None:
                header = columns
                continue
            row = dict(zip(header, columns))
            point = row.get("pointId", f"line{number}")
            raw = row.get(PAIRING_COLUMN, "")
            if not raw.strip():
                continue
            rule = parse_rule(point, raw)
            if rule is None:
                skipped.append(f"{point}: unreadable pairing rule {raw!r}")
            else:
                rules.append(rule)
    return rules, skipped


def fallback_key(record: dict[str, Any]) -> tuple:
    """What a record without a rule pairs on: the channel, kind, traced subject and method."""
    body = query.body_of(record)
    return (
        str(record.get("channel", "")),
        str(record.get("kind", "")),
        str(body.get("type", "")),
        str(body.get("method", "")),
        str(body.get("code", "")),
    )


def role_of(rows: list[dict[str, Any]]) -> str:
    """The machine's role, taken from its own first record. A session whose first record carries no role is
    `unknown` rather than a made-up host or client."""
    for row in rows:
        value = row.get("role")
        if isinstance(value, str) and value:
            return value
    return "unknown"


def merge(sessions: dict[str, list[dict[str, Any]]], rules: list[Rule], tolerance: float,
          paths: dict[str, str] | None = None) -> dict[str, Any]:
    # The caller names a machine; only its own argument carries the directory the index has to be read from.
    paths = {name: (paths or {}).get(name, name) for name in sessions}
    roles = {name: role_of(rows) for name, rows in sessions.items()}
    pairs: list[dict[str, Any]] = []
    unpairable: list[dict[str, Any]] = []

    for rule in rules:
        names = list(sessions)
        # Each unordered pair of sessions is walked once, in the order the caller named them, so a fact both
        # machines recorded produces one row instead of the same row twice.
        paired: set[tuple[str, Any]] = set()
        for index, left_name in enumerate(names):
            for right_name in names[index + 1:]:
                for record in sessions[left_name]:
                    if not rule.applies(record) or (left_name, record.get("seq")) in paired:
                        continue
                    key = rule.key_of(record)
                    if key is None:
                        unpairable.append({"rule": rule.point, "session": left_name, "seq": record.get("seq"), "reason": "record lacks a pairing key"})
                        continue
                    found = None
                    for candidate in sessions[right_name]:
                        if not rule.applies(candidate) or rule.key_of(candidate) != key:
                            continue
                        if not within(record, candidate, rule.window):
                            continue
                        found = candidate
                        break
                    if found is not None:
                        paired.add((left_name, record.get("seq")))
                        paired.add((right_name, found.get("seq")))
                    pairs.append({
                        "rule": rule.point,
                        "key": list(key),
                        "left": {"session": left_name, "role": roles.get(left_name), "seq": record.get("seq"), "snetTime": record.get("snetTime")},
                        "right": None if found is None else {
                            "session": right_name, "role": roles.get(right_name), "seq": found.get("seq"), "snetTime": found.get("snetTime")},
                        "deltaSnet": None if found is None else delta(record, found),
                    })
                # The right side's own unpaired records are rows too: a fact only the second machine recorded would
                # otherwise be invisible, which is the one answer this tool exists to give.
                for record in sessions[right_name]:
                    if not rule.applies(record) or (right_name, record.get("seq")) in paired:
                        continue
                    key = rule.key_of(record)
                    if key is None:
                        unpairable.append({"rule": rule.point, "session": right_name, "seq": record.get("seq"), "reason": "record lacks a pairing key"})
                        continue
                    pairs.append({
                        "rule": rule.point,
                        "key": list(key),
                        "left": {"session": right_name, "role": roles.get(right_name), "seq": record.get("seq"), "snetTime": record.get("snetTime")},
                        "right": None,
                        "deltaSnet": None,
                    })

    unmatched = unpaired_by_fallback(sessions)
    indexes = {name: (query.read_index(path) if os.path.isdir(path) else None) for name, path in paths.items()}
    return {
        "sessions": [
            {"name": name, "role": roles.get(name), "records": len(rows), "index": indexes[name]}
            for name, rows in sessions.items()
        ],
        "rules": [{"point": rule.point, "select": rule.select, "keys": rule.names, "window": rule.window} for rule in rules],
        "pairs": pairs,
        "clientOnly": [entry for entry in unmatched if entry["role"] == "client"],
        "hostOnly": [entry for entry in unmatched if entry["role"] == "host"],
        "unpairable": unpairable,
    }


def within(left: dict[str, Any], right: dict[str, Any], window: float) -> bool:
    """Two records describe the same moment when their snetTime is within the window. A record without snetTime
    still pairs, because the key itself already identified the event and the two machines may be at different
    points of the session's synchronization."""
    left_time, right_time = left.get("snetTime"), right.get("snetTime")
    if isinstance(left_time, (int, float)) and isinstance(right_time, (int, float)):
        return abs(left_time - right_time) <= max(window, 0.0)
    return True


def delta(left: dict[str, Any], right: dict[str, Any]) -> float | None:
    left_time, right_time = left.get("snetTime"), right.get("snetTime")
    if isinstance(left_time, (int, float)) and isinstance(right_time, (int, float)):
        return round(right_time - left_time, 3)
    return None


def unpaired_by_fallback(sessions: dict[str, list[dict[str, Any]]]) -> list[dict[str, Any]]:
    """The records no rule claimed, paired by channel/kind/type/method. This is the "client saw it, host did not"
    answer for a fact whose pairing rule has not been written yet. A record whose role the session did not carry is
    left out: reporting it as a host or a client fact would be inventing the one thing the row is about."""
    seen: dict[tuple, dict[str, Any]] = {}
    for name, rows in sessions.items():
        role = role_of(rows)
        if role not in ("host", "client"):
            continue
        for record in rows:
            if record.get("channel") in ("session", "log"):
                continue
            key = fallback_key(record)
            bucket = seen.setdefault(key, {})
            bucket[role] = bucket.get(role, 0) + 1
            bucket.setdefault(role + "Session", name)
            bucket.setdefault(role + "Seq", record.get("seq", 0))
    result = []
    for key, counts in sorted(seen.items()):
        host, client = counts.get("host", 0), counts.get("client", 0)
        if host == client:
            continue
        role = "client" if client > host else "host"
        result.append({
            "key": list(key),
            "host": host,
            "client": client,
            "role": role,
            "session": counts.get(role + "Session", "unknown"),
            "sampleSeq": counts.get(role + "Seq", 0),
            "delta": client - host,
        })
    return result


def parse_session_argument(value: str) -> tuple[str, str]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("expected name=directory")
    name, _, path = value.partition("=")
    if not name or not path:
        raise argparse.ArgumentTypeError("expected name=directory")
    return name, path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Merge Forge recorder sessions and report what never arrived.")
    parser.add_argument("sessions", nargs="+", type=parse_session_argument,
                        help="name=session-directory; name the machine, e.g. host=... client=...")
    parser.add_argument("--points", default=os.path.join(os.path.dirname(_HERE), "probes", "points.tsv"),
                        help="points.tsv carrying the pairing rules (default: ForgeDevelopment/probes/points.tsv)")
    parser.add_argument("--json", action="store_true", help="print the merged result as JSON")
    parser.add_argument("--limit", type=int, default=40, help="maximum rows printed per section")
    args = parser.parse_args(argv)

    sessions: dict[str, list[dict[str, Any]]] = {}
    paths: dict[str, str] = {}
    for name, path in args.sessions:
        sessions[name] = list(query.records(query.session_files(path)))
        paths[name] = path
        if not sessions[name]:
            print(f"{name}: {path} holds no records", file=sys.stderr)

    rules, skipped = load_rules(args.points)
    result = merge(sessions, rules, DEFAULT_WINDOW, paths)
    result["skippedRules"] = skipped

    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0

    for session in result["sessions"]:
        index = session.get("index") or {}
        print(f"session {session['name']}: role={session['role']} records={session['records']} "
              f"dropped={index.get('dropped', '?')} budgetReached={index.get('budgetReached', '?')}")
    print(f"pairing rules loaded: {len(rules)}"
          + (f" ({len(skipped)} unreadable)" if skipped else ""))
    for message in skipped:
        print("  ! " + message)

    paired = [pair for pair in result["pairs"] if pair["right"]]
    missing = [pair for pair in result["pairs"] if not pair["right"]]
    print(f"\npairs matched on both machines: {len(paired)}")
    for pair in paired[: args.limit]:
        print(f"  {pair['rule']} key={pair['key']} "
              f"{pair['left']['role']}#{pair['left']['seq']} <-> {pair['right']['role']}#{pair['right']['seq']} "
              f"delta={pair['deltaSnet']}")
    print(f"\nrecorded by one machine only: {len(missing)}")
    for pair in missing[: args.limit]:
        print(f"  {pair['rule']} key={pair['key']} only {pair['left']['role']} seq={pair['left']['seq']} "
              f"snetTime={pair['left']['snetTime']}")

    print(f"\nclient facts the host never recorded (no rule): {len(result['clientOnly'])}")
    for entry in result["clientOnly"][: args.limit]:
        print(f"  {entry['key']} client={entry['client']} host={entry['host']} sampleSeq={entry['sampleSeq']}")
    print(f"host facts the client never recorded (no rule): {len(result['hostOnly'])}")
    for entry in result["hostOnly"][: args.limit]:
        print(f"  {entry['key']} host={entry['host']} client={entry['client']} sampleSeq={entry['sampleSeq']}")
    if result["unpairable"]:
        print(f"\nunpairable records: {len(result['unpairable'])}")
        for entry in result["unpairable"][: args.limit]:
            print(f"  {entry['rule']} session={entry['session']} seq={entry['seq']}: {entry['reason']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
