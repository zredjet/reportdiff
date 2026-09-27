"""見送ったSkia帯の実画素差と、既存コアの検出を独立に照合する。"""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
spec = importlib.util.spec_from_file_location("skia_reference", root / "reference/prototype.py")
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)


def read(path, flags=cv2.IMREAD_COLOR):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), flags)


checked = []
for case in json.loads((folder / "context.json").read_text()):
    directory = folder / case["id"]
    images = {n: read(directory / n) for n in ("a.png", "b.png", "a-replaced.png", "b-replaced.png", "crop-a.png", "crop-b.png")}
    for side, other in (("a", "b"), ("b", "a")):
        band = case[f"band_{side}"]
        rect = np.s_[band["y"]:band["y"] + band["h"], band["x"]:band["x"] + band["w"]]
        assert np.array_equal(images[f"{side}.png"][rect], images[f"crop-{side}.png"])
        expected = images[f"{side}.png"].copy()
        expected[rect] = images[f"crop-{other}.png"]
        assert np.array_equal(expected, images[f"{side}-replaced.png"])
    difference = np.any(images["crop-a.png"] != images["crop-b.png"], axis=2)
    ys, xs = np.where(difference)
    if case["id"] != "skia-band-tone-1":
        assert np.count_nonzero(difference) == 794 and np.unique(ys).tolist() == [85]
        assert xs.min() == 106 and xs.max() == 899
    assert not case["exact_band_pixels"]
    for m in case["measurements"]:
        p = reference.Params()
        if m["profile"] == "strict":
            p.max_shift_mm, p.edge_tolerance = 0, 0
        elif m["profile"] == "loose":
            p.max_shift_mm = .30
        result = reference.compare_page(images[m["input_a"]], images[m["input_b"]], p)
        assert np.array_equal(result.raw_mask, read(directory / m["mask"], cv2.IMREAD_GRAYSCALE) > 0)
        for key, value in dict(raw=result.raw_pixels, status=result.status, clusters=len(result.clusters),
                               noise_dropped=result.noise_dropped, absorbed_groups=result.absorbed_groups,
                               max_shift_px=result.max_shift_px).items():
            assert m[key] == value
        expected_raw = (2612 if m["profile"] == "strict" else 3505) if case["id"] == "skia-band-tone-1" else (794 if m["profile"] == "strict" else 2376)
        assert result.raw_pixels == expected_raw and result.status == "different"
        checked.append(dict(case=case["id"], role=m["role"], profile=m["profile"], reverse=m["reverse"], raw=result.raw_pixels))
    print(case["id"], "罫線・濃淡の差を検出、参照マスク一致", flush=True)
assert len(checked) == 126
(folder / "skia-reference.json").write_text(json.dumps(dict(comparisons=checked, all_masks_and_stats_equal=True,
    pixel_difference_is_detected_by_existing_core=True, gate_was_not_relaxed=True), indent=2) + "\n")
