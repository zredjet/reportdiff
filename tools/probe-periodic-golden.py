#!/usr/bin/env python3
"""T2-8 の図形候補を予備検証する。正本のゴールデンや比較式は更新しない。"""

import argparse
from dataclasses import replace
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def load_reference():
    spec = importlib.util.spec_from_file_location("periodic_reference", ROOT / "reference/prototype.py")
    reference = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = reference
    spec.loader.exec_module(reference)
    return reference


def rectangle(reference, x, y, width, height):
    # 線端の丸みを含めず、1 倍画像で指定した幅・高さの黒画素を作る。
    # Scene の 4 倍描画と INTER_AREA 縮小を通し、整数位相を固定する。
    def draw(image, ox, oy):
        scale = reference.SS
        image[y * scale + oy:(y + height) * scale + oy,
              x * scale + ox:(x + width) * scale + ox] = 0
    return draw


def dashed(reference, period=4, length=2, missing=None):
    scene = reference.Scene()
    for x in range(240, 1081, period):
        if x != missing:
            scene.add(rectangle(reference, x, 420, length, 1))
    return scene


def candidates(reference):
    normal = reference.Params()
    loose = replace(normal, max_shift_mm=0.30)
    dots = dashed(reference)
    yield "I06", dots.render(), dots.render(shift_px=(1, 0)), normal, "same", 0
    yield "I07", dots.render(), dots.render(shift_px=(2, 0)), normal, "same", 0
    grid = reference.Scene()
    for y in range(360, 601, 4):
        for x in range(240, 1081, 4):
            grid.add(rectangle(reference, x, y, 1, 1))
    yield "I08", grid.render(), grid.render(shift_px=(2, 0)), normal, "same", 0
    wide = dashed(reference, period=8, length=4)
    yield "I09", wide.render(), wide.render(shift_px=(4, 0)), loose, "same", 0
    yield "D16", dots.render(), dashed(reference, missing=660).render(), normal, "different", 1
    yield "D17", dots.render(), dashed(reference, period=5).render(), normal, "different", None
    single = reference.Scene().add(rectangle(reference, 240, 420, 842, 1))
    double = reference.Scene().add(rectangle(reference, 240, 419, 842, 1))
    double.add(rectangle(reference, 240, 421, 842, 1))
    yield "D18", single.render(), double.render(), normal, "different", 1
    thick = reference.Scene().add(rectangle(reference, 240, 420, 842, 2))
    thick_double = reference.Scene().add(rectangle(reference, 240, 418, 842, 2))
    thick_double.add(rectangle(reference, 240, 422, 842, 2))
    yield "D19", thick.render(), thick_double.render(), loose, "different", 1


def stats(result):
    return {name: getattr(result, name) for name in
            ("status", "raw_pixels", "noise_dropped", "absorbed_groups", "max_shift_px")} | {
                "clusters": [vars(cluster) for cluster in result.clusters]}


