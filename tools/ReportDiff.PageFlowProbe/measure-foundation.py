"""記述保持・候補上限を各3子プロセスで計測する。PDF描画・比較の性能検証ではない。"""
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
output = Path(sys.argv[1]).resolve()
if output.exists() and any(output.iterdir()):
    raise ValueError("出力先が空ではありません。")
if not hasattr(os, "wait4"):
    raise RuntimeError("子プロセスごとのRSS計測にはmacOSまたはLinuxが必要です。")
output.mkdir(parents=True, exist_ok=True)
scenarios = ["a4-2", "a4-16", "a4-30", "a4-31", "dense-16", "dense-17", "page-128", "page-129",
             "metadata-127", "metadata-128", "candidates-128", "candidates-129"]
records = []
for scenario in scenarios:
    runs = []
    for attempt in range(3):
        path = output / f"{scenario}-{attempt}.json"
        command = ["dotnet", str(root / "tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll"),
                   "--foundation-measure", scenario, str(path)]
        child = subprocess.Popen(command, cwd=root, stdout=subprocess.DEVNULL)
        _, status, usage = os.wait4(child.pid, 0)
        child.returncode = os.waitstatus_to_exitcode(status)
        if child.returncode:
            raise RuntimeError(f"計測に失敗: {scenario}/{attempt}/{child.returncode}")
        run = json.loads(path.read_text())
        run["process_peak_rss_bytes"] = usage.ru_maxrss * (1 if sys.platform == "darwin" else 1024)
        assert run["process_peak_rss_bytes"] > 0
        run["rss_source"] = "wait4.ru_maxrss"
        path.write_text(json.dumps(run, indent=2) + "\n")
        runs.append(run)
    accepted = scenario not in {"a4-31", "dense-17", "page-129", "metadata-128", "candidates-129"}
    assert all(r["accepted"] == accepted for r in runs), scenario
    records.append(dict(scenario=scenario, accepted=accepted, processes=3,
                        median_ms=statistics.median(r["elapsed_ms"] for r in runs),
                        max_rss_bytes=max(r["process_peak_rss_bytes"] for r in runs),
                        median_retained_bytes=statistics.median(r.get("managed_retained_delta_bytes", 0) for r in runs)
                        if not scenario.startswith("candidates-") else None,
                        failure_reasons=sorted({r.get("failure_reason") for r in runs if r.get("failure_reason")}),
                        runs=runs))
    print(scenario, "採用" if accepted else "見送り", flush=True)
(output / "summary.json").write_text(json.dumps(dict(platform=sys.platform, scenarios=records,
    processes=36, pdf_rendering_included=False, image_comparison_included=False,
    product_runtime_acceptance=False), indent=2) + "\n")
