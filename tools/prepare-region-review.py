#!/usr/bin/env python3
"""合成帳票で領域表示・抑制・監査のブラウザ確認用レポートを作る。"""
import argparse
import json
from pathlib import Path
import subprocess
import cv2
import numpy as np

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("output", type=Path)
parser.add_argument("--cli", type=Path, default=Path("src/ReportDiff.Cli/bin/Debug/net10.0/reportdiff.dll"))
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
a = np.full((900, 800, 3), 255, np.uint8)
b = a.copy()
for image in [a, b]:
    cv2.putText(image, "REPORT 2026", (45, 55), cv2.FONT_HERSHEY_SIMPLEX, 1.1, (45, 45, 45), 2, cv2.LINE_AA)
    for y in [80, 170, 280, 390, 500, 610, 750]:
        cv2.line(image, (40, y), (760, y), (185, 185, 185), 1)
for y in [135, 235, 345, 455, 565]:
    for image, value in [(a, "12345"), (b, "12348" if y == 345 else "12345")]:
        cv2.putText(image, value, (75, y), cv2.FONT_HERSHEY_SIMPLEX, 1.1, (30, 30, 30), 2, cv2.LINE_AA)
    cv2.putText(a, "TOTAL 1200", (440, y), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (25, 25, 25), 2, cv2.LINE_AA)
    cv2.putText(b, "TOTAL 1200", (441, y), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (25, 25, 25), 2, cv2.LINE_AA)
cv2.rectangle(b, (75, 640), (220, 665), (0, 0, 0), -1)
cv2.rectangle(a, (620, 675), (720, 705), (0, 0, 0), -1)
cv2.rectangle(b, (630, 675), (740, 705), (0, 0, 0), -1)
for image, x in [(a, 80), (b, 82)]:
    cv2.putText(image, "FOOTER  /  PAGE 1", (x, 820), cv2.FONT_HERSHEY_SIMPLEX, 1, (25, 25, 25), 2, cv2.LINE_AA)
for name, image in [("a", a), ("b", b)]:
    (args.output / (name + ".png")).write_bytes(cv2.imencode(".png", image)[1].tobytes())
config = {"dpi": 254, "diff": {"max_shift_mm": 0, "edge_tolerance": 0}, "report": {"raw_overlay": True}, "regions": [
    {"name": "明細欄の厳密な比較", "page": "all", "x": 4, "y": 9, "w": 30, "h": 64, "profile": "strict"},
    {"name": "合計欄の小さな位置ずれを許容", "page": "all", "x": 41, "y": 9, "w": 35, "h": 53, "profile": "loose"},
    {"name": "ロゴを除外", "page": "all", "x": 60, "y": 65, "w": 16, "h": 8, "mode": "exclude"},
    {"name": "フッターの位置ずれを許容", "page": "all", "x": 4, "y": 77, "w": 72, "h": 10, "profile": "loose"},
    {"name": "ページ外の領域", "page": "all", "x": 100, "y": 100, "w": 5, "h": 5, "profile": "strict"}
]}
(args.output / "settings.yaml").write_text(json.dumps(config, ensure_ascii=False, indent=2))
records = []
for mode in ["regions", "audit"]:
    output = args.output / mode
    result = subprocess.run(["dotnet", str(args.cli.resolve()), "compare", str((args.output / "a.png").resolve()),
        str((args.output / "b.png").resolve()), "--out", str(output), "--config", str(args.output / "settings.yaml"),
        *(["--no-regions"] if mode == "audit" else [])], capture_output=True, text=True)
    assert result.returncode == 1, result.stderr
    report = json.loads((output / "result.json").read_text())
    page = report["pages"][0]
    records.append({"mode": mode, "summary": report["summary"], "regions": page.get("regions")})
    if mode == "regions":
        assert page["regions"]["suppressed_pixels"] > 0
        assert page["regions"]["excluded_pixels"] > 0
first = json.loads((args.output / "regions/result.json").read_text())["pages"][0]["raw_evidence"]["overlay"]
second = json.loads((args.output / "audit/result.json").read_text())["pages"][0]["raw_evidence"]["overlay"]
assert (args.output / "regions" / first).read_bytes() == (args.output / "audit" / second).read_bytes()
(args.output / "verification.json").write_text(json.dumps({"raw_png_equal": True, "records": records}, ensure_ascii=False, indent=2) + "\n")