def first_stage_evidence(reference, a, b, parameters):
    la, lb = reference.to_lab(a), reference.to_lab(b)
    delta = np.abs(cv2.blur(la, (3, 3)) - cv2.blur(lb, (3, 3)))
    threshold = parameters.color_threshold + parameters.edge_tolerance * np.maximum(
        reference._contrast(la, 5), reference._contrast(lb, 5))
    changed = delta[:, :, 0] > 0
    return {"input_changed_pixels": int(np.any(a != b, axis=2).sum()),
            "max_blurred_L_difference": float(delta[:, :, 0].max()),
            "minimum_threshold_at_changed_L": float(threshold[:, :, 0][changed].min()),
            "maximum_threshold_at_changed_L": float(threshold[:, :, 0][changed].max()),
            "candidate_pixels": int(np.any(delta > threshold, axis=2).sum())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--cli", type=Path, required=True, help="ビルド済み reportdiff.dll")
    args = parser.parse_args()
    output = args.out.resolve()
    output.mkdir(parents=True, exist_ok=False)
    reference = load_reference()
    expected_version = json.loads((ROOT / "reference/golden/expected.json").read_text(encoding="utf-8"))["opencv"]
    if cv2.__version__ != expected_version:
        raise ValueError(f"OpenCV {expected_version} を使用してください（現在 {cv2.__version__}）。")
    results = []
    for case_id, a, b, parameters, status, count in candidates(reference):
        files = []
        hashes = {}
        for side, image in (("a", a), ("b", b)):
            name = output / f"{case_id}_{side}.png"
            ok, encoded = cv2.imencode(".png", image)
            if not ok:
                raise RuntimeError(f"{case_id}: PNG を保存できません。")
            name.write_bytes(encoded.tobytes())
            files.append(name)
            hashes[side] = hashlib.sha256(name.read_bytes()).hexdigest()
        actual = reference.compare_page(a, b, parameters)
        initial = reference.tolerant_diff(a, b, replace(parameters, max_shift_mm=0))
        zero_shift_candidates = int(initial.sum())
        zero_alternatives = {}
        if case_id in ("I07", "I09"):
            shift = 2 if case_id == "I07" else 4
            for dx in (-shift, shift):
                zero_alternatives[str(dx)] = int(reference.tolerant_diff(
                    a, reference._shift(b, dx, 0), replace(parameters, max_shift_mm=0)).sum())
        cli_output = output / f"{case_id}-cli"
        command = ["dotnet", str(args.cli.resolve()), "compare", *(str(file) for file in files),
                   "--out", str(cli_output), "--no-html", "--quiet"]
        if case_id in ("I09", "D19"):
            command += ["--profile", "loose"]
        process = subprocess.run(command, text=True, encoding="utf-8", capture_output=True, cwd=ROOT)
        if process.returncode not in (0, 1):
            raise RuntimeError(f"{case_id}: CLI が失敗しました: {process.stderr}")
        report = json.loads((cli_output / "result.json").read_text(encoding="utf-8"))
        page = report["pages"][0]
        observed = stats(actual)
        core_keys = ("status", "raw_pixels", "noise_dropped", "absorbed_groups", "max_shift_px")
        matched = all(page[key] == observed[key] for key in core_keys)
        csharp_boxes = [{"id": c["id"], "x": c["bbox_px"]["x"], "y": c["bbox_px"]["y"],
                        "w": c["bbox_px"]["w"], "h": c["bbox_px"]["h"], "pixels": c["pixels"],
                        "fill_ratio": c["fill_ratio"]} for c in page["clusters"]]
        matched = matched and observed["clusters"] == csharp_boxes
        passed = actual.status == status and (count is None or len(actual.clusters) == count)
        entry = {"id": case_id, "expected_status": status, "expected_clusters": count,
                 "params": vars(parameters), "observed": observed, "expectation_met": passed,
                 "initial_candidate_pixels": zero_shift_candidates,
                 "whole_page_residual_at_alternative_shift": zero_alternatives,
                 "csharp_exact_match": matched, "cli_exit_code": process.returncode,
                 "png_sha256": hashes}
        if case_id in ("D16", "D17"):
            entry["first_stage_evidence"] = first_stage_evidence(reference, a, b, parameters)
        results.append(entry)
        print(f"{case_id}: {'OK' if passed else 'NG'} {actual.status}, clusters={len(actual.clusters)}, "
              f"raw={actual.raw_pixels}, initial={zero_shift_candidates}, absorbed={actual.absorbed_groups}, "
              f"C#一致={matched}", flush=True)
    summary = {"opencv": cv2.__version__, "numpy": np.__version__, "candidate_count": len(results),
               "expectation_failures": [r["id"] for r in results if not r["expectation_met"]],
               "all_csharp_exact_match": all(r["csharp_exact_match"] for r in results), "cases": results}
    (output / "probe.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 1 if summary["expectation_failures"] or not summary["all_csharp_exact_match"] else 0


if __name__ == "__main__":
    sys.exit(main())
