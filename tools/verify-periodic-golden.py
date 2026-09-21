#!/usr/bin/env python3
"""登録済みの周期ケースが二段目へ入り、別解／欠落の条件を満たすか検証する。"""

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def verify():
    spec = importlib.util.spec_from_file_location("periodic_reference", ROOT / "reference/prototype.py")
    reference = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = reference
    spec.loader.exec_module(reference)
    golden = ROOT / "reference/golden"
    stored = json.loads((golden / "expected.json").read_text(encoding="utf-8"))
    if cv2.__version__ != stored["opencv"]:
        raise ValueError(f"OpenCV {stored['opencv']} を使用してください。")
    generated = {case.id: case for case in reference.build_periodic_cases()}
    positive = {"I10": (2, 0, 1736), "I11": (0, 2, 1736), "I12": (4, 0, 7370)}
    negative = {"D20": 60, "D21": 96}
    records = []
    for case_id in [*positive, *negative]:
        expected = next(c for c in stored["cases"] if c["id"] == case_id)
        parameters = reference.Params(**expected["params"])
        images = [cv2.imdecode(np.frombuffer((golden / f"{case_id}_{side}.png").read_bytes(), np.uint8),
                               cv2.IMREAD_COLOR) for side in ("a", "b")]
        if not all(np.array_equal(image, original) for image, original in
                   zip(images, (generated[case_id].a, generated[case_id].b))):
            raise ValueError(f"{case_id}: 生成画像と保存済み PNG が一致しません。")
        la, lb = [reference.to_lab(image) for image in images]
        av, bv = cv2.blur(la, (3, 3)), cv2.blur(lb, (3, 3))
        ac, bc = reference._contrast(la, 5), reference._contrast(lb, 5)
        radius = round(parameters.px(parameters.max_shift_mm))
        counts = {}
        for dx in range(-radius, radius + 1):
            for dy in range(-radius, radius + 1):
                mask = (np.abs(av - reference._shift(bv, dx, dy)) > parameters.color_threshold
                        + parameters.edge_tolerance * np.maximum(ac, reference._shift(bc, dx, dy))).any(axis=2)
                counts[dx, dy] = int(mask.sum())
        initial = counts[0, 0]
        actual = reference.compare_page(*images, parameters)
        if case_id in positive:
            dx, dy, expected_initial = positive[case_id]
            if initial != expected_initial or initial <= 0 or counts[dx, dy] or counts[-dx, -dy]:
                raise ValueError(f"{case_id}: 初期候補または逆符号のゼロ残差条件を満たしません。")
            if actual.status != "same" or actual.absorbed_groups != 1 or actual.raw_pixels != 0:
                raise ValueError(f"{case_id}: 二段目で 1 group が吸収されていません。")
        elif min(counts.values()) != negative[case_id] or actual.status != "different" \
                or len(actual.clusters) != 1 or actual.absorbed_groups != 0:
            raise ValueError(f"{case_id}: 欠落が検出されていません。")
        for key in ("status", "raw_pixels", "noise_dropped", "absorbed_groups", "max_shift_px"):
            if getattr(actual, key) != expected[key]:
                raise ValueError(f"{case_id}: {key} が期待値と一致しません。")
        if [vars(c) for c in actual.clusters] != expected["clusters"]:
            raise ValueError(f"{case_id}: クラスタが期待値と一致しません。")
        records.append({"id": case_id, "initial_candidate_pixels": initial,
                        "status": actual.status, "raw_pixels": actual.raw_pixels,
                        "absorbed_groups": actual.absorbed_groups, "max_shift_px": actual.max_shift_px,
                        "minimum_whole_page_residual": min(counts.values()),
                        "zero_residual_shifts": [{"dx": dx, "dy": dy} for (dx, dy), n in counts.items() if n == 0],
                        "residuals": [{"dx": dx, "dy": dy, "pixels": n} for (dx, dy), n in counts.items()],
                        "png_sha256": {side: hashlib.sha256((golden / f"{case_id}_{side}.png").read_bytes()).hexdigest()
                                       for side in ("a", "b")}})
    return {"opencv": cv2.__version__, "numpy": np.__version__, "all_passed": True, "cases": records}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, help="検証結果 JSON の保存先")
    args = parser.parse_args()
    result = verify()
    if args.out:
        args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for case in result["cases"]:
        print(f"{case['id']}: initial={case['initial_candidate_pixels']}, "
              f"zero shifts={case['zero_residual_shifts']}, raw={case['raw_pixels']}, "
              f"absorbed={case['absorbed_groups']}")
