#!/usr/bin/env python3
"""固定独立証拠を実CLIへ照合。元の採否・内訳・C/D・実構造とraw画像を変更しない。"""
import hashlib, importlib.util, json, subprocess, sys
from pathlib import Path
import numpy as np
from PIL import Image
root=Path(__file__).resolve().parents[2]
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
baseline=Path(sys.argv[2]).resolve();cli=root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
evidence=root/'out/t3-1c-multiple/verified';fixture=root/'tests/ReportDiff.Tests/Fixtures/page-flow-multiple'
spec=importlib.util.spec_from_file_location('compat',root/'tools/verify-pagemap-compatibility.py');compat=importlib.util.module_from_spec(spec);spec.loader.exec_module(compat)
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
read=lambda folder,path:np.array(Image.open(folder/path).convert('RGB'))
for p,h in json.loads((fixture/'sha256.json').read_text()).items():assert digest(fixture/p)==h
records=[];images=raw=proofs=groups=structures=compatible=0
for r in json.loads((evidence/'multiple-causes.json').read_text()):
 name=r['run'];results={};paths=[fixture/r['id']/(s+'.pdf') for s in (['b','a'] if r['reverse'] else ['a','b'])]
 for label,exe,enabled in [('before-off',baseline,False),('off',cli,False),('on',cli,True)]:
  config=out/f'{label}.yaml';config.write_text(f'rows: {{enabled: true, carry_enabled: {str(enabled).lower()}}}\nreport: {{raw_overlay: true}}\n')
  folder=out/(name+'-'+label)
  run=subprocess.run(['dotnet',str(exe),'compare',*map(str,paths),'--config',str(config),'--out',str(folder),'--quiet'],capture_output=True,text=True)
  assert run.returncode==1,(name,label,run.stderr)
  results[label]=(folder,json.loads((folder/'result.json').read_text()))
 off,old=results['off'];on,new=results['on'];flow=new['page_flow'];expected=r['candidate']
 snapshots=[compat.snapshot(results[l][0]) for l in ['before-off','off']];assert snapshots[0]==snapshots[1],(name,'disabled compatibility');compatible+=len(snapshots[0])
 assert (flow['status']=='applied')==r['input']['gate_ready'],(name,'gate',flow['reasons'])
 assert flow['aggregation']['status']==expected['status'],(name,'grouping')
 for k in ['difference_count','aggregated_difference_count','difference_count_complete','aggregated_difference_count_complete']:
  assert new['summary'][k]==expected[k],(name,k,new['summary'][k],expected[k])
 # 独立ツールは非送り候補を配列から除いた。製品は元のIDを維持し、証明付きで残す。
 assert len(flow['links'])==len(r['product_gate']['selected_links']) or len(flow['links'])==len(r['input']['links'])+len(r['nonflow'])
 assert [l['id'] for l in flow['links']]==list(range(1,len(flow['links'])+1))
 for proof in r['nonflow']:
  link=flow['links'][proof['candidate_index']];p=link['nonflow'];proofs+=1
  assert link['reason']=='same_page_rows_not_flow' and link['status']=='skipped' and link['image_status']=='not_performed' and link['original_comparisons']==0
  assert p['document_text_unique_and_ordered'] and p['no_common_row_crosses_boundary'] and not p['pixel_equality_proven']
  for side in ['source','target']:
   for match,row,other in zip(p[side+'_rows'],proof[side+'_rows'],proof[side+'_counterparts'],strict=True):
    for key,value in [('row',row),('counterpart',other)]:
     e=match[key];assert e['side']==('a' if value['side']==0 else 'b') and e['page']==value['page'] and e['coordinate_system']=='original_top_left'
     assert e['bounds_px']['y']==value['start'] and e['bounds_px']['h']==value['length']
 for actual,wanted in zip(flow['aggregation']['groups'],expected['groups'],strict=True):
  groups+=1
  for k in ['cause','structures','balance']:assert actual[k]==wanted[k],(name,k)
  mapped=[flow['links'][id-1] for id in actual['links']]
  assert len(mapped)==len(wanted['links']) and all(l['status']=='carried' for l in mapped)
  for link,w in zip(mapped,wanted['links']):
   for side in ['source','target']:
    assert link[side]['page']==w[side]['page']['page'] and link[side]['bounds_px']['y']==w[side]['top']
 for p,q in zip(new['pages'],old['pages'],strict=True):
  for x,y in [(p['raw_evidence'][s]['image'],q['raw_evidence'][s]['image']) for s in ['a','b']]+[(p['raw_evidence']['overlay'],q['raw_evidence']['overlay'])]:
   assert digest(on/x)==digest(off/y),(name,'raw');raw+=1
  if flow['status']!='applied':assert p==q,(name,'fallback');continue
  proj=next(x for x in r['projections'] if x['page']==p['page'])
  assert p['raw_pixels']==proj['raw_pixels'] and len(p['clusters'])==len(proj['content_clusters'])
  for mode,keys in [('C',['content_a','content_b']),('D',['a','b'])]:
   for side,key in zip(['A','B'],keys):
    assert np.array_equal(read(on,p['images'][key]),read(evidence/name,f"p{p['page']}-{mode}-{side}.png")),(name,'C/D',mode,side);images+=1
  for actual,wanted in zip(p['row_alignment']['structural_changes'],proj['structures'],strict=True):
   assert actual['id']==wanted['id'] and actual['kind']==wanted['kind'];structures+=1
 if r['id']=='nonflow-terminal-tone':assert new['pages'][1]['raw_pixels']==2850 and len(new['pages'][1]['clusters'])==1
 html=(on/'report.html').read_text();assert 'ページ送りと文書集約' in html
 if r['nonflow']:assert '画素一致の証明ではなく、この帯の内容比較は続けています。' in html
 for g in flow['aggregation']['groups']:
  for ref in g['structures']:
   assert f"href=\"#page-{ref['page']}-structure-{ref['structural_change_id']}\"" in html
 records.append(dict(run=name,summary=new['summary'],status=flow['status'],groups=len(flow['aggregation']['groups']),nonflow=len(r['nonflow']),result_sha256=digest(on/'result.json')))
 print(name,flow['status'],new['summary']['difference_count'],new['summary']['aggregated_difference_count'],flush=True)
 (out/'checkpoint.json').write_text(json.dumps(records,indent=2)+'\n')
(out/'verification.json').write_text(json.dumps(dict(runs=len(records),processes=3*len(records),cd_images=images,raw_png_pairs=raw,nonflow=proofs,groups=groups,structures=structures,compatible_files=compatible,records=records),indent=2)+'\n')
