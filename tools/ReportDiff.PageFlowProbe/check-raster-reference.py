"""診断用Cの判定マスクを既存Python参照実装と比較する。"""
import importlib.util
import json
from pathlib import Path
import sys

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
spec = importlib.util.spec_from_file_location('raster_reference', root / 'reference/prototype.py')
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)
read = lambda p, flag: cv2.imdecode(np.frombuffer(p.read_bytes(), np.uint8), flag)
records = []
for name in ['integer', 'sparse']:
    for page in [1, 2]:
        directory = folder / name
        a, b = [read(directory / f'C-{page}-{s}.png', cv2.IMREAD_COLOR) for s in ['A', 'B']]
        result = reference.compare_page(a, b, reference.Params())
        mask = read(directory / f'C-{page}-raw.png', cv2.IMREAD_GRAYSCALE) > 0
        assert np.array_equal(result.raw_mask, mask), (name, page)
        records.append(dict(case=name, page=page, raw=result.raw_pixels, clusters=len(result.clusters),
                            csharp_mask_equal=True))
        print(records[-1], flush=True)
(folder / 'reference.json').write_text(json.dumps(records, indent=2) + '\n')
