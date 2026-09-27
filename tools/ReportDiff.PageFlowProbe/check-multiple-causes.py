#!/usr/bin/env python3
"""編集列と共通行のページ移動から独立に分離を照合し、実コアのC/D・生差分マスクを監査する。"""
import difflib,hashlib,importlib.util,json,sys
from collections import defaultdict
from pathlib import Path
import cv2,numpy as np
root=Path(__file__).resolve().parents[2];folder=Path(sys.argv[1]).resolve()
spec=importlib.util.spec_from_file_location('multi_reference',root/'reference/prototype.py');ref=importlib.util.module_from_spec(spec);sys.modules[spec.name]=ref;spec.loader.exec_module(ref)
records=json.loads((folder/'multiple-causes.json').read_text());manifest=json.loads((folder/'fixtures.json').read_text())
for f in manifest:assert hashlib.sha256((folder/f['id']/(f['side']+'.pdf')).read_bytes()).hexdigest()==f['sha256']
metrics=dict(runs=0,oracle_groups=0,reference_pages=0,surface_images=0,structures=0,raw_png_pairs=0,nonflow_boundaries=0)
results=[]
def read(path,mode=cv2.IMREAD_COLOR):return cv2.imdecode(np.frombuffer(path.read_bytes(),np.uint8),mode)
def render(image,segments,side):
 result=np.full((sum(s['length'] for s in segments),image.shape[1],3),255,dtype=np.uint8)
 for s in segments:
  start=s[side+'_start']
  if start is not None:result[s['canvas_start']:s['canvas_start']+s['length']]=image[start:start+s['length']]
 return result
