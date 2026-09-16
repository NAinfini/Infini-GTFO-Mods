import gzip
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).parents[1] / "scripts"


def load(name: str):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), SCRIPTS / name)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


query = load("rec-query.py")
merge = load("rec-merge.py")

import sys

sys.modules["rec_query"] = query


def record(seq, channel, kind, body, snet=None, role="host", stamp=None, frame=None):
    value = {"v": "forge.rec", "seq": seq, "session": "rec-test", "channel": channel, "kind": kind,
             "t": seq * 10 if stamp is None else stamp, "role": role, "slot": "0", "level": "Rundown/Test"}
    if snet is not None:
        value["snetTime"] = snet
    if frame is not None:
        value["frame"] = frame
    value["body"] = body
    return value


def write_session(directory: Path, rows, gzip_segments=False):
    directory.mkdir(parents=True, exist_ok=True)
    if gzip_segments:
        path = directory / "rec-test-000.jsonl.gz"
        with gzip.open(path, "wt", encoding="utf-8") as handle:
            for row in rows:
                handle.write(json.dumps(row) + "\n")
    else:
        path = directory / "rec-test-000.jsonl"
        path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
    (directory / "index.json").write_text(json.dumps({
        "v": "forge.rec", "session": "rec-test", "role": rows[0]["role"] if rows else "host",
        "dropped": 0, "budgetReached": False, "segments": [{"file": path.name, "bytes": 1, "records": len(rows)}],
    }), encoding="utf-8")
    return directory


class QueryTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp())
        self.rows = [
            record(1, "session", "session.start", {"plugin": "test"}),
            record(2, "tracer", "call", {"type": "LG_SecurityDoor", "method": "OnOpen", "args": [1]}, snet=10.0, frame=5),
            record(3, "tracer", "call", {"type": "LG_SecurityDoor", "method": "OnClose", "args": [2]}, snet=14.0, frame=6),
            # A bookmark is taken from the player's hotkey and carries where they were looking, not a session time.
            record(4, "mark", "bookmark", {"label": "door opened"}, stamp=400),
            record(5, "log", "line", {"message": "shot fired"}),
            record(6, "net", "packet", {"type": "SNet_Packet", "method": "Receive"}, snet=14.2),
        ]
        self.session = write_session(self.root / "rec-host", self.rows)

    def test_channel_and_method_filters(self):
        selected = [row for row in query.records(query.session_files(str(self.session)))
                    if query.matches(row, _args(channel=["tracer"], method="OnOpen"))]
        self.assertEqual([row["seq"] for row in selected], [2])

    def test_time_window_uses_snet_time(self):
        within = [row for row in self.rows
                  if query.matches(row, _args(since=14.0, until=14.2))]
        self.assertEqual([row["seq"] for row in within], [3, 6])

    def test_bookmark_around_window(self):
        marks = [row for row in self.rows if row["channel"] == "mark"]
        picked = query.around(self.rows, marks[0], 0.06)
        self.assertEqual([row["seq"] for row in picked], [4])

    def test_gzip_segment_is_read(self):
        directory = write_session(self.root / "rec-gz", self.rows, gzip_segments=True)
        self.assertEqual(len(list(query.records(query.session_files(str(directory))))), len(self.rows))

    def test_truncated_record_is_reported_and_skipped(self):
        path = self.root / "rec-broken" / "rec-test-000.jsonl"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.rows[0]) + "\n{ this is not json\n", encoding="utf-8")
        rows = list(query.records(query.session_files(str(path.parent))))
        self.assertEqual([row["seq"] for row in rows], [1])

    def test_missing_directory_is_an_error(self):
        with self.assertRaises(SystemExit):
            query.session_files(str(self.root / "nothing"))

    def test_cli_prints_a_table_and_json(self):
        out = _capture(lambda: query.main([str(self.session), "--channel", "tracer", "--method", "OnOpen"]))
        self.assertIn("OnOpen", out)
        self.assertIn("1 of 6 records", out)
        out = _capture(lambda: query.main([str(self.session), "--json", "--channel", "mark"]))
        parsed = json.loads(out)
        self.assertEqual(parsed[0]["body"]["label"], "door opened")

    def test_index_is_printed(self):
        out = _capture(lambda: query.main([str(self.session), "--index"]))
        self.assertEqual(json.loads(out)["session"], "rec-test")


class MergeTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp())
        host_rows = [
            record(1, "session", "session.start", {"plugin": "test"}),
            record(2, "net", "replicator", {"type": "SNet_StateReplicator", "method": "OnStateChange",
                                            "fields": {"m_state": 3}}, snet=10.0),
            record(3, "tracer", "call", {"type": "LG_SecurityDoor", "method": "OnOpen"}, snet=11.0),
        ]
        client_rows = [
            record(1, "session", "session.start", {"plugin": "test"}, role="client"),
            record(2, "net", "replicator", {"type": "SNet_StateReplicator", "method": "OnStateChange",
                                            "fields": {"m_state": 3}}, snet=10.4, role="client"),
            record(4, "tracer", "call", {"type": "LG_DoorButton", "method": "OnPress"}, snet=12.0, role="client"),
        ]
        self.host = write_session(self.root / "rec-host", host_rows)
        self.client = write_session(self.root / "rec-client", client_rows)

    def test_rule_pairs_the_same_state_on_both_machines(self):
        rule = merge.parse_rule("P1", "trace:net/replicator#m_state=fields.m_state")
        self.assertIsNotNone(rule)
        result = merge.merge({"host": _rows(self.host), "client": _rows(self.client)}, [rule], 1.0)
        paired = [pair for pair in result["pairs"] if pair["right"]]
        self.assertEqual(len(paired), 1)
        self.assertEqual(paired[0]["key"], [3])
        self.assertAlmostEqual(paired[0]["deltaSnet"], 0.4, places=3)

    def test_a_recorded_fact_the_peer_never_recorded_is_reported(self):
        rule = merge.parse_rule("P2", "trace:tracer/call#method=method")
        result = merge.merge({"host": _rows(self.host), "client": _rows(self.client)}, [rule], 1.0)
        # Every traced call is one row: the door the host opened has no client partner, and the button the client
        # pressed has no host partner. Which machine recorded the unpaired fact is what the row's own role says.
        missing = [pair for pair in result["pairs"] if not pair["right"]]
        self.assertEqual(sorted(pair["key"][0] for pair in missing), ["OnOpen", "OnPress"])
        self.assertEqual([pair["left"]["role"] for pair in missing].count("host"), 1)
        self.assertEqual([pair["left"]["role"] for pair in missing].count("client"), 1)

    def test_a_rule_that_does_not_discriminate_reports_its_own_limit(self):
        # A rule keyed on the method alone still separates the two calls, because the door and the button are two
        # different methods. A rule keyed on something both machines share would pair them and hide the difference:
        # the rule, not the tool, is what has to be selective.
        rule = merge.parse_rule("P2", "trace:tracer/call#method=method")
        result = merge.merge({"host": _rows(self.host), "client": _rows(self.client)}, [rule], 1.0)
        self.assertEqual([pair for pair in result["pairs"] if pair["right"]], [])
        self.assertEqual(sorted(pair["key"][0] for pair in result["pairs"]), ["OnOpen", "OnPress"])

    def test_a_selection_without_a_key_is_not_a_rule(self):
        self.assertIsNone(merge.parse_rule("P2", "trace:tracer/call"))
        self.assertIsNone(merge.parse_rule("P3", "trace:tracer/call#"))

    def test_without_a_rule_the_client_only_fact_is_reported(self):
        result = merge.merge({"host": _rows(self.host), "client": _rows(self.client)}, [], 1.0)
        self.assertTrue(any(entry["key"][3] == "OnPress" for entry in result["clientOnly"]))
        self.assertTrue(any(entry["key"][3] == "OnOpen" for entry in result["hostOnly"]))

    def test_records_without_a_pairing_key_are_reported(self):
        # A rule whose key path the traced calls do not carry: they match the selection but have no such field, so
        # they are reported as unpairable instead of being paired on a key nobody wrote.
        rule = merge.parse_rule("P3", "trace:tracer/call#m_state=fields.m_state")
        result = merge.merge({"host": _rows(self.host), "client": _rows(self.client)}, [rule], 1.0)
        self.assertEqual(len(result["unpairable"]), 2)
        self.assertEqual(len([pair for pair in result["pairs"] if pair["right"]]), 0)
    def test_rules_come_from_the_points_table(self):
        points = self.root / "points.tsv"
        points.write_text(
            "pointId\tsource\t要点\tlistId\t采集方式\t玩家操作\t需要双机\t需要截图\t需要测试数据块\t" + merge.PAIRING_COLUMN + "\n"
            "P1\tni-hud.md:12\tdoor state visible\tL1\ttrace:net/replicator\topen a door\tyes\tno\tno\t"
            "trace:net/replicator#m_state=fields.m_state\n"
            "P2\tni-hud.md:13\tmanual check\tL1\tmanual:look\tnone\tno\tno\tno\t\n",
            encoding="utf-8")
        rules, skipped = merge.load_rules(str(points))
        self.assertEqual(len(rules), 1)
        self.assertEqual(rules[0].point, "P1")
        self.assertEqual(rules[0].names, ["m_state"])
        self.assertEqual(skipped, [])

    def test_a_missing_points_table_falls_back_with_a_reason(self):
        rules, skipped = merge.load_rules(str(self.root / "absent.tsv"))
        self.assertEqual(rules, [])
        self.assertEqual(len(skipped), 1)

    def test_cli_merges_two_directories(self):
        out = _capture(lambda: merge.main([
            f"host={self.host}", f"client={self.client}",
            "--points", str(self.root / "absent.tsv")]))
        self.assertIn("session host:", out)
        self.assertIn("client facts the host never recorded", out)
        self.assertIn("OnPress", out)


def _args(**overrides):
    class Namespace:
        pass

    values = dict(channel=None, kind=None, role=None, type=None, method=None, contains=None, since=None,
                  until=None, from_ms=None, to_ms=None)
    values.update(overrides)
    namespace = Namespace()
    for key, value in values.items():
        setattr(namespace, key, value)
    return namespace


def _rows(directory):
    return list(query.records(query.session_files(str(directory))))


def _capture(call):
    import io
    from contextlib import redirect_stdout

    buffer = io.StringIO()
    with redirect_stdout(buffer):
        call()
    return buffer.getvalue()


if __name__ == "__main__":
    unittest.main()
