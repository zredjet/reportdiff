#!/usr/bin/env python3
"""固定の独立証拠と旧新CLIを照合。共有原因だけを追加し、C/D・raw画像は維持する。"""
import hashlib,importlib.util,json,subprocess,sys
from pathlib import Path
import numpy as np
from PIL import Image
root=Path(__file__).resolve().parents[2];out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
before=Path(sys.argv[2]).resolve();current=root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
fx=root/'tests/ReportDiff.Tests/Fixtures/page-flow-shared';evidence=root/'out/t3-1c-shared/verified'
spec=importlib.util.spec_from_file_location('compat',root/'tools/verify-pagemap-compatibility.py');compat=importlib.util.module_from_spec(spec);spec.loader.exec_module(compat)
metrics=dict(runs=0,processes=0,new_shared=0,unchanged_cases=0,compatible_files=0,cd_images=0,raw_png_pairs=0,structures=0);records=[]
for name,sha in json.loads((fx/'sha256.json').read_text()).items():assert hashlib.sha256((fx/name).read_bytes()).hexdigest()==sha
for r in json.loads((fx/'expected.json').read_text()):
 name=r['run'];paths=[fx/r['id']/(s+'.pdf') for s in (['b','a'] if r['reverse'] else ['a','b'])];results={}
 for label,cli,carry in [('before-on',before,True),('on',current,True),('before-off',before,False),('off',current,False)]:
  config=out/(label+'.yaml');config.write_text(f'rows: {{enabled: true, carry_enabled: {str(carry).lower()}}}\nreport: {{raw_overlay: true}}\n')
  folder=out/(name+'-'+label);p=subprocess.run(['dotnet',str(cli),'compare',*map(str,paths),'--out',str(folder),'--config',str(config),'--quiet'],capture_output=True,text=True)
  assert p.returncode==1,(name,label,p.stdout,p.stderr);results[label]=(folder,json.loads((folder/'result.json').read_text()));metrics['processes']+=1
 olddir,old=results['before-on'];directory,new=results['on'];flow=new['page_flow'];expected=r['expected']
 assert new['pages']==old['pages'],(name,'ページ出力不変')
 assert flow['pages']==old['page_flow']['pages'] and flow['links']==old['page_flow']['links'],(name,'前段不変')
 for key in ['difference_count','aggregated_difference_count','aggregated_difference_count_complete']:assert new['summary'][key]==expected[key],(name,key)
 assert flow['aggregation']['status']==expected['status']
 shared=[c for c in expected['components'] if len(c['causes'])>1];actual=flow['aggregation'].get('shared_components',[])
 assert len(shared)==len(actual)
 if not shared:
  assert compat.snapshot(olddir)==compat.snapshot(directory),(name,'非対象の出力');metrics['unchanged_cases']+=1
 else:
  metrics['new_shared']+=1
  for wanted,got in zip(shared,actual,strict=True):
   rk=lambda v:[v['page'],v['structural_change_id']]
   assert got['coordinate_system']=='globally_aligned' and got['pages']==wanted['pages']
   assert [rk(x) for x in got['structures']]==wanted['structures'] and got['balance']==wanted['balance']
   for a,b in zip(got['causes'],wanted['causes'],strict=True):assert rk(a['reference'])==b['reference'] and a['rows']==len(b['rows']) and a['delta_px']==b['delta']
   for a,b in zip(got['movements'],wanted['movements'],strict=True):assert rk(a['structure'])==b['structure'] and [rk(x) for x in a['causes']]==b['causes'] and a['dy']==b['dy']
   for a,b in zip(got['links'],wanted['flows'],strict=True):
    link=flow['links'][a['link']-1];assert link['status']=='carried' and link['source']['page']==b['boundary']
    assert [rk(x) for x in a['structures']]==b['structures'] and [rk(x) for x in a['causes']]==b['causes'] and a['rows']==len(b['rows'])
  html=(directory/'report.html').read_text();assert '同じ送り連鎖の複数原因' in html
  refs=[x for c in actual for x in c['structures']]+[x for g in flow['aggregation']['groups'] for x in g['structures']]
  assert len(refs)==len({(x['page'],x['structural_change_id']) for x in refs})
  for x in refs:assert f'id="page-{x["page"]}-structure-{x["structural_change_id"]}"' in html and f'href="#page-{x["page"]}-structure-{x["structural_change_id"]}"' in html
 first,second=[compat.snapshot(results[label][0]) for label in ['before-off','off']];assert first==second;metrics['compatible_files']+=len(first)
 for p,q in zip(new['pages'],results['off'][1]['pages'],strict=True):
  for a,b in [(p['raw_evidence'][s]['image'],q['raw_evidence'][s]['image']) for s in ['a','b']]+[(p['raw_evidence']['overlay'],q['raw_evidence']['overlay'])]:
   assert (directory/a).read_bytes()==(results['off'][0]/b).read_bytes();metrics['raw_png_pairs']+=1
  if not r['input']['gate_ready']:assert p==q;continue
  proj=next(x for x in r['projections'] if x['page']==p['page'])
  assert p['raw_pixels']==proj['raw_pixels'] and len(p['clusters'])==len(proj['content_clusters'])
  for mode,keys in [('C',['content_a','content_b']),('D',['a','b'])]:
   for side,key in zip(['A','B'],keys):
    assert np.array_equal(np.array(Image.open(directory/p['images'][key])),np.array(Image.open(evidence/name/f'p{p["page"]}-{mode}-{side}.png')));metrics['cd_images']+=1
  assert [s['id'] for s in p['row_alignment']['structural_changes']]==[s['id'] for s in proj['structures']];metrics['structures']+=len(proj['structures'])
 metrics['runs']+=1;records.append(dict(run=name,summary=new['summary'],aggregation=flow['aggregation']))
 print(name,new['summary']['difference_count'],new['summary']['aggregated_difference_count'],flush=True)
 (out/'verification.json').write_text(json.dumps(dict(metrics=metrics,records=records),indent=2)+'\n')