for run in records:
 assert run['matched'],(run['run'],'fixed expectation')
 name=run['run'];directory=folder/name; inp=run['input'];decision=run['candidate'];metrics['runs']+=1
 rows=inp['rows'];a=sorted([r for r in rows if r['side']==0],key=lambda r:(r['page'],r['start']));b=sorted([r for r in rows if r['side']==1],key=lambda r:(r['page'],r['start']))
 report=json.loads((directory/'cli/result.json').read_text())
 for p in report['pages']:
  for side in ['a','b']:
   assert np.array_equal(read(directory/f"p{p['page']}-O-{side.upper()}.png"),read(directory/'cli'/p['raw_evidence'][side]['image']));metrics['raw_png_pairs']+=1
 for proof in run['nonflow']:
  metrics['nonflow_boundaries']+=1
  byb={r['text']:r for r in b}
  assert not any(min(r['page'],byb[r['text']]['page'])<=proof['boundary']<max(r['page'],byb[r['text']]['page']) for r in a if r['text'] in byb)
  for side in ['source','target']:
   assert proof[side+'_rows'] and len(proof[side+'_rows'])==len(proof[side+'_counterparts'])
   for r,s in zip(proof[side+'_rows'],proof[side+'_counterparts']):assert r['text']==s['text'] and r['page']==s['page'] and r['length']==s['length'] and r['side']!=s['side']
 if inp['gate_ready']:
  for side,actual in [('a',a),('b',b)]:
   native_side=('b' if side=='a' else 'a') if run['reverse'] else side
   expected=next(m for m in manifest if m['id']==run['id'] and m['side']==native_side)['expected_rows']
   assert [[r['text'] for r in actual if r['page']==n+1] for n in range(len(expected))]==expected,(name,'PDF text')
  # 送り候補の辺を使わず、同じ本文行が跨いだページから連結成分を作る。
  adjacency={p['number']:set() for p in inp['pages']};byb={r['text']:r for r in b}
  for r in a:
   if r['text'] in byb and r['page']!=byb[r['text']]['page']:
    other=byb[r['text']]['page'];adjacency[r['page']].add(other);adjacency[other].add(r['page'])
  unseen=set(adjacency);components=[]
  while unseen:
   todo=[min(unseen)];group=set()
   while todo:
    n=todo.pop()
    if n in group:continue
    group.add(n);todo.extend(adjacency[n]-group)
   unseen-=group;components.append(group)
  oracle=[];valid=True
  for group in components:
   aa=[r for r in a if r['page'] in group];bb=[r for r in b if r['page'] in group]
   edits=[op for op in difflib.SequenceMatcher(a=[r['text'] for r in aa],b=[r['text'] for r in bb],autojunk=False).get_opcodes() if op[0]!='equal']
   structs=[s for p in inp['pages'] if p['number'] in group for s in p['structures']]
   if len(group)==1:
    if edits or structs:valid=False
    continue
   if len(edits)!=1 or edits[0][0] not in ['insert','delete']:valid=False;continue
   tag,i,j,k,l=edits[0];extra=bb[k:l] if tag=='insert' else aa[i:j]
   side=1 if tag=='insert' else 0
   assert len({r['page'] for r in extra})==1
   band=dict(page=dict(side=side,page=extra[0]['page']),top=extra[0]['start'],height=sum(r['length'] for r in extra),bottom=extra[-1]['start']+extra[-1]['length'])
   causes=[s for s in structs if s['kind']==('inserted' if side==1 else 'deleted') and s['b' if side==1 else 'a']==band]
   assert len(causes)==1
   oracle.append(dict(cause=causes[0]['reference'],pages=sorted(group),refs=sorted((s['reference']['page'],s['reference']['structural_change_id']) for s in structs)))
  assert (decision['status']=='grouped')==valid,(name,oracle,decision)
  if valid:
   metrics['oracle_groups']+=len(oracle)
   assert len(oracle)==len(decision['groups'])
   for expected,actual in zip(oracle,decision['groups']):
    assert expected['cause']==actual['cause']
    assert expected['pages']==[v['page'] for v in actual['balance']]
    assert expected['refs']==sorted((r['page'],r['structural_change_id']) for r in actual['structures'])
   allrefs=[r for g in oracle for r in g['refs']];assert len(allrefs)==len(set(allrefs))
   assert decision['aggregated_difference_count']==sum(p['clusters'] for p in inp['pages'])+len(oracle)
 for proj in run['projections']:
  n=proj['page'];ca=read(directory/f'p{n}-C-A.png');cb=read(directory/f'p{n}-C-B.png');raw=read(directory/f'p{n}-C-raw.png',cv2.IMREAD_GRAYSCALE)
  compared=ref.compare_page(ca,cb,ref.Params());assert np.array_equal(compared.raw_mask,raw>0),(name,n,'raw mask')
  assert compared.raw_pixels==proj['raw_pixels'] and len(compared.clusters)==len(proj['content_clusters']);metrics['reference_pages']+=1
  display=read(directory/f'p{n}-D-raw.png',cv2.IMREAD_GRAYSCALE);expected=np.zeros_like(display)
  for piece in proj['pieces']:expected[piece['display_start']:piece['display_start']+piece['length']]=raw[piece['content_start']:piece['content_start']+piece['length']]
  assert np.array_equal(display,expected) and np.count_nonzero(display)==np.count_nonzero(raw)
  for side in ['a','b']:
   original=read(directory/f'p{n}-O-{side.upper()}.png')
   for mode in ['C','D']:
    reconstructed=render(original,proj['content_map' if mode=='C' else 'display_map'],side)
    assert np.array_equal(reconstructed,read(directory/f'p{n}-{mode}-{side.upper()}.png'));metrics['surface_images']+=1
    if mode=='D':assert np.count_nonzero(np.any(reconstructed!=255,axis=2))==np.count_nonzero(np.any(original!=255,axis=2))
  actualpage=next(p for p in inp['pages'] if p['number']==n)
  assert [s['id'] for s in proj['structures']]==[s['reference']['structural_change_id'] for s in actualpage['structures']]
  metrics['structures']+=len(proj['structures'])
 results.append(dict(run=name,grouped=decision['status']=='grouped',difference_count=decision['difference_count'],aggregate=decision['aggregated_difference_count'],cli_count=run['cli']['summary']['difference_count']))
 print(name,decision['status'],decision['aggregated_difference_count'],flush=True)
(folder/'independent-check.json').write_text(json.dumps(dict(metrics=metrics,records=results),indent=2)+'\n')
