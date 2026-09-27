#!/usr/bin/env python3
"""Actual PDF CLI acceptance, prior-binary compatibility, and independent O->G->C/D pixel audit."""
import hashlib,importlib.util,json,subprocess,sys
from pathlib import Path
import numpy as np
from PIL import Image
root=Path(__file__).resolve().parents[2]
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=True)
multiple=len(sys.argv)>3 and sys.argv[3]=='--multiple'
numeric=len(sys.argv)>3 and sys.argv[3]=='--numeric'
shared=len(sys.argv)>3 and sys.argv[3]=='--shared'
fixtures=root/'tests/ReportDiff.Tests/Fixtures'/('page-flow-shared' if shared else 'page-flow-numeric' if numeric else 'page-flow-multiple' if multiple else 'page-flow-global')
current=root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll';baseline=Path(sys.argv[2]).resolve()
spec=importlib.util.spec_from_file_location('compat',root/'tools/verify-pagemap-compatibility.py');compat=importlib.util.module_from_spec(spec);spec.loader.exec_module(compat)
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
binaries={label:{p.name:digest(p) for p in cli.parent.glob('*.dll')} for label,cli in [('current',current),('baseline',baseline)]}
if (out/'binaries.json').exists():assert json.loads((out/'binaries.json').read_text())==binaries,'binaries changed since prior run'
else:(out/'binaries.json').write_text(json.dumps(binaries,indent=2)+'\n')
for p,h in json.loads((fixtures/'sha256.json').read_text()).items():assert digest(fixtures/p)==h
cases=[(s,s,False,'',s in ['zero','down','up','variable','mixed','chain','body-tone']) for s in ['zero','down','up','variable','mixed','chain','horizontal','both','body-tone','band-tone','edge','subpixel','unpaired']]
cases += [('reverse','down',True,'',True),('body-excluded','body-tone',False,'exclude: [{page: 2, x: 54.86, y: 59.436, w: 1.7, h: 1.7}]',True),('body-region','body-tone',False,'regions: [{page: 2, name: strict, mode: compare, x: 54.86, y: 59.436, w: 1.7, h: 1.7, diff: {max_shift_mm: 0, edge_tolerance: 0}}]',True),('band-excluded','band-tone',False,'exclude: [{page: 2, x: 54, y: 51, w: 3, h: 3}]',False)]
if multiple:
 cases=[(s+('-ba' if reverse else '-ab'),s,reverse,'',s in ['multiple-down','multiple-up'] or not reverse and s!='multiple-horizontal') for s in ['multiple-down','multiple-up','multiple-variable','multiple-opposite','multiple-terminal-tone','multiple-horizontal'] for reverse in [False,True]]
if numeric:
 cases=[(s+('-ba' if reverse else '-ab'),s,reverse,'',s in ['numeric-down','numeric-up'] or not reverse and s in ['numeric-variable','numeric-multiple']) for s in ['numeric-down','numeric-up','numeric-variable','numeric-multiple','numeric-weak','numeric-horizontal'] for reverse in [False,True]]
if shared:
 cases=[(s+('-ba' if reverse else '-ab'),s,reverse,'',s=='shared-strong-number' or s=='shared-strong-multiple' and not reverse) for s in ['shared-down','shared-up','shared-variable','shared-global-tone','shared-global-multiple','shared-horizontal','shared-global-number','shared-strong-number','shared-strong-multiple'] for reverse in [False,True]]
records=[];surfaces=structures=bands=raw_images=0;compatible_files=0;nonflow_rows=0

def read(folder,path):return np.array(Image.open(folder/path).convert('RGB'))
def render(image,segments,height,side,shift=0,start='canvas_start'):
 result=np.full((height,image.shape[1],3),255,dtype=np.uint8)
 for s in segments:
  if s[side+'_start'] is None:continue
  src=s[side+'_start']-shift;dst=s[start];length=s['length'];lo=max(0,-src);hi=min(length,image.shape[0]-src)
  if hi>lo:result[dst+lo:dst+hi]=image[src+lo:src+hi]
 return result
