#!/usr/bin/env python3
"""計測後のPDF寸法・文字列・画素差を独立に記録する。pypdf/Pillow/NumPyが必要。"""
import hashlib
import json
from pathlib import Path
import sys
from pypdf import PdfReader
from PIL import Image
import numpy as np

base=Path(sys.argv[1]).resolve()
verification=json.load(open(base/'measurements/verification.json'))
hashfile=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
inputs=[]
for item in verification['cases']:
    case=item['case']; expected=case['expected']; count=expected['test']['pages']
    for side,path in zip(['a','b'],case['files']):
        assert hashfile(path)==expected[side+'_sha256']
        pdf=PdfReader(path); assert len(pdf.pages)==count
        body=[]; sizes=[]
        for page in pdf.pages:
            sizes.append([float(page.mediabox.width),float(page.mediabox.height)])
            lines=[' '.join(line.split()) for line in page.extract_text().splitlines() if line.strip()]
            assert len(lines)>=5,(path,lines)
            assert lines[0].startswith(('REPORT HEADER','HEAD START')),(path,lines[:2])
            assert lines[-1]=='END CHECK',(path,lines[-2:])
            body.extend(lines[2:-2])
        want=expected[side+'_rows']
        if want and isinstance(want[0],int):
            want=['NEW ADDED ROW VALUE' if i==-1 else f'ITEM R{i:06d} DESCRIPTION VALUE' for i in want]
        assert body==want,(path,'text_or_order_changed',body[:4],want[:4])
        assert all(s==sizes[0] for s in sizes)
        inputs.append(dict(case=case['id'],side=side,path=path,sha256=hashfile(path),pages=count,body_lines=len(body),
                           media_box_points=sizes[0],media_box_mm=[x*25.4/72 for x in sizes[0]]))
# 未成立の三つの対照を、製品から独立した元画像の行切り出しで調べる。
geometry=[]
for label in ['skia','inset','fractional','integer']:
    d=base/'measurements'/f'{label}-a4-2-true-product-0'
    report=json.load(open(d/'result.json')); page=report['pages'][0]
    a=np.array(Image.open(d/page['raw_evidence']['a']['image']).convert('RGB'))
    b=np.array(Image.open(d/page['raw_evidence']['b']['image']).convert('RGB'))
    top=400; pitch=100; insertion=3 if label in ['skia','inset'] else 2
    differences=[]
    for i in range(insertion,23):
        aa=a[top+i*pitch:top+(i+1)*pitch];bb=b[top+(i+1)*pitch:top+(i+2)*pitch]
        different=np.any(aa!=bb,axis=2)
        if different.any():
            ys,xs=np.where(different)
            differences.append(dict(a_slot=i,b_slot=i+1,pixels=int(different.sum()),
                                    bounds_in_row=[int(xs.min()),int(ys.min()),int(xs.max()+1),int(ys.max()+1)]))
    rows=np.where(np.any(a[300:3000]<255,axis=(1,2)))[0]+300
    geometry.append(dict(case=label+'-a4-2',original_size_px=[a.shape[1],a.shape[0]],
                         nonwhite_body_top=int(rows[0]),nonwhite_body_bottom=int(rows[-1]+1),
                         paired_row_differences=differences,flow=report['page_flow']))
# 濃淡変更の矩形中心を実クラスタが覆うこと。送りによる文字差だけの検出で合格にしない。
markers=[]
for name,slot in [('a4-content-2',19),('a4-band-change-2',0)]:
    d=base/'measurements'/f'skia-{name}-true-product-0';r=json.load(open(d/'result.json'))
    page=r['pages'][1];x=2050;y=round((96+slot*24+10)*300/72)
    hits=[c['id'] for c in page['clusters'] if c['bbox_px']['x']<=x<c['bbox_px']['x']+c['bbox_px']['w'] and c['bbox_px']['y']<=y<c['bbox_px']['y']+c['bbox_px']['h']]
    assert hits,(name,'changed_marker_not_detected',x,y)
    markers.append(dict(case=name,page=2,point_px=[x,y],clusters=hits))
structures=[]
for name,pages in [('a4-sparse-2',2),('a4-sparse-16',16)]:
    d=base/'measurements'/f'sparse-{name}-true-product-0';r=json.load(open(d/'result.json'));flow=r['page_flow']
    actual={(p['page'],s['id']) for p in r['pages'] for s in p['row_alignment']['structural_changes'] if not s['excluded']}
    assert len(actual)==3*pages-1 and len(flow['aggregation']['groups'])==1
    group=flow['aggregation']['groups'][0]
    refs={(s['page'],s['structural_change_id']) for s in group['structures']}
    assert refs==actual and len(group['structures'])==len(actual)
    assert (group['cause']['page'],group['cause']['structural_change_id']) in actual
    assert len(flow['links'])==pages-1 and set(group['links'])==set(range(1,pages))
    for i,link in enumerate(flow['links'],1):
        assert link['status']=='carried' and link['image_status']=='verified'
        source,target=link['source'],link['target']
        assert source['page']==i and target['page']==i+1 and source['side']=='a' and target['side']=='b'
        assert source['bounds_px']==dict(x=0,y=900,w=2475,h=100) and target['bounds_px']==dict(x=0,y=400,w=2475,h=100)
        assert hashfile(d/source['image'])==hashfile(d/target['image'])
        for endpoint in [source,target]:
            page=next(p for p in r['pages'] if p['page']==endpoint['page']);b=endpoint['bounds_px']
            with Image.open(d/page['raw_evidence'][endpoint['side']]['image']) as original, Image.open(d/endpoint['image']) as band:
                assert np.array_equal(np.array(original.crop((b['x'],b['y'],b['x']+b['w'],b['y']+b['h']))),np.array(band))
    structures.append(dict(case=name,structures=len(actual),links=pages-1,original_band_crops_equal=True,all_references_resolve=True))
record=dict(pdf_inputs=inputs,geometry=geometry,changed_markers=markers,structures=structures)
(base/'audit.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n')
print('PDF',len(inputs),'件の寸法・文字順、4対照の画素差、2件の変更矩形、52構造・16リンクを確認')
