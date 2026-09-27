#!/usr/bin/env python3
"""未成立6方向の独立診断を、共通行のページ所属・編集列・画素マスク・旧CLIへ照合する。"""
import hashlib,importlib.util,json,sys
from pathlib import Path
import cv2,numpy as np
from shared_cause_model import evaluate
root=Path(__file__).resolve().parents[2];folder=Path(sys.argv[1]).resolve()
prior=Path(sys.argv[2]).resolve()
def module(name,path):
 s=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(s);sys.modules[name]=m;s.loader.exec_module(m);return m
reference=module('inference_reference',root/'reference/prototype.py');compat=module('inference_compat',root/'tools/verify-pagemap-compatibility.py')
metrics=dict(runs=0,experimental_grouped=0,new_experimental_grouped=0,unmet_positive=0,negative_rejections=0,compatible_files=0,
             boundary_records=0,crossing_candidates=0,verified_bands=0,original_comparisons=0,reference_pages=0,surface_images=0,structures=0,raw_images=0,
             permutations=0,duplicate_rejections=0,crossing_rejections=0,order_rejections=0,nonflow_alternatives=0,nonflow_row_pairs=0)
fixed={r['run']:r for r in json.loads((root/'tests/ReportDiff.Tests/Fixtures/page-flow-shared/expected.json').read_text())}
read=lambda path,gray=False:cv2.imdecode(np.frombuffer(path.read_bytes(),np.uint8),cv2.IMREAD_GRAYSCALE if gray else cv2.IMREAD_COLOR)
rk=lambda r:(r['page'],r['structural_change_id'])
records=[]
def render(original,segments,side):
 result=np.full((sum(s['length'] for s in segments),original.shape[1],3),255,np.uint8)
 for s in segments:
  if s[side+'_start'] is not None:result[s['canvas_start']:s['canvas_start']+s['length']]=original[s[side+'_start']:s[side+'_start']+s['length']]
 return result
