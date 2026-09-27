"""白い帯の案を比較参照実装で照合する。製品・参照実装の式は変更しない。"""
import dataclasses
import importlib.util
import json
import math
from pathlib import Path
import sys

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folder = root / 'out/t3-1b-probe/results/white-band-pdf'
spec = importlib.util.spec_from_file_location('white_band_reference', root / 'reference/prototype.py')
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)

def read(name, flags=cv2.IMREAD_COLOR):
    return cv2.imdecode(np.frombuffer((folder / name).read_bytes(), np.uint8), flags)

results = []
for case in json.loads((folder / 'white-band-pdf.json').read_text()):
    name, profile = case['name'], case['profile']
    for variant, suffix_a, suffix_b in [('baseline', 'source-a', 'baseline-b'),
                                        ('previous', 'display-a', 'display-b'),
                                        ('candidate', 'prepared-a', 'prepared-b')]:
        a, b = read(name + '-' + suffix_a + '.png'), read(name + '-' + suffix_b + '.png')
        p = reference.Params()
        if profile == 'strict':
            p.max_shift_mm, p.edge_tolerance = 0, 0
        if profile == 'loose':
            p.max_shift_mm = 0.30
        if variant != 'baseline':
            for band in case['excluded_bands_px']:
                y, height = band['y'], band['h']
                rect = (0, (y + 0.25) / 300 * 25.4, a.shape[1] / 300 * 25.4, (height - 0.5) / 300 * 25.4)
                assert (math.floor(p.px(rect[1])), math.ceil(p.px(rect[1] + rect[3]))) == (y, y + height)
                p.exclude_mm.append(rect)
        if case['reverse']:
            a, b = b, a
        actual = reference.compare_page(a, b, p)
        expected = case[variant]
        expected_mask = read(case['id'] + '-' + variant + '-raw.png', cv2.IMREAD_GRAYSCALE) > 0
        assert np.array_equal(expected_mask, actual.raw_mask), (case['id'], variant, 'raw mask')
        assert (actual.status, actual.raw_pixels, actual.noise_dropped, actual.absorbed_groups, actual.max_shift_px) == (
            expected['Status'], expected['RawPixels'], expected['NoiseDropped'], expected['AbsorbedGroups'], expected['MaxShiftPx'])
        assert len(actual.clusters) == len(expected['clusters'])
        for c, e in zip(actual.clusters, expected['clusters']):
            assert (c.x, c.y, c.w, c.h, c.pixels) == tuple(e[k] for k in ('x', 'y', 'w', 'h', 'Pixels'))
        results.append(dict(id=case['id'], variant=variant, status=actual.status, raw_pixels=actual.raw_pixels,
                            clusters=[dataclasses.asdict(c) for c in actual.clusters], mask_equal=True))
    print(case['id'] + ': 基準・従来案・白い帯の案が C# と一致', flush=True)

# 実際の変更画素の候補判定値を比較する。
causes = []
for variant, suffix_a, suffix_b, y in [('baseline', 'source-a', 'baseline-b', 400),
                                     ('candidate', 'prepared-a', 'prepared-b', 500)]:
    a, b = [read('boundary-0-' + suffix + '.png') for suffix in (suffix_a, suffix_b)]
    la, lb = reference.to_lab(a), reference.to_lab(b)
    blur_a, blur_b = cv2.blur(la, (3, 3)), cv2.blur(lb, (3, 3))
    contrast_a, contrast_b = reference._contrast(la, 5), reference._contrast(lb, 5)
    x = 102
    delta = abs(blur_a[y, x, 0] - blur_b[y, x, 0])
    threshold = 3 + 0.3 * max(contrast_a[y, x, 0], contrast_b[y, x, 0])
    causes.append(dict(variant=variant, x=x, y=y, a=a[y, x].tolist(), b=b[y, x].tolist(),
                       blur_delta_L=float(delta), threshold_L=float(threshold), candidate_at_pixel=bool(delta > threshold)))
(folder / 'reference.json').write_text(json.dumps(dict(opencv=cv2.__version__, comparisons=results, pixel_causes=causes), indent=2) + '\n')
print(f'{len(results)} 回の参照比較を確認しました。', flush=True)
