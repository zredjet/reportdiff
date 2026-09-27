#!/usr/bin/env python3
"""最終ソースの送り有効ケースを再生成し、独立照合済みの全出力と日時以外を比較する。"""
import importlib.util, json, subprocess, sys
from pathlib import Path
root=Path(__file__).resolve().parents[2];out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
verified=Path(sys.argv[2]).resolve();fixture=root/'tests/ReportDiff.Tests/Fixtures'/('page-flow-shared' if len(sys.argv)>3 and sys.argv[3]=='--shared' else 'page-flow-numeric')
spec=importlib.util.spec_from_file_location('compat',root/'tools/verify-pagemap-compatibility.py');compat=importlib.util.module_from_spec(spec);spec.loader.exec_module(compat)
cli=root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
config=out/'carry.yaml';config.write_text('rows: {enabled: true, carry_enabled: true}\nreport: {raw_overlay: true}\n')
records=[];files=0
for r in json.loads((fixture/'expected.json').read_text()):
 paths=[fixture/r['id']/(s+'.pdf') for s in (['b','a'] if r['reverse'] else ['a','b'])];target=out/r['run']
 result=subprocess.run(['dotnet',str(cli),'compare',*map(str,paths),'--config',str(config),'--out',str(target),'--quiet'],capture_output=True,text=True)
 assert result.returncode==1,(r['run'],result.stderr)
 actual=compat.snapshot(target);assert actual==compat.snapshot(verified/(r['run']+'-on')),r['run']
 files+=len(actual);records.append(r['run']);print(r['run'],'全出力一致',flush=True)
 (out/'verification.json').write_text(json.dumps(dict(runs=len(records),files=files,records=records),ensure_ascii=False,indent=2)+'\n')