for run in json.loads((folder/'diagnosis.json').read_text()):
 name=run['run'];directory=folder/name;e=run['experiment'];inp=e['input'];current=json.loads((directory/'cli/result.json').read_text());saved=fixed[name]
 actual=compat.snapshot(directory/'cli');assert actual==compat.snapshot(prior/name),(name,'製品出力不変');metrics['compatible_files']+=len(actual)
 d=evaluate(inp);got=e['aggregate']
 for key in ['status','difference_count','aggregated_difference_count','aggregated_difference_count_complete']:assert d[key]==got[key],(name,key)
 shared=[c for c in d['components'] if len(c['causes'])>1]
 for c,q in zip(shared,got.get('shared_components',[]),strict=True):
  assert c['pages']==q['pages'] and c['balance']==q['balance']
  assert [tuple(x) for x in c['structures']]==list(map(rk,q['structures']))
  for cause,proof in zip(c['causes'],q['causes'],strict=True):assert tuple(cause['reference'])==rk(proof['reference']) and cause['delta']==proof['delta'] and len(cause['rows'])==proof['rows']
  for move,proof in zip(c['movements'],q['movements'],strict=True):assert tuple(move['structure'])==rk(proof['structure']) and move['dy']==proof['dy'] and [tuple(x) for x in move['causes']]==list(map(rk,proof['causes']))
  for flow,proof in zip(c['flows'],q['links'],strict=True):assert flow['boundary']==proof['link']['source']['page']['page'] and [tuple(x) for x in flow['causes']]==list(map(rk,proof['causes']))
 grouped=d['status']=='grouped';metrics['experimental_grouped']+=grouped
 previous=saved['expected']['status']=='grouped';is_new=grouped and not previous
 assert is_new==(run['id'] in ['shared-chain','shared-and-independent']);metrics['new_experimental_grouped']+=is_new
 if run['id']=='shared-chain':assert (d['difference_count'],d['aggregated_difference_count'])==(10,2)
 if run['id']=='shared-and-independent':assert (d['difference_count'],d['aggregated_difference_count'])==(12,3)
 if saved['hypothesis']['grouped'] and not grouped:metrics['unmet_positive']+=1
 if not saved['hypothesis']['grouped']:assert not grouped;metrics['negative_rejections']+=1
 rr=run['rows'];aa={r['text']:r for r in rr if r['page']['side']==0};bb={r['text']:r for r in rr if r['page']['side']==1}
 maximum=round(current['config']['rows']['max_shift_mm']/25.4*300);minimum=current['config']['rows']['min_support_bands']
 for boundary in e['inferred']['boundaries']:
  side=boundary['side'];page=boundary['page'];a,b=(aa,bb) if side==0 else (bb,aa)
  source=sorted([r for r in a.values() if r['page']['page']==page],key=lambda r:r['top']);crossed=[r for r in source if r['text'] in b and b[r['text']]['page']['page']==page+1]
  assert boundary['crossing']==crossed and boundary['counterparts']==[b[r['text']] for r in crossed]
  assert boundary['no_common_crossing']==all(not(min(aa[t]['page']['page'],bb[t]['page']['page'])<=page<max(aa[t]['page']['page'],bb[t]['page']['page'])) for t in aa.keys()&bb.keys())
  shifts={}
  for r in source:
   other=b.get(r['text'])
   if other is not None and other['page']['page']==page and round(other['left']-r['left'])==0:shifts.setdefault(round(other['baseline']-r['baseline']),[]).append(r['text'])
  assert boundary['shifts']==[dict(dy=dy,text=shifts[dy],eligible=0<dy<=maximum and len(shifts[dy])>=minimum) for dy in sorted(shifts)]
  metrics['boundary_records']+=1
  candidate=boundary['candidate']
  if candidate:
   height=sum(r['height'] for r in crossed);assert candidate['source']['height']==candidate['target']['height']==height and candidate['text']==[r['text'] for r in crossed]
   assert candidate['support']==len(shifts.get(height,[]));assert height<=maximum;metrics['crossing_candidates']+=1
 for link in e['links']:
  if link['status']!='band_verified':continue
  images=[]
  for endpoint in [link['source'],link['target']]:
   original=read(directory/f"p{endpoint['page']['page']}-O-{'AB'[endpoint['page']['side']]}.png")
   images.append(original[endpoint['top']:endpoint['bottom']])
  assert np.array_equal(*images);metrics['verified_bands']+=1
 metrics['original_comparisons']+=e['original_comparisons']
 assert e['all_nonflow_proven'] and e['nonflow']['failure_reason'] is None
 for alternative,proof in zip(e['nonflow_alternatives'],e['nonflow']['proofs'],strict=True):
  assert alternative['reason']=='text_mismatch' and alternative['source']==proof['source'] and alternative['target']==proof['target']
  metrics['nonflow_alternatives']+=1
  for matches in [proof['source_rows'],proof['target_rows']]:
   for match in matches:
    row,counterpart=match['row'],match['counterpart'];assert row['page']['page']==counterpart['page']['page'] and row['page']['side']!=counterpart['page']['side']
    pick=lambda band:next(r for r in rr if r['page']==band['page'] and r['top']==band['top'] and r['height']==band['height'])
    assert pick(row)['text']==pick(counterpart)['text'];assert match['original_row']==row and match['original_counterpart']==counterpart
    metrics['nonflow_row_pairs']+=1
 for page in current['pages']:
  n=page['page'];original=[read(directory/f'p{n}-O-{s}.png') for s in ['A','B']]
  for side,im in zip(['a','b'],original):assert np.array_equal(im,read(directory/'cli'/page['raw_evidence'][side]['image']));metrics['raw_images']+=1
  a,b=[((im[:,:,0].astype(np.int32)*114+im[:,:,1].astype(np.int32)*587+im[:,:,2].astype(np.int32)*299+500)//1000) for im in original]
  tint=(np.minimum(255-a,255-b)*204+127)//255
  raw=np.stack([a+tint,np.minimum(a,b)+tint,b+tint],axis=2).astype(np.uint8)
  assert np.array_equal(raw,read(directory/'cli'/page['raw_evidence']['overlay']));metrics['raw_images']+=1
 for proj in e['projections']:
  n=proj['page'];ca,cb=[read(directory/f'p{n}-C-{s}.png') for s in ['A','B']];mask=read(directory/f'p{n}-C-raw.png',True)
  result=reference.compare_page(ca,cb,reference.Params());assert np.array_equal(result.raw_mask,mask>0) and result.raw_pixels==proj['raw_pixels'] and len(result.clusters)==len(proj['content_clusters']);metrics['reference_pages']+=1
  display=read(directory/f'p{n}-D-raw.png',True);projected=np.zeros_like(display)
  for piece in proj['pieces']:projected[piece['display_start']:piece['display_start']+piece['length']]=mask[piece['content_start']:piece['content_start']+piece['length']]
  assert np.array_equal(projected,display) and np.count_nonzero(display)==np.count_nonzero(mask)
  for side in ['a','b']:
   original=read(directory/f'p{n}-O-{side.upper()}.png')
   for mode in ['C','D']:
    rendered=render(original,proj['content_map' if mode=='C' else 'display_map'],side)
    assert np.array_equal(rendered,read(directory/f'p{n}-{mode}-{side.upper()}.png'));metrics['surface_images']+=1
    if mode=='D':assert np.count_nonzero(np.any(rendered!=255,axis=2))==np.count_nonzero(np.any(original!=255,axis=2))
  page=next(p for p in inp['pages'] if p['number']==n)
  assert [s['id'] for s in proj['structures']]==[s['reference']['structural_change_id'] for s in page['structures']];metrics['structures']+=len(page['structures'])
 if grouped:
  assert e['gate']['ready'] and e['inferred']['displacement_support'] and all(a['accepted'] for a in e['adoptions'])
  assert all(min(a['support_bands'])>=minimum for a in e['adoptions'])
 if run['id']=='same-page-two':
  limited=[b for b in e['inferred']['boundaries'] if b['reason']=='insufficient_displacement_support'];assert len(limited)==1 and limited[0]['candidate']['support']==0
  failed=[g for g in e['gaps'] if not g['local'] and not g['verified_carry']];assert len(failed)==1 and failed[0]['before']==4 and failed[0]['after']==0
 for k,v in run['audit'].items():metrics[k]+=v
 metrics['runs']+=1;records.append(dict(run=name,product=current['summary'],diagnostic=dict(status=d['status'],count=d['difference_count'],aggregate=d['aggregated_difference_count']),new_experimental_grouped=is_new))
 print(name,'診断',d['difference_count'],d['aggregated_difference_count'],flush=True)
(folder/'verification.json').write_text(json.dumps(dict(metrics=metrics,records=records),ensure_ascii=False,indent=2)+'\n');print(metrics)
