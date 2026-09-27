#!/usr/bin/env python3
"""既存76条件と複数原因28方向を旧新CLIで比較し、承認した数値変更6方向だけを固定独立結果へ接続する。"""
import hashlib, importlib.util, json, subprocess, sys
from pathlib import Path
root=Path(__file__).resolve().parents[2];out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
old=Path(sys.argv[2]).resolve();current=root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
spec=importlib.util.spec_from_file_location('compat',root/'tools/verify-pagemap-compatibility.py');compat=importlib.util.module_from_spec(spec);spec.loader.exec_module(compat)
base=root/'out/t3-1c-probe';cases=[]
for dataset,inputs,candidates in [('fixed','type3-final','gate-fixed'),('additional','candidate-fixtures-final','gate-additional'),('skia','skia-fixtures','skia-candidates'),('skia-local','skia-local-fixtures','skia-local-candidates'),('causal','causal-fixtures-final','causal-candidates')]:
 for r in json.loads((base/candidates/'candidates.json').read_text()):
  cases.append(dict(name=dataset+'-'+r['folder'],paths=[base/inputs/r['id']/(s+'.pdf') for s in (['b','a'] if r['reverse'] else ['a','b'])],selected=r.get('selected'),numeric='terminal-number' if dataset=='causal' and r['id']=='flow-number-change' else None,reverse=r['reverse']))
for r in json.loads((root/'out/t3-1c-multiple/verified/multiple-causes.json').read_text()):
 cases.append(dict(name='multiple-'+r['run'],paths=[root/'tests/ReportDiff.Tests/Fixtures/page-flow-multiple'/r['id']/(s+'.pdf') for s in (['b','a'] if r['reverse'] else ['a','b'])],selected=None,numeric={'nonflow-terminal-number':'multiple-terminal','independent-number-change':'multiple-final'}.get(r['id']),reverse=r['reverse']))
diagnostic=json.loads((root/'out/t3-1c-numeric/final-verified/numeric.json').read_text())
config=out/'carry.yaml';config.write_text('rows: {enabled: true, carry_enabled: true}\nreport: {raw_overlay: true}\n')
metrics=dict(cases=0,processes=0,unchanged_cases=0,unchanged_files=0,approved_numeric_cases=0,raw_png_pairs=0);records=[]
for case in cases:
 reports={}
 for label,cli in [('before',old),('after',current)]:
  directory=out/(case['name']+'-'+label);args=['dotnet',str(cli),'compare',*map(str,case['paths']),'--config',str(config),'--out',str(directory),'--quiet']
  if case['selected'] is not None:args += ['--pages',','.join(map(str,case['selected']))]
  result=subprocess.run(args,capture_output=True,text=True);assert result.returncode==1,(case['name'],label,result.stdout,result.stderr)
  reports[label]=(directory,json.loads((directory/'result.json').read_text()));metrics['processes']+=1
 d0,before=reports['before'];d1,after=reports['after']
 if case['numeric'] is None:
  first,second=compat.snapshot(d0),compat.snapshot(d1);assert first==second,(case['name'],'承認対象外の変更')
  metrics['unchanged_cases']+=1;metrics['unchanged_files']+=len(first)
 else:
  expected=next(r for r in diagnostic if r['id']==case['numeric'] and r['reverse']==case['reverse']);assert expected['input']['gate_ready']
  assert after['page_flow']['status']=='applied' and after['page_flow']['aggregation']['status']=='grouped'
  for key in ['difference_count','aggregated_difference_count']:assert after['summary'][key]==expected['candidate'][key]
  assert sum(len(p['clusters']) for p in after['pages'])==sum(len(p['content_clusters']) for p in expected['projections'])==1
  assert after['summary']['status']==before['summary']['status']=='different';metrics['approved_numeric_cases']+=1
 for a,b in zip(before['pages'],after['pages'],strict=True):
  for x,y in [(a['raw_evidence'][s]['image'],b['raw_evidence'][s]['image']) for s in ['a','b']]+[(a['raw_evidence']['overlay'],b['raw_evidence']['overlay'])]:
   assert (d0/x).read_bytes()==(d1/y).read_bytes();metrics['raw_png_pairs']+=1
 metrics['cases']+=1;records.append(dict(case=case['name'],numeric=case['numeric'],before=before['summary'],after=after['summary']))
 print(case['name'],after['page_flow']['status'],after['summary']['difference_count'],after['summary']['aggregated_difference_count'],flush=True)
 (out/'verification.json').write_text(json.dumps(dict(metrics=metrics,records=records),indent=2)+'\n')
