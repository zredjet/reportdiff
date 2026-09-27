#!/usr/bin/env python3
"""実PDF6方向のCLI全工程を別プロセスで反復し、時間・RSSと出力の不変性を記録する。"""
import datetime
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time

root = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=False)
cli = root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
fixtures = root/'tests/ReportDiff.Tests/Fixtures'
records = []

def stamp(text):
    date = datetime.datetime.fromisoformat(text)
    return date.strftime('%Y-%m-%d %H:%M:%S ') + date.strftime('%z')[:3] + ':' + date.strftime('%z')[3:]

def same_outputs(first, second):
    a = json.loads((first/'result.json').read_text()); b = json.loads((second/'result.json').read_text())
    old, new = a['generated_at'], b['generated_at']; b['generated_at'] = old
    assert a == b
    aa = {p.relative_to(first): p for p in first.rglob('*') if p.is_file()}
    bb = {p.relative_to(second): p for p in second.rglob('*') if p.is_file()}
    assert aa.keys() == bb.keys()
    for name in aa:
        if str(name) == 'result.json': continue
        if str(name) == 'report.html':
            assert aa[name].read_text() == bb[name].read_text().replace(new, old).replace(stamp(new), stamp(old))
        else: assert aa[name].read_bytes() == bb[name].read_bytes(), name

for variant, raw in [('original', 0), ('tone', 3807), ('context', 956)]:
    folder = fixtures/('page-flow-same-page-support/same-page-two' if variant == 'original' else 'page-flow-anchored-acceptance/'+variant)
    for direction in ['ab', 'ba']:
        runs = []
        for attempt in range(3):
            for enabled in [False, True]:
                mode = 'on' if enabled else 'off'; name = f'{variant}-{direction}-{mode}-{attempt}'; dest = out/name
                config = out/(mode+'.yaml')
                config.write_text(f'rows: {{enabled: true, carry_enabled: {str(enabled).lower()}}}\nreport: {{raw_overlay: true}}\n')
                command = ['dotnet', str(cli), 'compare', str(folder/(direction[0]+'.pdf')), str(folder/(direction[1]+'.pdf')),
                    '--config', str(config), '--out', str(dest), '--quiet']
                start = time.perf_counter()
                with (out/(name+'.log')).open('w') as log:
                    child = subprocess.Popen(command, cwd=root, stdout=log, stderr=subprocess.STDOUT)
                    _, status, usage = os.wait4(child.pid, 0)
                seconds = time.perf_counter()-start; child.returncode = os.waitstatus_to_exitcode(status)
                assert child.returncode == 1, (name, child.returncode)
                report = json.loads((dest/'result.json').read_text())
                audit = report.get('page_flow', {}).get('anchored_content')
                if enabled:
                    assert audit['status'] == 'applied'
                    assert sum(p['raw_pixels'] for p in report['pages']) == raw
                    assert report['summary']['difference_count'] == (6 if raw == 0 else 7)
                    assert report['summary']['aggregated_difference_count'] == (2 if raw == 0 else 3)
                    baseline = out/f'{variant}-{direction}-off-{attempt}'
                    off = json.loads((baseline/'result.json').read_text())
                    for a, b in zip(off['pages'], report['pages'], strict=True):
                        for image in ['overlay']:
                            assert (baseline/a['raw_evidence'][image]).read_bytes() == (dest/b['raw_evidence'][image]).read_bytes()
                if attempt: same_outputs(out/f'{variant}-{direction}-{mode}-0', dest)
                runs.append(dict(attempt=attempt, enabled=enabled, seconds=seconds,
                    peak_rss_bytes=usage.ru_maxrss*(1 if sys.platform == 'darwin' else 1024),
                    difference_count=report['summary']['difference_count'], aggregate=report['summary']['aggregated_difference_count'],
                    budget=audit['budget'] if audit else None))
        records.append(dict(run=variant+'-'+direction, runs=runs, summary=[dict(enabled=enabled,
            median_seconds=statistics.median(r['seconds'] for r in runs if r['enabled'] == enabled),
            max_rss_bytes=max(r['peak_rss_bytes'] for r in runs if r['enabled'] == enabled)) for enabled in [False, True]]))
        print(variant, direction, '完了', flush=True)
(out/'metrics.json').write_text(json.dumps(dict(platform=sys.platform, rss_source='wait4.ru_maxrss',
    processes=36, repeats=3, real_pdf=True, synthetic=True, full_cli=True, html=True, raw_overlay=True,
    repeated_output='identical except generated_at', on_off_raw_overlay='byte identical', records=records), ensure_ascii=False, indent=2)+'\n')
