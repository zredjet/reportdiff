#!/usr/bin/env python3
"""A4と別の描画対照を逐次計測し、採用期待の未成立もそのまま記録する。"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import time

root=Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('workspace',type=Path)
p.add_argument('output',type=Path)
p.add_argument('--instrumented',type=Path,required=True)
args=p.parse_args(); base=args.workspace.resolve(); output=args.output.resolve()
output.mkdir(parents=True,exist_ok=False)
if not hasattr(os,'wait4'):raise RuntimeError('macOS/Linuxのwait4が必要です。')
spec=importlib.util.spec_from_file_location('compat',root/'tools/verify-pagemap-compatibility.py')
compat=importlib.util.module_from_spec(spec); spec.loader.exec_module(compat)
digest=lambda f:hashlib.sha256(Path(f).read_bytes()).hexdigest()
cli=root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
cases=[]
for label,folder,only in [('skia','inputs',None),('sparse','inputs-sparse',None),
                           ('inset','inputs-control',{'a4-2'}),('fractional','inputs-grid',{'a4-2'}),('integer','inputs-integer',{'a4-2'})]:
    manifest=json.load(open(base/folder/'manifest.json'))
    for case in manifest['cases']:
        if only and case['test']['id'] not in only:continue
        files=[base/folder/case['test']['id']/(s+'.pdf') for s in ['a','b']]
        for s,f in zip(['a','b'],files):assert digest(f)==case[s+'_sha256'],f
        cases.append(dict(label=label,id=label+'-'+case['test']['id'],files=list(map(str,files)),
                          repeats=case['test']['repeats'] if only is None else 1,expected=case))
records=[]; snapshots={}; first={}; summaries=[]
for case in cases:
    for iteration in range(case['repeats']):
        for enabled in ([False,True] if iteration%2==0 else [True,False]):
            # 計測コピーは最初の反復だけ。製品時間とは分けて保存する。
            for kind in (['product','instrumented'] if iteration==0 else ['product']):
                key=case['id']+'-'+str(enabled).lower(); name=f'{key}-{kind}-{iteration}'
                directory=output/name; config=output/(key+'.yaml'); metric=output/(name+'-stages.json')
                if not config.exists():config.write_text(f'rows: {{enabled: true, carry_enabled: {str(enabled).lower()}}}\nreport: {{raw_overlay: true}}\n')
                binary=cli if kind=='product' else args.instrumented.resolve()
                env=os.environ.copy()
                if kind=='instrumented':env['REPORTDIFF_FLOW_METRICS']=str(metric)
                command=['dotnet',str(binary),'compare',*case['files'],'--config',str(config),'--out',str(directory),'--quiet']
                start=time.perf_counter()
                with (output/(name+'.log')).open('w') as log:
                    child=subprocess.Popen(command,cwd=root,env=env,stdout=log,stderr=subprocess.STDOUT)
                    _,status,usage=os.wait4(child.pid,0)
                child.returncode=os.waitstatus_to_exitcode(status); elapsed=time.perf_counter()-start
                assert child.returncode==1,(name,child.returncode)
                report=json.load(open(directory/'result.json')); flow=report.get('page_flow')
                assert report['summary']['status']=='different' and report['summary']['difference_count']>0,name
                snap=compat.snapshot(directory)
                if key in snapshots:assert snap==snapshots[key],(name,'product_instrumented_or_repeat_changed')
                else:snapshots[key]=snap
                raw={str(page['page'])+'-'+part:digest(directory/page['raw_evidence'][part]['image'])
                     for page in report['pages'] for part in ['a','b']}
                raw.update({str(page['page'])+'-overlay':digest(directory/page['raw_evidence']['overlay']) for page in report['pages']})
                first.setdefault((case['id'],enabled),(report,raw))
                status=flow['status'] if flow else 'disabled'
                if enabled:
                    assert flow is not None
                    if status=='skipped':assert report['summary']['aggregated_difference_count']==report['summary']['difference_count']
                    if case['expected']['test']['pages']==31:assert status=='skipped' and 'flow_pixel_limit' in flow['reasons'],name
                    if case['label']=='sparse':
                        assert status=='applied' and report['summary']['clusters']==0,name
                        assert report['summary']['difference_count']==3*case['expected']['test']['pages']-1,name
                        assert report['summary']['aggregated_difference_count']==1,name
                stages=json.load(open(metric)) if kind=='instrumented' else []
                if stages:
                    ids={e['id'] for e in stages}
                    assert all(e['parent'] is None or e['parent'] in ids for e in stages)
                    assert sum(e['stage']=='pdf_render' for e in stages)>0
                unmet=bool(enabled and (status!=case['expected']['expected_flow'] or
                           case['expected']['expected_aggregate'] is not None and report['summary']['aggregated_difference_count']!=case['expected']['expected_aggregate']))
                files=[f for f in directory.rglob('*') if f.is_file()]
                row=dict(id=name,case=case['id'],carry_enabled=enabled,kind=kind,iteration=iteration,
                         seconds=elapsed,peak_rss_bytes=usage.ru_maxrss*(1 if platform.system()=='Darwin' else 1024),
                         output_bytes=sum(f.stat().st_size for f in files),png_bytes=sum(f.stat().st_size for f in files if f.suffix=='.png'),
                         png_count=sum(f.suffix=='.png' for f in files),status=status,reasons=flow['reasons'] if flow else [],
                         summary=report['summary'],canvas_px=report['pages'][0]['raw_evidence']['canvas_size_px'],
                         expectation_unmet=unmet,stages=stages,result_sha256=digest(directory/'result.json'))
                records.append(row)
                (output/'checkpoint.json').write_text(json.dumps(records,ensure_ascii=False,indent=2)+'\n')
                print(name,round(elapsed,3),round(row['peak_rss_bytes']/1024**2,1),status,'期待未成立' if unmet else '',flush=True)
    off,raw_off=first[case['id'],False]; on,raw_on=first[case['id'],True]
    assert raw_off==raw_on,(case['id'],'raw_evidence_changed')
    if on['page_flow']['status']=='skipped':assert off['pages']==on['pages'],(case['id'],'fallback_page_changed')
    summaries.append(dict(case=case,raw_evidence_png_pairs=len(raw_off),fallback_pages_equal=on['page_flow']['status']=='skipped',
        variants=[dict(carry_enabled=en,processes=len(runs),median_seconds=statistics.median(r['seconds'] for r in runs),
                       min_seconds=min(r['seconds'] for r in runs),max_seconds=max(r['seconds'] for r in runs),
                       max_rss_bytes=max(r['peak_rss_bytes'] for r in runs),median_output_bytes=statistics.median(r['output_bytes'] for r in runs),
                       status=runs[0]['status'],expectation_unmet=runs[0]['expectation_unmet'],summary=runs[0]['summary'])
                  for en in [False,True] for runs in [[r for r in records if r['case']==case['id'] and r['carry_enabled']==en and r['kind']=='product']]]))
(output/'verification.json').write_text(json.dumps(dict(platform=platform.platform(),dpi=300,processes=len(records),
    warmups=0,cold_cache_claim=False,product_source_unchanged=True,raw_evidence_equal=True,all_output_equivalent_except_generated_at=True,
    cli_sha256=digest(cli),instrumented_sha256=digest(args.instrumented),cases=summaries,runs=records),ensure_ascii=False,indent=2)+'\n')
print('完了',len(cases),'条件',len(records),'プロセス',flush=True)
