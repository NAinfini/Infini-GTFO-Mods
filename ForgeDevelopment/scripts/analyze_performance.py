"""Read Infini Tweaks TSV logs; produce an evidence-limited Markdown report.

Usage (repository root): python ForgeDevelopment/scripts/analyze_performance.py SESSION.log --output report.md
All timestamps stay in the log's timezone (normally UTC). No dependencies.
"""
import argparse
from collections import defaultdict
from datetime import datetime
import math
from pathlib import Path
import re
import statistics


def number(row, key, default=0.0):
    try:
        value = float(row.get(key, default))
        return value if math.isfinite(value) else default
    except (TypeError, ValueError):
        return default


def read_log(path):
    rows, malformed = [], 0
    with Path(path).open(encoding="utf-8-sig", errors="replace") as stream:
        for line in stream:
            parts = line.rstrip("\r\n").split("\t")
            try:
                # .NET's round-trip format has seven fractional digits; Python
                # 3.10 accepts only three or six. Retain the original for output.
                timestamp = re.sub(r"\.(\d+)", lambda m: "." + m[1][:6].ljust(6, "0"), parts[0])
                datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
            except ValueError:
                malformed += 1
                continue
            row = dict(part.split("=", 1) for part in parts[1:] if "=" in part)
            if "type" not in row:
                malformed += 1
                continue
            row["time"] = parts[0]
            rows.append(row)
    return rows, malformed


def attempts(rows):
    """State boundaries separate attempts even when the next level has no name."""
    runs, current = [], None
    for row in rows:
        if row["type"] in ("game_state", "frame_summary") and row.get("state") != "InLevel":
            current = None
        if row["type"] != "frame_summary" or row.get("state") != "InLevel":
            continue
        if number(row, "samples") <= 0 or number(row, "average_ms") <= 0:
            continue
        if current is None:
            current = []
            runs.append(current)
        current.append(row)
    return runs


def fps(frames):
    samples = sum(number(row, "samples") for row in frames)
    elapsed = sum(number(row, "samples") * number(row, "average_ms") for row in frames)
    return 1000 * samples / elapsed if elapsed else 0


