#!/usr/bin/env python3
"""複数原因の実CLIを計測。段階タイマーと描画回数はout内のコピーだけに追加する。"""
import hashlib,importlib.util,json,os,platform,statistics,subprocess,sys,time
from pathlib import Path
root=Path(__file__).resolve().parents[2];out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
instrumented=Path(sys.argv[2]).resolve();current=root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
fixtures=root/'tests/ReportDiff.Tests/Fixtures/page-flow-multiple'
baseline=Path(sys.argv[3]).resolve()
numeric=len(sys.argv)>4 and sys.argv[4]=='--numeric'
if numeric:fixtures=root/'tests/ReportDiff.Tests/Fixtures/page-flow-numeric'
shared=len(sys.argv)>4 and sys.argv[4]=='--shared'
ambiguity=len(sys.argv)>4 and sys.argv[4]=='--ambiguity'
if shared or ambiguity:fixtures=root/'tests/ReportDiff.Tests/Fixtures/page-flow-shared'
spec=importlib.util.spec_from_file_location('compat',root/'tools/verify-pagemap-compatibility.py');compat=importlib.util.module_from_spec(spec);spec.loader.exec_module(compat)
records=[];snapshots={};summaries=[]
for case in (['shared-chain','shared-and-independent','same-page-two'] if ambiguity else ['shared-two','independent-and-shared','shared-chain'] if shared else ['terminal-number','multiple-terminal','weak-actual-support'] if numeric else ['independent-inserts','independent-three','independent-band-change']):
 for iteration in range(3):
  for enabled in ([False,True] if iteration%2==0 else [True,False]):
   for kind,cli in (([('product',current),('instrumented',instrumented)] if iteration==0 else [('product',current)]) + ([('before',baseline)] if enabled else [])):
    name=f'{case}-{enabled}-{iteration}-{kind}';folder=out/name;metric=out/(name+'-stages.json');config=out/(case+str(enabled)+'.yaml')
    config.write_text(f'rows: {{enabled: true, carry_enabled: {str(enabled).lower()}}}\nreport: {{raw_overlay: true}}\n')
    env=os.environ.copy()
    if kind=='instrumented':env['REPORTDIFF_FLOW_METRICS']=str(metric)
    start=time.perf_counter()
    with (out/(name+'.log')).open('w') as log:
     p=subprocess.Popen(['dotnet',str(cli),'compare',str(fixtures/case/'a.pdf'),str(fixtures/case/'b.pdf'),'--config',str(config),'--out',str(folder),'--quiet'],cwd=root,env=env,stdout=log,stderr=subprocess.STDOUT)
     _,status,usage=os.wait4(p.pid,0)
    p.returncode=os.waitstatus_to_exitcode(status);elapsed=time.perf_counter()-start;assert p.returncode==1,name
    report=json.loads((folder/'result.json').read_text());snap=compat.snapshot(folder);key=(case,enabled,kind=='before')
    if key in snapshots:assert snapshots[key]==snap,(name,'output mismatch')
    else:snapshots[key]=snap
    stages=json.loads(metric.read_text()) if kind=='instrumented' else []
    row=dict(case=case,carry=enabled,kind=kind,iteration=iteration,seconds=elapsed,peak_rss_bytes=usage.ru_maxrss*(1 if platform.system()=='Darwin' else 1024),stages=stages,status=report.get('page_flow',{}).get('status','disabled'),summary=report['summary'])
    records.append(row);print(name,round(elapsed,3),round(row['peak_rss_bytes']/1024**2,1),flush=True)
 for enabled in [False,True]:
  runs=[r for r in records if r['case']==case and r['carry']==enabled and r['kind']=='product'];instrument=next(r for r in records if r['case']==case and r['carry']==enabled and r['kind']=='instrumented')
  stages=instrument['stages'];summaries.append(dict(case=case,carry=enabled,status=runs[0]['status'],median_seconds=statistics.median(r['seconds'] for r in runs),max_rss_bytes=max(r['peak_rss_bytes'] for r in runs),pdf_renders=sum(s['stage']=='pdf_render' for s in stages),global_estimations=sum(s['stage'] in ['flow_global_align','global_align'] for s in stages),stages={stage:sum(s['ms'] for s in stages if s['stage']==stage) for stage in sorted({s['stage'] for s in stages})}))
(out/'verification.json').write_text(json.dumps(dict(platform=platform.platform(),product_processes=18,baseline_processes=9,instrumented_processes=6,repeats=3,raw_overlay=True,html=True,warmups=0,cold_cache_claim=False,product_instrumented_outputs_equal=True,rss_source='wait4.ru_maxrss',summaries=summaries,records=records),ensure_ascii=False,indent=2)+'\n')
