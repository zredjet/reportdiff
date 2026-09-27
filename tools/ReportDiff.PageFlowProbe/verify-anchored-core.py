#!/usr/bin/env python3
"""A2テストが保存したC/Dを、固定PDF用の独立座標表とPython比較コアで照合する。"""
import importlib.util
import json
import sys
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("anchored_reference", ROOT / "reference/prototype.py")
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)


def read(path, gray=False):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), 0 if gray else 1)


def check(root, actual_pdf=False):
    records = []
    for variant, pixels in [("original", 0), ("tone", 3807), ("context", 956)]:
        for reverse in [False, True]:
            folder = root / (variant + ("-ba" if reverse else "-ab"))
            record = json.loads((folder / "result.json").read_text())
            source, target = ("B", "A") if reverse else ("A", "B")
            originals = {(side, p): read(folder / f"O{p}-{side}.png") for side in "AB" for p in [1, 2]}
            masks = {}
            for p in [1, 2]:
                expected = {(side): originals[side, p].copy() for side in "AB"}
                if p == 1:
                    expected[target][500:700] = originals[target, 1][600:800]
                    expected[target][700:900] = originals[target, 2][300:500]
                else:
                    expected[target][300:700] = originals[target, 2][500:900]
                    expected[target][700:900] = 255
                for side in "AB":
                    assert np.array_equal(expected[side], read(folder / f"C{p}-{side}.png")), (folder.name, p, side, "C")
                compared = reference.compare_page(expected["A"], expected["B"], reference.Params())
                masks[p] = compared.raw_mask.astype(np.uint8) * 255
                assert np.array_equal(masks[p], read(folder / f"C{p}-raw.png", True)), (folder.name, p, "raw")
                actual = record["Contents"][p - 1]
                assert compared.raw_pixels == actual["RawPixels"] == (pixels if p == 1 else 0)
                assert compared.noise_dropped == actual["NoiseDropped"]
                assert len(compared.clusters) == len(actual["Clusters"])
                for x, y in zip(compared.clusters, actual["Clusters"], strict=True):
                    assert (x.id, x.x, x.y, x.w, x.h, x.pixels) == (y["Id"], y["Bounds"]["Left"], y["Bounds"]["Top"],
                        y["Bounds"]["Right"] - y["Bounds"]["Left"], y["Bounds"]["Bottom"] - y["Bounds"]["Top"], y["Pixels"])
                table = ([(0, 500, 0, 0), (500, 100, None, 500), (600, 200, 500, 600),
                    (900 if reverse else 800, 200, 700, None), (800 if reverse else 1000, 100, None, 800), (1100, 350, 900, 900)]
                    if p == 1 else [(0, 300, 0, 0), (300, 200, None, 300), (500, 400, 300, 500),
                    (900, 200, 700, None), (1100, 350, 900, 900)])
                for side, column in [(source, 2), (target, 3)]:
                    display = np.full((1450, 999, 3), 255, np.uint8)
                    coverage = np.zeros(1250, np.int32)
                    for row in table:
                        start = row[column]
                        if start is None:
                            continue
                        y, length = row[:2]
                        display[y:y + length] = originals[side, p][start:start + length]
                        coverage[start:start + length] += 1
                    assert np.all(coverage == 1)
                    assert np.array_equal(display, read(folder / f"D{p}-{side}.png")), (folder.name, p, side, "D")
            display = {p: np.zeros((1450, 999), np.uint8) for p in [1, 2]}
            for lo, hi, shift in [(0, 500, 0), (500, 700, 100), (700, 900, 200 if reverse else 100), (900, 1250, 200)]:
                display[1][lo + shift:hi + shift] |= masks[1][lo:hi]
            display[2][300:500] |= masks[1][700:900]
            display[2][:300] |= masks[2][:300]
            display[2][500:1450] |= masks[2][300:1250]
            for p in [1, 2]:
                assert np.array_equal(display[p], read(folder / f"D{p}-raw.png", True)), (folder.name, p, "D raw")
            assert record["OmittedBandPixels"] == 199800
            omitted = sum(np.any(originals[target, 1][top:top + 100] != 255, axis=2).sum() for top in [500, 800])
            assert record["OmittedNonwhitePixels"] == omitted
            assert len(record["Structures"]) == 6
            assert sum(s["Role"] == "cause" for s in record["Structures"]) == 2
            assert record["Aggregation"]["AggregatedDifferenceCount"] == (2 if pixels == 0 else 3)
            records.append(dict(name=folder.name, raw_pixels=pixels, display_pixels=[int((display[p] != 0).sum()) for p in [1, 2]]))
    result = dict(cases=6, content_pages=12, content_images=24, display_images=24, raw_masks=24,
        original_rows_checked=30000, records=records, fixed_pdf_core_cases=6 if actual_pdf else 2, memory_variants=0 if actual_pdf else 4)
    (root.parent / "reference-check.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    check(Path(sys.argv[1]).resolve(), "--actual-pdf" in sys.argv[2:])
