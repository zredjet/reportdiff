#!/usr/bin/env python3
"""T3-4b 前の保存済み JSON / PNG / 終了コードと、現在の領域なし出力を照合する。"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("baseline", type=Path, help="bbf14cf の manifest.json")
parser.add_argument("output", type=Path)
parser.add_argument("--cli", type=Path, default=Path("src/ReportDiff.Cli/bin/Debug/net10.0/reportdiff.dll"))
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
baseline = json.loads(args.baseline.read_text())
results = []
for case in baseline["cases"]:
    name = case["id"]
    output = args.output / name
    command = ["dotnet", str(args.cli.resolve()), "compare", case["json"]["inputs"]["a"]["path"],
               case["json"]["inputs"]["b"]["path"], "--out", str(output), "--config",
               str(args.baseline.parent / (name + ".yaml")), "--save-all-pages", "--raw-overlay"]
    process = subprocess.run(command, capture_output=True, text=True)
    assert process.returncode == case["exit_code"], (name, process.stderr)
    report = json.loads((output / "result.json").read_text())
    report.pop("generated_at")
    assert report == case["json"], f"{name}: JSON 不一致"
    png = {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest() for p in output.rglob("*.png")}
    assert png == case["png_sha256"], f"{name}: PNG 不一致"
    results.append({"id": name, "exit_code": process.returncode, "json_equal": True, "png_equal": True, "png_count": len(png)})
    print(name, flush=True)
record = {"baseline": baseline["baseline"], "excluded_json_fields": ["generated_at"], "cases": results,
          "png_count": sum(r["png_count"] for r in results)}
(args.output / "verification.json").write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n")
