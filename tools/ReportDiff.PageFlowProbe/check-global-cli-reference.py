#!/usr/bin/env python3
"""Recompare real-CLI C images with the existing Python core; regional settings use the separate coordinate audit."""
import importlib.util,json,sys
from pathlib import Path
import cv2,numpy as np
root=Path(__file__).resolve().parents[2];folder=Path(sys.argv[1]).resolve()
spec=importlib.util.spec_from_file_location('reference',root/'reference/prototype.py');ref=importlib.util.module_from_spec(spec);sys.modules[spec.name]=ref;spec.loader.exec_module(ref)
records=[]
for run in json.loads((folder/'verification.json').read_text())['records']:
 if run['status']!='applied' or run['extra_yaml']:continue
 directory=Path(run['directory']);report=json.loads((directory/'result.json').read_text())
 for page in report['pages']:
  images=[cv2.imdecode(np.frombuffer((directory/page['images']['content_'+side]).read_bytes(),np.uint8),cv2.IMREAD_COLOR) for side in ['a','b']]
  compared=ref.compare_page(*images,ref.Params())
  assert compared.raw_pixels==page['raw_pixels'] and len(compared.clusters)==len(page['clusters']),(run['case'],page['page'])
  records.append(dict(case=run['case'],page=page['page'],raw_pixels=compared.raw_pixels,clusters=len(compared.clusters)))
  print(run['case'],page['page'],compared.raw_pixels,flush=True)
(folder/'reference.json').write_text(json.dumps(records,indent=2)+'\n')
