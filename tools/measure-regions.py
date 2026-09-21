#!/usr/bin/env python3
"""A4 300dpi で基準のみ／2設定／3設定の CLI 時間と最大 RSS を測定する（macOS）。"""
import argparse
import json
import platform
from pathlib import Path
import re
import statistics
import subprocess
import time
import cv2
import numpy as np

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("output", type=Path)
parser.add_argument("--cli", type=Path, default=Path("src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll"))
parser.add_argument("--iterations", type=int, default=3)
args = parser.parse_args()
if platform.system() != "Darwin" or args.iterations < 1:
    parser.error("macOS で 1 回以上の測定を指定してください。")
args.output.mkdir(parents=True, exist_ok=False)
width, height, dpi = 2480, 3508, 300
a = np.full((height, width, 3), 255, np.uint8)
b = a.copy()
for row in range(30):
    for column in range(20):
        point = (round((12 + column * 9.5) * dpi / 25.4), round((14 + row * 9.2) * dpi / 25.4))
        cv2.putText(a, "0", point, cv2.FONT_HERSHEY_SIMPLEX, 1.3, (0, 0, 0), 2, cv2.LINE_AA)
        cv2.putText(b, "8" if row == 15 and column == 10 else "0", point, cv2.FONT_HERSHEY_SIMPLEX, 1.3, (0, 0, 0), 2, cv2.LINE_AA)
shifted = cv2.warpAffine(b, np.float64([[1, 0, 1], [0, 1, 1]]), (width, height), borderMode=cv2.BORDER_REPLICATE)
for name, image in [("a", a), ("changed", b), ("shifted", shifted)]:
    (args.output / (name + ".png")).write_bytes(cv2.imencode(".png", image)[1].tobytes())
del a, b, shifted, image
regions = [{"name": "左の厳密比較", "page": "all", "x": 0, "y": 0, "w": 100, "h": 297, "profile": "strict"},
           {"name": "右の位置ずれ許容", "page": "all", "x": 110, "y": 0, "w": 100, "h": 297, "profile": "loose"}]
for count in [1, 2, 3]:
    (args.output / f"settings-{count}.yaml").write_text(json.dumps({"regions": regions[:count - 1]}, ensure_ascii=False))


def run(scenario, count, iteration):
    output = args.output / f"{scenario}-{count}-{iteration}"
    command = ["/usr/bin/time", "-l", "dotnet", str(args.cli.resolve()), "compare", str((args.output / "a.png").resolve()),
               str((args.output / (scenario + ".png")).resolve()), "--out", str(output), "--config",
               str(args.output / f"settings-{count}.yaml"), "--no-html", "--quiet"]
    started = time.perf_counter()
    process = subprocess.run(command, capture_output=True, text=True)
    elapsed = (time.perf_counter() - started) * 1000
    assert process.returncode == 1, process.stderr
    rss = re.search(r"(\d+)\s+maximum resident set size", process.stderr)
    assert rss, process.stderr
    report = json.loads((output / "result.json").read_text())
    page = report["pages"][0]
    assert len(page.get("regions", {}).get("runs", [0])) == count
    return {"elapsed_ms": elapsed, "peak_rss_bytes": int(rss[1]), "raw_pixels": page["raw_pixels"],
            "clusters": len(page["clusters"]), "suppressed_pixels": page.get("regions", {}).get("suppressed_pixels", 0),
            "output_bytes": sum(p.stat().st_size for p in output.rglob("*") if p.is_file())}


records = []
for scenario in ["changed", "shifted"]:
    samples = {count: [] for count in [1, 2, 3]}
    for iteration in range(args.iterations + 1):
        for count in ([1, 2, 3] if iteration % 2 == 0 else [3, 2, 1]):
            result = run(scenario, count, iteration)
            if iteration:
                samples[count].append(result)
    for count, values in samples.items():
        item = {"scenario": scenario, "settings": count, "runs": values,
                "median_ms": statistics.median(v["elapsed_ms"] for v in values),
                "maximum_rss_bytes": max(v["peak_rss_bytes"] for v in values)}
        records.append(item)
        print(scenario, count, round(item["median_ms"], 1), item["maximum_rss_bytes"], flush=True)
(args.output / "verification.json").write_text(json.dumps({"platform": platform.platform(), "opencv": cv2.__version__,
    "width": width, "height": height, "dpi": dpi, "glyphs": 600, "iterations": args.iterations, "warmups": 1,
    "scope": "独立 CLI プロセスの起動・PNG 読み込み・比較・PNG/JSON 保存。HTML・PDF 描画・raw evidence は含まない。領域枠の描画は含む。設定で結果も変わる。RSS は macOS time -l。",
    "measurements": records}, ensure_ascii=False, indent=2) + "\n")
