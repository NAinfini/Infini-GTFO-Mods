import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("analysis", Path(__file__).parents[1] / "scripts/analyze_performance.py")
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)


class PerformanceReportTests(unittest.TestCase):
    def test_weighted_fps_and_state_boundaries(self):
        def frame(samples, ms):
            return {"type": "frame_summary", "state": "InLevel", "samples": samples, "average_ms": ms}
        fast, slow = frame(100, 10), frame(25, 40)
        self.assertEqual(analysis.fps([fast, slow]), 62.5)
        self.assertEqual(analysis.attempts([fast, {"type": "game_state", "state": "Lobby"}, slow]), [[fast], [slow]])

    def test_truncated_input_and_unavailable_channels(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.log"
            path.write_text("2026-09-08T14:00:00.1234567Z\ttype=frame_summary\tstate=InLevel\tsamples=50\taverage_ms=20\tmaximum_ms=80\npartial\n", encoding="utf-8")
            rows, invalid = analysis.read_log(path)
        self.assertEqual(invalid, 1)
        self.assertEqual(len(rows), 1)
        output = analysis.report(rows, invalid)
        self.assertIn("不能据此判断 CPU 或 GPU 瓶颈", output)
        self.assertIn("缺少 session_end", output)
        self.assertNotIn("nan", output)

    def test_invalid_and_empty_frames(self):
        self.assertEqual(analysis.number({"v": "nan"}, "v", -1), -1)
        self.assertEqual(analysis.attempts([{"type": "frame_summary", "state": "InLevel", "samples": 1, "average_ms": 0}]), [])
        self.assertIn("没有有效的关卡帧摘要", analysis.report([]))

    def test_scope_aggregation_excludes_loading(self):
        rows = [
            {"type": "frame_summary", "state": "InLevel", "time": "2026-09-08T14:00:00Z", "samples": 50, "average_ms": 20},
            {"type": "infini_section", "name": "Markers", "calls": 2, "total_ms": 6, "maximum_ms": 5},
            {"type": "infini_section", "name": "Markers", "calls": 1, "total_ms": 6, "maximum_ms": 6},
            {"type": "game_state", "state": "Lobby"},
            {"type": "infini_section", "name": "Loading", "calls": 1, "total_ms": 1000, "maximum_ms": 1000},
        ]
        output = analysis.report(rows)
        self.assertIn("| Markers | 3 | 4.0000 | 6.000 |", output)
        self.assertNotIn("| Loading |", output)

    def test_marker_counts_and_atomic_scan_steps(self):
        timestamp = "2026-09-08T14:00:00Z"
        rows = [
            {"type": "frame_summary", "state": "InLevel", "time": timestamp, "samples": 120, "average_ms": 8.33},
            {"type": "marker_summary", "time": timestamp, "remembered": 40, "allocated_markers": 20, "ui_change_groups": 2, "linecasts": 8},
            {"type": "marker_summary", "time": timestamp, "remembered": 60, "allocated_markers": 30, "ui_change_groups": 3, "linecasts": 12},
            {"type": "game_state", "state": "Lobby"},
            {"type": "marker_summary", "time": timestamp, "remembered": 999, "ui_change_groups": 999},
            {"type": "asset_slow_step", "time": timestamp, "stage": "textures", "atomic_ms": 7.5},
            {"type": "asset_enumeration", "time": timestamp, "kind": "Texture", "objects": 4000, "atomic_ms": 6.1},
        ]
        output = analysis.report(rows)
        self.assertIn("40 → 60；峰值 60", output)
        self.assertIn("实际 UI 变更组：关卡累计 5 次", output)
        self.assertIn("textures，7.500 ms", output)
        self.assertIn("Texture，4000 个，6.100 ms", output)
        self.assertIn("不可相加重复计算", output)

    def test_managed_allocations_are_scoped_and_not_live_memory(self):
        rows = [
            {"type": "frame_summary", "state": "InLevel", "time": "2026-09-08T14:00:00Z", "samples": 50, "average_ms": 20},
            {"type": "infini_section", "name": "Markers", "calls": 2, "total_ms": 6, "maximum_ms": 5,
             "managed_allocated_bytes": 1048576, "maximum_managed_allocated_bytes": 2048},
            {"type": "infini_section", "name": "Markers", "calls": 1, "total_ms": 3, "maximum_ms": 3,
             "managed_allocated_bytes": 1048576, "maximum_managed_allocated_bytes": 1024},
            {"type": "game_state", "state": "Lobby"},
            {"type": "infini_section", "name": "Loading", "managed_allocated_bytes": 9999999},
        ]
        output = analysis.report(rows)
        self.assertIn("| Markers | 2.000 | 2.000 |", output)
        self.assertNotIn("| Loading |", output)
        self.assertIn("同线程托管分配", output)


if __name__ == "__main__":
    unittest.main()
