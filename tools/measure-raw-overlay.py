"""macOS 用。合成 A4 画像を使い、独立プロセスの CLI 時間・最大 RSS・出力量を測る。"""
import argparse
import hashlib
import json
import platform
import re
import statistics
import subprocess
import time
from pathlib import Path

import cv2
import numpy as np

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("cli", type=Path, help="Release の reportdiff.dll")
parser.add_argument("output", type=Path, help="新しい計測用ディレクトリ")
parser.add_argument("--iterations", type=int, default=3)
args = parser.parse_args()
if platform.system() != "Darwin" or not 1 <= args.iterations <= 20:
    parser.error("macOS で 1〜20 回を指定してください。")
args.output.mkdir(parents=True, exist_ok=False)
cli = args.cli.resolve()
width, height, dpi = 2480, 3508, 300


def draw(changed):
    image = np.full((height, width, 3), 255, np.uint8)
    for row in range(30):
        for column in range(20):
            text = "8" if changed and row == 15 and column == 10 else "0"
            point = (round((12 + column * 9.5) * dpi / 25.4), round((14 + row * 9.2) * dpi / 25.4))
            cv2.putText(image, text, point, cv2.FONT_HERSHEY_SIMPLEX, 1.3, (0, 0, 0), 2, cv2.LINE_AA)
    return image


a, b = draw(False), draw(True)
shifted = cv2.warpAffine(b, np.float64([[1, 0, 1], [0, 1, 1]]), (width, height), borderMode=cv2.BORDER_REPLICATE)
input_paths = {}
for name, image in [("a", a), ("changed", b), ("shifted", shifted)]:
    filename = args.output / (name + ".png")
    filename.write_bytes(cv2.imencode(".png", image)[1].tobytes())
    input_paths[name] = filename.resolve()
del a, b, shifted, image


def run(scenario, target, enabled, iteration):
    output = args.output / f"{scenario}-{'on' if enabled else 'off'}-{iteration}"
    command = ["/usr/bin/time", "-l", "dotnet", str(cli), "compare", str(input_paths["a"]), str(target),
               "--out", str(output), "--dpi", str(dpi), "--no-html", "--quiet"]
    if enabled:
        command.append("--raw-overlay")
    start = time.perf_counter()
    result = subprocess.run(command, capture_output=True, text=True)
    elapsed_ms = (time.perf_counter() - start) * 1000
    assert result.returncode == (0 if scenario == "same" else 1), result.stderr
    match = re.search(r"(\d+)\s+maximum resident set size", result.stderr)
    assert match, result.stderr
    doc = json.loads((output / "result.json").read_text())
    doc.pop("generated_at")
    doc["config"]["report"].pop("raw_overlay")
    for page in doc["pages"]:
        page.pop("raw_evidence", None)
    png = {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest()
           for p in output.rglob("*.png") if "_raw_" not in p.name}
    files = [p for p in output.rglob("*") if p.is_file()]
    return {"elapsed_ms": elapsed_ms, "peak_rss_bytes": int(match[1]),
            "output_bytes": sum(p.stat().st_size for p in files),
            "png_files": sum(p.suffix == ".png" for p in files)}, (doc, png)


records = []
for scenario, target in [("same", input_paths["a"]), ("changed", input_paths["changed"]), ("shifted", input_paths["shifted"])]:
    samples = {False: [], True: []}
    expected = None
    for iteration in range(args.iterations + 1):
        # 最初の一組はウォームアップ。順序の影響を減らすため、有効／無効を交互に先行する。
        for enabled in ([False, True] if iteration % 2 == 0 else [True, False]):
            record, existing = run(scenario, target, enabled, iteration)
            if expected is None:
                expected = existing
            assert expected == existing, "有効／無効で既存の出力が変化しました。"
            if iteration:
                samples[enabled].append(record)
    modes = {}
    for enabled in [False, True]:
        values = samples[enabled]
        modes["enabled" if enabled else "disabled"] = {
            "runs": values, "median_ms": statistics.median(v["elapsed_ms"] for v in values),
            "maximum_rss_bytes": max(v["peak_rss_bytes"] for v in values),
            "output_bytes": values[0]["output_bytes"], "png_files": values[0]["png_files"]}
    off, on = modes["disabled"], modes["enabled"]
    records.append({"scenario": scenario, **modes, "added_median_ms": on["median_ms"] - off["median_ms"],
                    "added_output_bytes": on["output_bytes"] - off["output_bytes"],
                    "difference_in_maximum_rss_bytes": on["maximum_rss_bytes"] - off["maximum_rss_bytes"],
                    "existing_outputs_equal": True})
    print(scenario, f"{off['median_ms']:.1f} → {on['median_ms']:.1f} ms", flush=True)
report = {"platform": platform.platform(), "opencv": cv2.__version__, "width": width, "height": height, "dpi": dpi,
          "glyphs": 600, "iterations": args.iterations, "warmup_pairs": 1, "measurements": records,
          "scope": "独立 CLI プロセス。起動・PNG 読み込み・比較・PNG/JSON 保存を含む。HTML と PDF 描画は含まない。RSS は macOS time -l のプロセス最大値。",
          "input_sha256": {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in input_paths.items()}}
(args.output / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
