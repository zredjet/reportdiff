#!/usr/bin/env python3
"""共有原因と数値変更の実CLI・旧新互換性・Python内容比較を検証する。"""
import hashlib,importlib.util,json,subprocess,sys
from pathlib import Path
import numpy as np
from PIL import Image
root=Path(__file__).resolve().parents[2];out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
fx=root/'tests/ReportDiff.Tests/Fixtures/page-flow-shared';current=root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll';before=Path(sys.argv[2]).resolve()
def module(name,path):
 spec=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(spec);sys.modules[name]=m;spec.loader.exec_module(m);return m
compat=module('compat',root/'tools/verify-pagemap-compatibility.py');ref=module('reference',root/'reference/prototype.py')
metrics=dict(runs=0,processes=0,applied=0,reference_pages=0,raw_png_pairs=0,compatible_files=0);records=[]
for case in ['shared-number','shared-fields','shared-anchor-change','shared-carry-change']:
 for reverse in [False,True]:
  name=case+('-ba' if reverse else '-ab');results={};paths=[fx/case/(s+'.pdf') for s in (['b','a'] if reverse else ['a','b'])]
  for label,cli,carry in [('before-on',before,True),('on',current,True),('before-off',before,False),('off',current,False)]:
   config=out/(label+'.yaml');config.write_text(f'rows: {{enabled: true, carry_enabled: {str(carry).lower()}}}\nreport: {{raw_overlay: true}}\n')
   folder=out/(name+'-'+label);r=subprocess.run(['dotnet',str(cli),'compare',*map(str,paths),'--config',str(config),'--out',str(folder),'--quiet'],capture_output=True,text=True)
   assert r.returncode==1,(name,label,r.stdout,r.stderr);results[label]=(folder,json.loads((folder/'result.json').read_text()));metrics['processes']+=1
  directory,new=results['on'];old=results['before-on'][1];flow=new['page_flow'];adopted=case in ['shared-number','shared-fields']
  assert new['pages']==old['pages'] and flow['pages']==old['page_flow']['pages'] and flow['links']==old['page_flow']['links']
  assert (flow['status']=='applied')==adopted
  offsnap=compat.snapshot(results['off'][0]);assert offsnap==compat.snapshot(results['before-off'][0]);metrics['compatible_files']+=len(offsnap)
  if not adopted:
   assert new['pages']==results['off'][1]['pages'];assert compat.snapshot(directory)==compat.snapshot(results['before-on'][0]);metrics['compatible_files']+=len(compat.snapshot(directory))
  else:
   clusters=2 if case=='shared-fields' else 1
   assert new['summary']['difference_count']==7+clusters and new['summary']['aggregated_difference_count']==2+clusters
   proof,=flow['numeric_matches'];assert proof['status']=='applied' and not proof['used_as_exact_support'] and not proof['pixel_equality_proven']
   assert proof['a']['text']!=proof['b']['text'] and all(a['a']['text']==a['b']['text'] for a in proof['anchors'])
   component,=flow['aggregation']['shared_components'];assert len(component['causes'])==2
   assert any(len(m['causes'])==2 and m['dy']==(-200 if reverse else 200) for m in component['movements'])
   assert new['pages'][1]['raw_pixels']==40*clusters and len(new['pages'][1]['clusters'])==clusters;metrics['applied']+=1
  for p,q in zip(new['pages'],results['off'][1]['pages'],strict=True):
   for x,y in [(p['raw_evidence'][s]['image'],q['raw_evidence'][s]['image']) for s in ['a','b']]+[(p['raw_evidence']['overlay'],q['raw_evidence']['overlay'])]:
    assert (directory/x).read_bytes()==(results['off'][0]/y).read_bytes();metrics['raw_png_pairs']+=1
   if adopted:
    images=[np.array(Image.open(directory/p['images']['content_'+s]).convert('RGB'))[:,:,::-1].copy() for s in ['a','b']]
    result=ref.compare_page(*images,ref.Params());assert result.raw_pixels==p['raw_pixels'] and len(result.clusters)==len(p['clusters']);metrics['reference_pages']+=1
  records.append(dict(run=name,summary=new['summary'],status=flow['status']));metrics['runs']+=1;print(name,flow['status'],flush=True)
(out/'verification.json').write_text(json.dumps(dict(metrics=metrics,records=records),indent=2)+'\n')
