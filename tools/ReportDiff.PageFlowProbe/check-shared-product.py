#!/usr/bin/env python3
"""固定26と既存104の保存入力を製品Coreへ再生し、独立した原因・収支へ照合する。"""
import copy,json,subprocess,sys
from pathlib import Path
root=Path(__file__).resolve().parents[2];out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
fx=root/'tests/ReportDiff.Tests/Fixtures';records=json.loads((fx/'page-flow-shared/expected.json').read_text())
legacy=json.loads((root/'out/t3-1c-shared/verified/legacy-shared-check.json').read_text())['records'];expected={(r['dataset'],r['run']):r for r in legacy}
def convert(v):
 v=copy.deepcopy(v)
 def band(b):
  return None if b is None else dict(page=dict(side=0 if b['side']=='a' else 1,page=b['page']),top=b['start'],height=b['length'])
 for r in v['rows']:r['side']=0 if r['side']=='a' else 1
 for l in v['links']:l['source']=band(l['source']);l['target']=band(l['target'])
 for p in v['pages']:
  for s in p['structures']:s['a']=band(s['a']);s['b']=band(s['b'])
 return v
for dataset in ['fixed','additional','skia','skia-local','causal','multiple']:
 path=fx/'page-flow-multiple/aggregation.json' if dataset=='multiple' else root/'out/t3-1c-probe'/('aggregate-'+dataset)/'aggregation.json'
 data=json.loads(path.read_text());data=data if dataset=='multiple' else data['records']
 for r in data:
  saved=expected[dataset,r['run']];previous=r['expected'] if dataset=='multiple' else r['decision']
  records.append(dict(run=dataset+'-'+r['run'],input=r['input'] if dataset=='multiple' else convert(r['input']),expected=saved['candidate'] or previous))
(out/'inputs.json').write_text(json.dumps(records,indent=2)+'\n')
subprocess.run(['dotnet',str(root/'tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll'),'--shared-product-audit',str(out/'inputs.json'),str(out/'decisions.json')],check=True)
actual=json.loads((out/'decisions.json').read_text());metrics=dict(inputs=0,shared=0,permutations=0,rejections=0,completeness=0,content_preserved=0)
rk=lambda x:[x['page'],x['structural_change_id']]
for wanted,row in zip(records,actual,strict=True):
 got=row['decision'];want=wanted['expected'];assert row['run']==wanted['run']
 for k in ['status','difference_count','aggregated_difference_count','aggregated_difference_count_complete']:assert got[k]==want[k],(row['run'],k)
 components=[c for c in want.get('components',[]) if len(c['causes'])>1]
 assert len(got.get('shared_components',[]))==len(components),row['run']
 for c,d in zip(got.get('shared_components',[]),components,strict=True):
  assert c['pages']==d['pages'] and c['balance']==d['balance'] and list(map(rk,c['structures']))==d['structures']
  for a,b in zip(c['causes'],d['causes'],strict=True):assert rk(a['reference'])==b['reference'] and a['rows']==len(b['rows']) and a['delta']==b['delta']
  for a,b in zip(c['movements'],d['movements'],strict=True):assert rk(a['structure'])==b['structure'] and list(map(rk,a['causes']))==b['causes'] and a['dy']==b['dy']
  for a,b in zip(c['links'],d['flows'],strict=True):assert a['link']['source']['page']['page']==b['boundary'] and list(map(rk,a['structures']))==b['structures'] and list(map(rk,a['causes']))==b['causes'] and a['rows']==len(b['rows'])
 metrics['inputs']+=1;metrics['shared']+=bool(components)
 for key,value in row['audit'].items():metrics[key]+=value
(out/'verification.json').write_text(json.dumps(metrics,indent=2)+'\n');print(metrics)
