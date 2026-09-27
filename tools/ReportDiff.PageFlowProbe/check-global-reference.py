"""通常設定のCを既存Python参照と照合。領域・除外のページは別の座標監査で確認する。"""
import importlib.util
import json
from pathlib import Path
import sys
import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]; folder = Path(sys.argv[1]).resolve()
spec = importlib.util.spec_from_file_location('global_reference', root/'reference/prototype.py')
reference = importlib.util.module_from_spec(spec); sys.modules[spec.name] = reference; spec.loader.exec_module(reference)
records = []
for run in json.loads((folder/'global-flow.json').read_text()):
    name = run['scenario']['id']
    for page in run['pages']:
        if page['candidate'] is None or page['parameters']['regions'] or page['parameters']['exclude']:
            continue
        n = page['number']; directory = folder/name
        read = lambda file, flag: cv2.imdecode(np.frombuffer((directory/file).read_bytes(),np.uint8),flag)
        a,b = [read(f'p{n}-C-{side}.png',cv2.IMREAD_COLOR) for side in ['A','B']]
        result = reference.compare_page(a,b,reference.Params())
        assert np.array_equal(result.raw_mask,read(f'p{n}-C-raw.png',cv2.IMREAD_GRAYSCALE)>0),(name,n)
        assert result.raw_pixels==page['candidate']['raw_pixels'] and len(result.clusters)==page['candidate']['content_clusters']
        records.append(dict(case=name,page=n,raw=result.raw_pixels,clusters=len(result.clusters),csharp_mask_equal=True))
        print(name,n,result.raw_pixels,flush=True)
(folder/'reference.json').write_text(json.dumps(records,indent=2)+'\n')
