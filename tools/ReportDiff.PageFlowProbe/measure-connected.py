#!/usr/bin/env python3
"""固定PDFのCLI全工程を別プロセスで計測する。A4実帳票の性能受け入れではない。"""
import json
import os
from pathlib import Path
import statistics
import struct
import subprocess
import sys
import time

root = Path(__file__).resolve().parents[2]
output = Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=False)
if not hasattr(os, "wait4"):
    raise RuntimeError("RSS計測にはmacOSまたはLinuxが必要です。")
cli = root / "src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll"
fixtures = root / "tests/ReportDiff.Tests/Fixtures/page-flow"
records = []
for scenario in ["R10", "R11", "chain3", "flow-number-change"]:
    runs = []
    # 有効／無効を交互に測り、すべて描画・比較・保存・HTMLを含める。
    for attempt in range(3):
        for enabled in [False, True]:
            name = f"{scenario}-{attempt}-{str(enabled).lower()}"
            directory = output / name
            config = output / f"{name}.yaml"
            config.write_text(f"rows: {{enabled: true, carry_enabled: {str(enabled).lower()}}}\nreport: {{raw_overlay: true}}\n")
            command = ["dotnet", str(cli), "compare", str(fixtures/scenario/"a.pdf"), str(fixtures/scenario/"b.pdf"),
                       "--config", str(config), "--out", str(directory), "--quiet"]
            start = time.perf_counter()
            with (output/f"{name}.log").open("w") as log:
                child = subprocess.Popen(command, cwd=root, stdout=log, stderr=subprocess.STDOUT)
                _, status, usage = os.wait4(child.pid, 0)
            elapsed = time.perf_counter() - start
            child.returncode = os.waitstatus_to_exitcode(status)
            if child.returncode != 1:
                raise RuntimeError(f"計測に失敗: {name}/{child.returncode}")
            report = json.loads((directory/"result.json").read_text())
            png = directory / report["pages"][0]["raw_evidence"]["a"]["image"]
            dimensions = struct.unpack(">II", png.read_bytes()[16:24])
            files = [p for p in directory.rglob("*") if p.is_file()]
            runs.append(dict(attempt=attempt, carry_enabled=enabled, elapsed_seconds=elapsed,
                             peak_rss_bytes=usage.ru_maxrss * (1 if sys.platform == "darwin" else 1024),
                             output_bytes=sum(p.stat().st_size for p in files), files=len(files),
                             original_size_px=dimensions, selected_pages=len(report["pages"]),
                             status=report.get("page_flow", {}).get("status"),
                             difference_count=report["summary"]["difference_count"],
                             aggregated=report["summary"]["aggregated_difference_count"]))
    records.append(dict(scenario=scenario, summaries=[
        dict(carry_enabled=enabled,
             median_seconds=statistics.median(r["elapsed_seconds"] for r in runs if r["carry_enabled"] == enabled),
             max_rss_bytes=max(r["peak_rss_bytes"] for r in runs if r["carry_enabled"] == enabled),
             median_output_bytes=statistics.median(r["output_bytes"] for r in runs if r["carry_enabled"] == enabled))
        for enabled in [False, True]], runs=runs))
    print(scenario, "計測完了", flush=True)
(output/"summary.json").write_text(json.dumps(dict(platform=sys.platform, processes=24, repeats=3,
    rss_source="wait4.ru_maxrss", full_cli_pipeline=True, raw_overlay=True, html=True,
    a4_real_document_acceptance=False, records=records), ensure_ascii=False, indent=2)+"\n")
