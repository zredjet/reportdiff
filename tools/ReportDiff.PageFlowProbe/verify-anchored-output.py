#!/usr/bin/env python3
"""保存済みCLI26方向を基準に、A3の採用差分2方向と他のバイト互換を分けて検査する。"""
from pathlib import Path
import datetime, json, subprocess, sys
root = Path(__file__).resolve().parents[2]
baseline = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
records = []
def html_stamp(stamp):
    date = datetime.datetime.fromisoformat(stamp)
    return date.strftime('%Y-%m-%d %H:%M:%S ') + date.strftime('%z')[:3] + ':' + date.strftime('%z')[3:]
for oldpath in sorted(baseline.glob('*/result.json')):
    old = json.loads(oldpath.read_text()); name = oldpath.parent.name; dest = out/name
    run = subprocess.run(['dotnet', str(root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'),
        'compare', old['inputs']['a']['path'], old['inputs']['b']['path'], '--config', str(root/'examples/page-flow.yaml'),
        '--out', str(dest), '--force'], capture_output=True, text=True)
    assert run.returncode == 1, (name, run.stderr)
    got = json.loads((dest/'result.json').read_text()); stamp = got['generated_at']; got['generated_at'] = old['generated_at']
    a = {str(p.relative_to(oldpath.parent)): p for p in oldpath.parent.rglob('*') if p.is_file()}
    b = {str(p.relative_to(dest)): p for p in dest.rglob('*') if p.is_file()}
    if name.startswith('same-page-two-'):
        assert got['summary']['clusters'] == 0 and got['summary']['difference_count'] == 6 and got['summary']['aggregated_difference_count'] == 2
        assert got['page_flow']['anchored_content']['status'] == 'applied'
        mode = 'expected_new_route'; files = 0
    else:
        assert got == old, name
        assert a.keys() == b.keys(), name
        for f in a:
            if f == 'result.json': continue
            if f == 'report.html':
                assert b[f].read_text().replace(stamp, old['generated_at']).replace(html_stamp(stamp), html_stamp(old['generated_at'])) == a[f].read_text(), (name, f)
            else: assert a[f].read_bytes() == b[f].read_bytes(), (name, f)
        mode = 'identical_except_timestamp'; files = len(a)
    for pa, pb in zip(old['pages'], got['pages'], strict=True):
        assert a[pa['raw_evidence']['overlay']].read_bytes() == b[pb['raw_evidence']['overlay']].read_bytes(), (name, 'raw evidence')
    records.append(dict(run=name, status=mode, files=files, old_difference_count=old['summary']['difference_count'],
        new_difference_count=got['summary']['difference_count'], aggregate=got['summary']['aggregated_difference_count']))
    print(name, mode, flush=True)
assert len(records) == 26
(out/'verification.json').write_text(json.dumps(records, indent=2)+'\n')
