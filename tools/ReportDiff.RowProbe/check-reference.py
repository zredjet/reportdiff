import dataclasses
import importlib.util
import json
import math
from pathlib import Path
import sys

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('row_probe_reference', root / 'reference/prototype.py')
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)
folder = root / 'out/t3-1b-probe/results/pdf'
def read(name, flags=cv2.IMREAD_COLOR):
    return cv2.imdecode(np.frombuffer((folder / name).read_bytes(), np.uint8), flags)
results = []
for item in json.loads((folder / 'pdf-seams.json').read_text()):
    a = read(item['prefix'] + '-aligned-a.png')
    b = read(item['prefix'] + '-aligned-b.png')
    cuts = (400, a.shape[0] - 100)
    p = reference.Params(exclude_mm=[(0, (y + 0.25) / 300 * 25.4, a.shape[1] / 300 * 25.4, 99.5 / 300 * 25.4) for y in cuts])
    for y, (_, ey, _, eh) in zip(cuts, p.exclude_mm):
        assert (math.floor(p.px(ey)), math.ceil(p.px(ey + eh))) == (y, y + 100)
    if item['strict']:
        p.max_shift_mm, p.edge_tolerance = 0, 0
    r = reference.compare_page(a, b, p)
    expected = read(item['prefix'] + ('-strict' if item['strict'] else '-normal') + '-raw.png', cv2.IMREAD_GRAYSCALE) > 0
    assert np.array_equal(expected, r.raw_mask), item['prefix']
    assert r.status == item['Status'] and r.raw_pixels == item['RawPixels'], item['prefix']
    assert len(r.clusters) == len(item['clusters']), item['prefix']
    for c, expected_cluster in zip(r.clusters, item['clusters']):
        assert (c.x, c.y, c.w, c.h, c.pixels) == tuple(expected_cluster[k] for k in ('x', 'y', 'w', 'h', 'Pixels'))
    results.append(dict(case=item['prefix'], strict=item['strict'], raw_pixels=r.raw_pixels,
                        clusters=[dataclasses.asdict(c) for c in r.clusters], csharp_mask_equal=True))
    print(item['prefix'], 'strict' if item['strict'] else 'normal', 'exact', flush=True)
(folder / 'reference.json').write_text(json.dumps(dict(cases=results, opencv=cv2.__version__), indent=2) + '\n')

# 切断行をずらしても、対応画素が完全一致する範囲で偽の差分が残るか確認する。
a_canvas, b_canvas = [read('pdf-rule-1.44-inserted-aligned-' + side + '.png') for side in ('a', 'b')]
a_original = np.concatenate((a_canvas[:400], a_canvas[500:]))
b_original = b_canvas[:-100]
source_height, width = b_original.shape[:2]
white = np.full((100, width, 3), 255, np.uint8)
sweep = []
for delta in (-8, -4, -2, -1, 0, 1, 2, 4, 8):
    top = 400 + delta
    aa = np.concatenate((a_original[:top], white, a_original[top:]))
    bb = np.concatenate((b_original, white))
    allowed = np.ones(aa.shape[:2], bool)
    allowed[top:top + 100] = False
    allowed[source_height:] = False
    assert np.array_equal(aa[allowed], bb[allowed]), delta
    for reverse in (False, True):
        # px→mm→px の浮動小数誤差で、境界外 1px まで除外することを防ぐ。
        # floor / ceil 後の整数マスクが [y,y+100) となる mm 値を指定する。
        exclusions = [(0, (y + 0.25) / 300 * 25.4, width / 300 * 25.4, 99.5 / 300 * 25.4) for y in (top, source_height)]
        p = reference.Params(exclude_mm=exclusions)
        for y, (_, ey, _, eh) in zip((top, source_height), exclusions):
            assert (math.floor(p.px(ey)), math.ceil(p.px(ey + eh))) == (y, y + 100)
        r = reference.compare_page(bb, aa, p) if reverse else reference.compare_page(aa, bb, p)
        sweep.append(dict(cut_y=top, reverse=reverse, paired_pixels_equal=True,
                          status=r.status, raw_pixels=r.raw_pixels, clusters=[dataclasses.asdict(c) for c in r.clusters]))
print('Cut sweep: 9 safe cuts x both directions recorded.', flush=True)
(folder / 'cut-sweep.json').write_text(json.dumps(sweep, indent=2) + '\n')

# 原因になった一段目の値を残す。二段目の式・期待値は変えない。
mask = read('pdf-rule-1.44-inserted-normal-raw.png', cv2.IMREAD_GRAYSCALE)
la, lb = [reference.to_lab(i) for i in (a_canvas, b_canvas)]
ba, bb = [cv2.blur(i, (3, 3)) for i in (la, lb)]
ca, cb = [reference._contrast(i, 5) for i in (la, lb)]
cause = []
for y, x in zip(*np.nonzero(mask)):
    cause.append(dict(x=int(x), y=int(y), a=a_canvas[y, x].tolist(), b=b_canvas[y, x].tolist(),
                      blur_delta_L=float(abs(ba[y, x, 0] - bb[y, x, 0])),
                      threshold_L=float(3 + 0.3 * max(ca[y, x, 0], cb[y, x, 0]))))
(folder / 'pixel-cause.json').write_text(json.dumps(cause, indent=2) + '\n')
