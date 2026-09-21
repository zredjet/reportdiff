#!/usr/bin/env python3
"""全ゴールデンを補正無効／有効で実行し、PageMap 導入前後の JSON・PNG・HTML を照合する。"""
import argparse
from datetime import datetime
import hashlib
import html
import json
from pathlib import Path
import re
import subprocess


def digest(data):
    return hashlib.sha256(data).hexdigest()


def snapshot(output):
    files = {}
    dates = []

    def replace_date(match):
        dates.append(json.loads(match[2]))
        return match[1] + '"GENERATED_AT"'

    for path in output.rglob("*.json"):
        normalized = re.sub(r'("generated_at"\s*:\s*)("[^"\n]*")', replace_date, path.read_text())
        files[str(path.relative_to(output))] = digest(normalized.encode())
    for path in output.rglob("*"):
        if path.suffix == ".png":
            files[str(path.relative_to(output))] = digest(path.read_bytes())
        elif path.suffix == ".html":
            content = path.read_text()
            for date in dates:
                value = datetime.fromisoformat(date)
                offset = value.strftime("%z")
                visible = value.strftime("%Y-%m-%d %H:%M:%S ") + offset[:3] + ":" + offset[3:]
                parts = re.fullmatch(r"(.*T\d{2}:\d{2}:\d{2})(?:\.(\d+))?([+-]\d{2}:\d{2})", date)
                assert parts, date
                roundtrip = parts[1] + "." + (parts[2] or "").ljust(7, "0") + parts[3]
                for text in [roundtrip, date, visible]:
                    content = content.replace(text, "GENERATED_AT")
                    content = content.replace(html.escape(text).replace("+", "&#x2B;"), "GENERATED_AT")
            files[str(path.relative_to(output))] = digest(content.encode())
    return files


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline_cli", type=Path)
    parser.add_argument("current_cli", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--extra-cases", type=Path, help="[{id, args: CLI引数（--outを除く）}] の追加検証")
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    golden = Path(__file__).resolve().parent.parent / "reference/golden"
    cases = []
    for case in json.loads((golden / "expected.json").read_text())["cases"]:
        p = case["params"]
        for align in [False, True]:
            name = case["id"] + ("-align" if align else "-disabled")
            config = {"dpi": p["dpi"], "diff": {k: p[k] for k in ["max_shift_mm", "color_threshold", "edge_tolerance"]},
                      "ink": {"background_radius_mm": p["ink_background_radius_mm"], "contrast_threshold": p["ink_contrast_threshold"]},
                      "cluster": {k: p[k] for k in ["merge_x_mm", "merge_y_mm", "min_pixels", "max_diff_ratio", "reading_band_mm"]},
                      "exclude": [dict(zip(["x", "y", "w", "h"], r), page="all") for r in p["exclude_mm"]],
                      "align": {"enabled": align}}
            config_path = root / (name + ".yaml")
            config_path.write_text(json.dumps(config))
            cases.append({"id": name, "args": ["compare", str(golden / (case["id"] + "_a.png")),
                str(golden / (case["id"] + "_b.png")), "--config", str(config_path), "--save-all-pages", "--raw-overlay"]})
    if args.extra_cases:
        cases.extend(json.loads(args.extra_cases.read_text()))
    assert len({c["id"] for c in cases}) == len(cases)
    records = []
    for case in cases:
        snapshots = []
        codes = []
        for label, cli in [("before", args.baseline_cli), ("after", args.current_cli)]:
            output = root / label / case["id"]
            run = subprocess.run(["dotnet", str(cli.resolve()), *case["args"], "--out", str(output)], capture_output=True, text=True)
            assert run.returncode in [0, 1], (case["id"], label, run.returncode, run.stderr)
            snapshots.append(snapshot(output))
            codes.append(run.returncode)
        assert codes[0] == codes[1], (case["id"], codes)
        assert snapshots[0] == snapshots[1], (case["id"], [k for k in snapshots[0].keys() | snapshots[1].keys()
            if snapshots[0].get(k) != snapshots[1].get(k)])
        records.append({**case, "exit_code": codes[0], "files_sha256": snapshots[0]})
        print(case["id"], flush=True)
    record = {"baseline_cli_sha256": digest(args.baseline_cli.read_bytes()), "current_cli_sha256": digest(args.current_cli.read_bytes()),
              "excluded_fields": ["generated_at (JSON and its HTML representations)"], "cases": records,
              "file_count": sum(len(c["files_sha256"]) for c in records)}
    (root / "verification.json").write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    main()
