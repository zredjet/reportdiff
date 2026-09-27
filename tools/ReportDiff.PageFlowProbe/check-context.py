"""全ページ置換の試行を既存参照実装で検証。自動送りの採用は行わない。"""
import importlib.util
import json
from pathlib import Path
import sys

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
spec = importlib.util.spec_from_file_location("context_reference", root / "reference/prototype.py")
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)
cases = json.loads((folder / "context.json").read_text())
checked = []


def read(path, flags=cv2.IMREAD_COLOR):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), flags)


def area(rect):
    return (slice(rect["y"], rect["y"] + rect["h"]), slice(rect["x"], rect["x"] + rect["w"]))


for case in cases:
    directory = folder / case["id"]
    images = {name: read(directory / name) for name in ("a.png", "b.png", "a-replaced.png", "b-replaced.png", "crop-a.png", "crop-b.png")}
    a, b = images["a.png"], images["b.png"]
    ra, rb = area(case["band_a"]), area(case["band_b"])
    expected_a, expected_b = a.copy(), b.copy()
    expected_a[ra], expected_b[rb] = b[rb], a[ra]
    assert np.array_equal(images["a-replaced.png"], expected_a), (case["id"], "replacement_a")
    assert np.array_equal(images["b-replaced.png"], expected_b), (case["id"], "replacement_b")
    assert np.array_equal(images["crop-a.png"], a[ra]) and np.array_equal(images["crop-b.png"], b[rb])
    exact = np.array_equal(a[ra], b[rb])
    assert exact == case["exact_band_pixels"]
    for m in case["measurements"]:
        p = reference.Params()
        if m["profile"] == "strict":
            p.max_shift_mm, p.edge_tolerance = 0, 0
        elif m["profile"] == "loose":
            p.max_shift_mm = .30
        result = reference.compare_page(images[m["input_a"]], images[m["input_b"]], p)
        mask = read(directory / m["mask"], cv2.IMREAD_GRAYSCALE) > 0
        assert np.array_equal(result.raw_mask, mask), (case["id"], m["mask"], "raw_mask")
        for key, value in dict(raw=result.raw_pixels, status=result.status, clusters=len(result.clusters),
                               noise_dropped=result.noise_dropped, absorbed_groups=result.absorbed_groups,
                               max_shift_px=result.max_shift_px).items():
            assert m[key] == value, (case["id"], m["mask"], key, m[key], value)
        checked.append(dict(case=case["id"], mask=m["mask"], raw=result.raw_pixels, csharp_mask_equal=True))
    for decision in case["decisions"]:
        endpoints = [m for m in case["measurements"] if m["profile"] == decision["profile"] and m["role"].startswith("endpoint-")]
        assert len(endpoints) == 4
        all_zero = all(m["raw"] == 0 for m in endpoints)
        assert all_zero == decision["all_endpoint_raw_zero"]
        assert (exact and all_zero) == decision["exact_band_evidence"] == decision["expected_exact_band_evidence"]
    print(case["id"], "全ページ置換・参照マスク一致", flush=True)

by_id = {c["id"]: c for c in cases}


def measurements(name, profile, role):
    return [m for m in by_id[name]["measurements"] if m["profile"] == profile and m["role"] == role]


# 二段目が実際に動く対照。切り出しは吸収、実際の罫線全体は残る。
for profile in ("normal", "loose"):
    assert all(m["raw"] == 0 and m["absorbed_groups"] > 0 for m in measurements("long-connected-rule", profile, "crop-control"))
    assert all(m["raw"] > 0 for role in ("endpoint-a", "endpoint-b") for m in measurements("long-connected-rule", profile, role))
    assert all(m["raw"] == 0 and m["absorbed_groups"] > 0 for role in ("endpoint-a", "endpoint-b") for m in measurements("isolated-shift", profile, role))
assert all(m["raw"] > 0 for role in ("endpoint-a", "endpoint-b") for m in measurements("isolated-shift", "strict", role))

# 薄色変更を境界で二分すると許容内になる。四比較0だけを採用条件にできない。
for name in ("boundary-pale--2", "boundary-pale-198"):
    for profile in ("normal", "loose"):
        assert all(m["raw"] == 44 for m in measurements(name, profile, "whole-change-control"))
        assert all(m["raw"] == 0 for role in ("endpoint-a", "endpoint-b", "outside-only-control") for m in measurements(name, profile, role))
        assert not next(d for d in by_id[name]["decisions"] if d["profile"] == profile)["exact_band_evidence"]

# クラスタ0でも生差分1を同一性に使わない。
assert all(m["raw"] == 1 and m["clusters"] == 0 for role in ("endpoint-a", "endpoint-b") for m in measurements("single-pixel-at-cut", "strict", role))

result = dict(opencv=cv2.__version__, cases=len(cases), comparisons=checked, all_replacements_and_masks_equal=True,
              boundary_split_counterexamples=2, tolerance_only_evidence_safe=False,
              exact_evidence_decisions=sum(d["exact_band_evidence"] for c in cases for d in c["decisions"]),
              automatic_carry_adoptions=0)
(folder / "context-reference.json").write_text(json.dumps(result, indent=2) + "\n")
print(f"{len(checked)}比較のマスク・統計一致。四比較0だけでの採用は不安全。帯の全画素一致を要する案を検証。")