for name,fixture,reverse,extra,expected in cases:
 results={}
 for carry,label,cli in [(False,'before-off',baseline),(False,'off',current),(True,'on',current),(True,'before-on',baseline)]:
  config=out/(name+f'-{carry}.yaml')
  config.write_text(f'rows: {{enabled: true, carry_enabled: {str(carry).lower()}}}\nalign: {{enabled: true}}\nreport: {{raw_overlay: true}}\n{extra}\n')
  folder=out/(name+'-'+label)
  paths=[fixtures/fixture/(s+'.pdf') for s in (['b','a'] if reverse else ['a','b'])]
  if not (folder/'result.json').exists():
   r=subprocess.run(['dotnet',str(cli),'compare',*map(str,paths),'--out',str(folder),'--config',str(config),'--quiet'],cwd=root,capture_output=True,text=True)
   assert r.returncode==1,(name,label,r.stdout,r.stderr)
  results[label]=(folder,json.loads((folder/'result.json').read_text()))
 off,old=results['off'];on,new=results['on'];flow=new['page_flow']
 if shared:
  assert new['pages']==results['before-on'][1]['pages'],(name,'pre-aggregation pages')
  assert flow['pages']==results['before-on'][1]['page_flow']['pages'] and flow['links']==results['before-on'][1]['page_flow']['links']
 assert (flow['status']=='applied')==expected,(name,flow)
 snapshots=[compat.snapshot(results[l][0]) for l in ['before-off','off']];assert snapshots[0]==snapshots[1],(name,'off compatibility');compatible_files+=len(snapshots[0])
 if name in ['zero','horizontal','both','unpaired'] or shared and not expected:
  snapshots=[compat.snapshot(results[l][0]) for l in ['before-on','on']];assert snapshots[0]==snapshots[1],(name,'on compatibility');compatible_files+=len(snapshots[0])
 if not expected:
  assert new['pages']==old['pages']==results['before-on'][1]['pages'],(name,'fallback')
 elif shared:
  assert new['summary']['difference_count']==(8 if fixture=='shared-strong-number' else 12)
  assert new['summary']['aggregated_difference_count']==3
  component,=flow['aggregation']['shared_components'];assert component['coordinate_system']=='globally_aligned'
  assert len(component['causes'])==2 and any(m['dy']==(-200 if reverse else 200) for m in component['movements'])
  if fixture=='shared-strong-number':
   proof,=flow['numeric_matches'];assert not proof['used_as_exact_support'] and not proof['pixel_equality_proven']
   page=new['pages'][proof['a']['original']['page']-1]
   assert proof['b']['original']['bounds_px']['y']+page['global_shift_px']['dy']-proof['a']['original']['bounds_px']['y']==(-200 if reverse else 200)
   assert page['raw_pixels']==40 and len(page['clusters'])==1
 else:
  assert new['summary']['difference_count']==((11 if fixture=='numeric-multiple' else 7 if fixture=='numeric-up' else 6) if numeric else (11 if fixture=='multiple-terminal-tone' else 10) if multiple else (8 if name=='chain' else 6 if name in ['body-tone','body-region'] else 5))
  assert new['summary']['aggregated_difference_count']==((3 if fixture in ['numeric-up','numeric-multiple'] else 2) if numeric else (3 if fixture=='multiple-terminal-tone' else 2) if multiple else (2 if name in ['body-tone','body-region'] else 1))
 if numeric and flow.get('numeric_matches'):
  shifts={'numeric-down':[4,4],'numeric-up':[-6,-6],'numeric-variable':[4,-6],'numeric-multiple':[4,-6,0,3],'numeric-weak':[4,4]}[fixture]
  base_case={'numeric-down':'terminal-number','numeric-up':'terminal-minus','numeric-variable':'terminal-number','numeric-multiple':'multiple-terminal','numeric-weak':'weak-actual-support'}[fixture]
  original=next(r for r in json.loads((root/'out/t3-1c-numeric/final-verified/numeric.json').read_text()) if r['id']==base_case and r['reverse']==reverse)
  for proof,wanted in zip(flow['numeric_matches'],original['numeric']['pairs'],strict=True):
   assert proof['status']==('applied' if expected else 'not_applied') and not proof['used_as_exact_support'] and not proof['pixel_equality_proven']
   for actual,old_row in [(proof,wanted)]+list(zip(proof['anchors'],wanted['anchors'],strict=True)):
    for side in ['a','b']:
     endpoint=actual[side]['original'];row=old_row[side];native_b=(side=='a') if reverse else (side=='b')
     assert actual[side]['text']==row['text'] and endpoint['bounds_px']['y']==row['top']+256+(shifts[row['page']-1] if native_b else 0)
     assert endpoint['bounds_px']['h']==row['height'] and endpoint['coordinate_system']=='original_top_left'
 for p,q in zip(new['pages'],old['pages']):
  for path1,path2 in [(p['raw_evidence'][s]['image'],q['raw_evidence'][s]['image']) for s in ['a','b']]+[(p['raw_evidence']['overlay'],q['raw_evidence']['overlay'])]:
   assert digest(on/path1)==digest(off/path2);raw_images+=1
  if not expected:continue
  rows=p['row_alignment'];surfaces+=1
  if numeric or shared:
   import cv2
   if 'numeric_ref' not in sys.modules:
    refspec=importlib.util.spec_from_file_location('numeric_ref',root/'reference/prototype.py');ref=importlib.util.module_from_spec(refspec);sys.modules[refspec.name]=ref;refspec.loader.exec_module(ref)
   ref=sys.modules['numeric_ref'];comparison=ref.compare_page(read(on,p['images']['content_a'])[:,:,::-1].copy(),read(on,p['images']['content_b'])[:,:,::-1].copy(),ref.Params())
   assert comparison.raw_pixels==p['raw_pixels'] and len(comparison.clusters)==len(p['clusters'])
  for side in ['a','b']:
   raw=read(on,p['raw_evidence'][side]['image']);dy=(p['global_shift_px'] or {}).get('dy',0) if side=='b' else 0
   aligned=render(raw,[dict(a_start=0,b_start=0,canvas_start=0,length=len(raw))],len(raw),side,dy)
   for mode,segs,height,start,path in [('D',rows['segments'],p['size_px']['h'],'canvas_start',p['images'][side]),('C',rows['content_canvas']['pieces'],rows['content_canvas']['size_px']['h'],'content_start',p['images']['content_'+side])]:
    direct=render(raw,segs,height,side,dy,start);sequential=render(aligned,segs,height,side,0,start)
    assert np.array_equal(direct,sequential) and np.array_equal(direct,read(on,path)),(name,p['page'],mode,side)
    if mode=='D':assert np.count_nonzero(np.any(raw!=255,axis=2))==np.count_nonzero(np.any(direct!=255,axis=2)),(name,'cropped original')
  for s in rows['structural_changes']:
   structures+=1
   for side in ['a','b']:
    source=s['source_'+side]
    if source is None:continue
    dy=(p['global_shift_px'] or {}).get('dy',0) if side=='b' else 0
    top=s['bbox_px']['y'];bottom=top+s['bbox_px']['h'];parts=[]
    for seg in rows['segments']:
     lo=max(top,seg['canvas_start']);hi=min(bottom,seg['canvas_start']+seg['length'])
     if hi>lo and seg[side+'_start'] is not None:parts.append((seg[side+'_start']+lo-seg['canvas_start']-dy,hi-lo))
    assert [(x['y'],x['h']) for x in source['parts_px']]==parts,(name,s['id'],side)
  for c in p['clusters']:
   for fragment in c['row_parts']:
    for side in ['a','b']:
     source=fragment['source_'+side]
     if source is None:continue
     piece=next(x for x in rows['content_canvas']['pieces'] if x['content_start']<=fragment['content_bounds_px']['y']<x['content_start']+x['length'])
     dy=(p['global_shift_px'] or {}).get('dy',0) if side=='b' else 0
     assert source['y']==piece[side+'_start']+fragment['content_bounds_px']['y']-piece['content_start']-dy
 for link in flow['links']:
  if link.get('nonflow'):
   assert link['status']=='skipped' and link['image_status']=='not_performed' and not link['nonflow']['pixel_equality_proven']
   for match in link['nonflow']['source_rows']+link['nonflow']['target_rows']:
    r=match['row'];q=match['counterpart'];assert r['page']==q['page'] and r['side']!=q['side']
    page=next(p for p in new['pages'] if p['page']==r['page']);shift=(page['global_shift_px'] or {}).get('dy',0)
    aa=r if r['side']=='a' else q;bb=q if q['side']=='b' else r
    assert bb['bounds_px']['y']+shift-aa['bounds_px']['y'] in ([-200,-100,0,100,200] if shared else [-100,0,100])
    assert r['coordinate_system']==q['coordinate_system']=='original_top_left';nonflow_rows+=1
  for e in [link['source'],link['target']]:
   if e is None:continue
   p=next(p for p in new['pages'] if p['page']==e['page']);raw=read(on,p['raw_evidence'][e['side']]['image']);b=e['bounds_px'];crop=raw[b['y']:b['y']+b['h'],b['x']:b['x']+b['w']]
   assert np.array_equal(crop,read(on,e['image']));bands+=1
   for ref in e['structures']:
    s=next(s for s in p['row_alignment']['structural_changes'] if s['id']==ref['structural_change_id']);assert s['source_'+e['side']]['bounds_px']==b
  if link['image_status']=='verified':assert np.array_equal(read(on,link['source']['image']),read(on,link['target']['image']))
 records.append(dict(case=name,fixture=fixture,reverse=reverse,extra_yaml=extra,status=flow['status'],summary=new['summary'],directory=str(on),pages=[dict(page=p['page'],alignment=p['alignment']) for p in new['pages']],result_sha256=digest(on/'result.json')))
 print(name,flow['status'],new['summary']['difference_count'],new['summary']['aggregated_difference_count'],flush=True)
 (out/'checkpoint.json').write_text(json.dumps(records,ensure_ascii=False,indent=2)+'\n')
(out/'verification.json').write_text(json.dumps(dict(cases=len(records),processes=4*len(records),surfaces=surfaces,structures=structures,original_band_pngs=bands,raw_png_pairs=raw_images,compatible_files=compatible_files,nonflow_rows=nonflow_rows,records=records),ensure_ascii=False,indent=2)+'\n')