def report(rows, malformed=0):
    frames = [row for run in attempts(rows) for row in run]
    lines = ["# 性能日志分析", "", "时间沿用日志时区（Z 表示 UTC）。平均 FPS 按帧数和总帧时间计算；窗口分位数不会被冒充为整局分位数。", ""]
    versions = sorted({row["plugin_version"] for row in rows if "plugin_version" in row})
    lines += [f"插件版本：{', '.join(versions) or '日志未记录，无法确认'}。无效／截断行：{malformed}。", ""]
    lines += ["| 局次 | 首末统计窗口 | 平均 FPS | 窗口 FPS 中位数 | <60 FPS 窗口 | 最大单帧 ms |", "|---|---|---:|---:|---:|---:|"]
    for index, run in enumerate(attempts(rows), 1):
        med = statistics.median(1000 / number(row, "average_ms") for row in run)
        below = sum(number(row, "average_ms") > 1000 / 60 for row in run)
        lines.append(f"| {index} | {run[0]['time']} — {run[-1]['time']} | {fps(run):.1f} | {med:.1f} | {below}/{len(run)} | {max(number(r, 'maximum_ms') for r in run):.1f} |")
    if not frames:
        lines += ["", "没有有效的关卡帧摘要，不能评估游戏内帧率。"]
    lines += ["", "## 最慢的一分钟窗口", "", "| 局次 | 时间 | 平均 FPS | 最大单帧 ms | .NET GC0/1/2 次数 |", "|---|---|---:|---:|---|"]
    minutes = []
    for index, run in enumerate(attempts(rows), 1):
        buckets = defaultdict(list)
        for row in run:
            buckets[row["time"][:16]].append(row)
        minutes.extend((index, minute, group) for minute, group in buckets.items())
    for index, minute, group in sorted(minutes, key=lambda entry: fps(entry[2]))[:8]:
        gc = "/".join(str(int(sum(number(r, key) for r in group))) for key in ("gc0", "gc1", "gc2"))
        lines.append(f"| {index} | {minute} | {fps(group):.1f} | {max(number(r, 'maximum_ms') for r in group):.1f} | {gc} |")
    lines += ["", "## 最大长帧所在窗口", "", "| 时间 | 窗口平均 FPS | 最大单帧 ms | CPU/GPU 样本数 | 扫描／原生抓取中 |", "|---|---:|---:|---|---|"]
    for row in sorted(frames, key=lambda r: number(r, "maximum_ms"), reverse=True)[:8]:
        lines.append(f"| {row['time']} | {1000 / number(row, 'average_ms'):.1f} | {number(row, 'maximum_ms'):.1f} | {int(number(row, 'cpu_frame_samples'))}/{int(number(row, 'gpu_frame_samples'))} | {row.get('asset_scan_active', '未记录')}/{row.get('native_capture_active', '未记录')} |")
    lines += ["", "## 诊断自身与采集能力", ""]
    scans = [r for r in rows if r["type"] == "asset_snapshot_end"]
    for row in scans:
        lines.append(f"- 扫描结束 {row['time']}：CPU 总耗时 {number(row, 'scanner_ms'):.1f} ms；最大执行片段 {row.get('maximum_step_ms', '未记录')} ms；状态 {row.get('status', '旧日志未记录')}。")
    for row in sorted((r for r in rows if r['type'] == 'asset_slow_step'), key=lambda r: number(r, 'atomic_ms'), reverse=True)[:8]:
        lines.append(f"- 扫描慢步骤 {row['time']}：{row.get('stage', '未知阶段')}，{number(row, 'atomic_ms'):.3f} ms；2 ms 是协作预算，不是硬上限。")
    for row in (r for r in rows if r['type'] == 'asset_enumeration'):
        lines.append(f"- 原生资源枚举 {row['time']}：{row.get('kind', '未知类别')}，{int(number(row, 'objects'))} 个，{number(row, 'atomic_ms'):.3f} ms。")
    cpu = sum(number(r, "cpu_frame_samples") for r in frames)
    gpu = sum(number(r, "gpu_frame_samples") for r in frames)
    samplers = sum(r["type"] == "unity_sampler" for r in rows)
    lines.append(f"- 关卡 CPU/GPU 帧样本：{int(cpu)}/{int(gpu)}；Unity sampler 记录：{samplers}。")
    undated = sum(number(r, "frame_timing_untimestamped_reads") for r in frames)
    if undated:
        lines.append(f"- {int(undated)} 次 FrameTiming 读取没有完成时间戳，无法保证样本来自不同帧。")
    if not cpu or not gpu:
        lines.append("- CPU/GPU 帧计时不完整，不能据此判断 CPU 或 GPU 瓶颈；低进程总 CPU 占用也不能排除单线程瓶颈。")
    for row in rows:
        if row["type"] in ("unity_capture_unavailable", "unity_capture_error", "frame_timing_status", "asset_snapshot_error"):
            detail = row.get("error", row.get("reason", row.get("available", "未记录")))
            lines.append(f"- {row['time']} {row['type']}：{detail}")
    dropped = max((number(r, "dropped_log_lines") for r in rows), default=0)
    dropped_samples = sum(number(r, "dropped_samples") for r in frames)
    lines.append(f"- 日志丢行计数最大值：{int(dropped)}；关卡分位数采样丢弃：{int(dropped_samples)}。")
    if not any(r["type"] == "session_end" for r in rows):
        lines.append("- 缺少 session_end：会话可能仍在运行或未正常结束。")
    lines += ["", "## 关卡内已计时的模组路径", "", "平均值按调用次数加权，最大值是单次调用耗时；不代表整个模组或 GPU 耗时。", "", "| 路径 | 调用次数 | 平均 ms/次 | 最大 ms/次 |", "|---|---:|---:|---:|"]
    scopes = defaultdict(list)
    markers = []
    in_window = False
    for row in rows:
        if row["type"] == "frame_summary":
            in_window = row.get("state") == "InLevel"
        elif row["type"] == "game_state":
            in_window = False
        elif in_window and row["type"] == "infini_section":
            scopes[row.get("name", "unknown")].append(row)
        elif in_window and row["type"] == "marker_summary":
            markers.append(row)
    for name, values in sorted(scopes.items(), key=lambda entry: sum(number(r, "total_ms") for r in entry[1]), reverse=True):
        calls = sum(number(r, "calls") for r in values)
        if calls > 0:
            average = sum(number(r, "total_ms") for r in values) / calls
            maximum = max(number(r, "maximum_ms") for r in values)
            lines.append(f"| {name} | {int(calls)} | {average:.4f} | {maximum:.3f} |")
    allocated_scopes = [(name, values) for name, values in scopes.items() if any('managed_allocated_bytes' in r for r in values)]
    if allocated_scopes:
        lines += ["", "### 同线程托管分配", "", "累计分配不是存活内存；不含 Unity/IL2CPP 原生分配和其他线程。嵌套路径不能相加。", "", "| 路径 | 累计 MiB | 最大单次 KiB |", "|---|---:|---:|"]
        for name, values in sorted(allocated_scopes, key=lambda entry: sum(number(r, 'managed_allocated_bytes') for r in entry[1]), reverse=True):
            allocated = sum(number(r, 'managed_allocated_bytes') for r in values)
            peak = max(number(r, 'maximum_managed_allocated_bytes') for r in values)
            lines.append(f"| {name} | {allocated / 1048576:.3f} | {peak / 1024:.3f} |")
    lines += ["", "## 标记数量与提交成本", ""]
    if markers:
        lines.append(f"- 已记忆标记：{int(number(markers[0], 'remembered'))} → {int(number(markers[-1], 'remembered'))}；峰值 {int(max(number(r, 'remembered') for r in markers))}；已创建 UI 标记峰值 {int(max(number(r, 'allocated_markers') for r in markers))}。")
        for key, label in (("ui_change_groups", "实际 UI 变更组"), ("label_checks", "标题检查"), ("discovery_checks", "发现候选检查"), ("linecasts", "视线射线")):
            lines.append(f"- {label}：关卡累计 {int(sum(number(r, key) for r in markers))} 次。")
        lines.append("- UI 变更组不是 draw call；MarkerDiscovery、MarkerRefresh 是 ItemMarkers 的嵌套子项，不可相加重复计算。")
    else:
        lines.append("日志未记录标记数量或 UI 提交计数，不能把成本增长直接归因于数量增长。")
    lines += ["", "## 内存与归因边界", ""]
    for index, run in enumerate(attempts(rows), 1):
        values = [number(r, "working_set_mb", -1) for r in run]
        values = [value for value in values if value >= 0]
        if values:
            lines.append(f"- 第 {index} 局工作集：{values[0]:.0f} → {values[-1]:.0f} MiB，峰值 {max(values):.0f} MiB。缓存／关卡差异同样可能导致增长，不能单凭此认定泄漏。")
    lines += ["- GC0/1/2 是 .NET 模组运行时计数，不覆盖 Unity/IL2CPP 的全部垃圾回收。", "- sampler 每 100 ms 抽读，可能漏掉短峰值；模组 scope 可嵌套，不能简单相加为整帧占比。", "- 长帧、异常日志或资源数量与掉帧同期出现只构成相关性；本报告不自动指认其他模组或单个资产为根因。", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = report(*read_log(args.log))
    if args.output:
        args.output.write_text(result, encoding="utf-8")
    else:
        print(result)


if __name__ == "__main__":
    main()
