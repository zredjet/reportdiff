#!/usr/bin/env python3
"""末尾ページの追加8方向を旧CLI／無効／有効で交互に反復する。重い検証と同時に実行しない。"""
import datetime
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
out = folder / 'measure'
out.mkdir(exist_ok=False)
current = root / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
old = folder / 'baseline-cli/reportdiff.dll'
fixture = root / 'tests/ReportDiff.Tests/Fixtures/page-flow-unpaired'
records = []


def stamp(text):
    date = datetime.datetime.fromisoformat(text)
    return date.strftime('%Y-%m-%d %H:%M:%S ') + date.strftime('%z')[:3] + ':' + date.strftime('%z')[3:]


def same_outputs(first, second):
    a = json.loads((first / 'result.json').read_text())
    b = json.loads((second / 'result.json').read_text())
    old_date, new_date = a['generated_at'], b['generated_at']
    b['generated_at'] = old_date
    assert a == b
    paths = {p.relative_to(first) for p in first.rglob('*') if p.is_file()}
    assert paths == {p.relative_to(second) for p in second.rglob('*') if p.is_file()}
    for name in paths:
        if name.name == 'result.json':
            continue
        if name.name == 'report.html':
            assert (first / name).read_text() == (second / name).read_text().replace(new_date, old_date).replace(stamp(new_date), stamp(old_date))
        else:
            assert (first / name).read_bytes() == (second / name).read_bytes()


for variant in ['independent-terminal', 'independent-opposite', 'neutral-between', 'independent-tone']:
    for direction in ['ab', 'ba']:
        runs = []
        for attempt in range(3):
            for mode, cli, carry in [('old-on', old, True), ('new-off', current, False), ('new-on', current, True)]:
                name = f'{variant}-{direction}-{mode}-{attempt}'
                target = out / name
                config = out / (mode + '.yaml')
                config.write_text('rows: {enabled: true, carry_enabled: ' + str(carry).lower() + '}\nreport: {raw_overlay: true}\n')
                args = ['dotnet', str(cli), 'compare', str(fixture / variant / (direction[0] + '.pdf')),
                        str(fixture / variant / (direction[1] + '.pdf')), '--config', str(config), '--out', str(target), '--quiet']
                start = time.perf_counter()
                with (out / (name + '.log')).open('w') as log:
                    child = subprocess.Popen(args, cwd=root, stdout=log, stderr=subprocess.STDOUT)
                    _, status, usage = os.wait4(child.pid, 0)
                seconds = time.perf_counter() - start
                child.returncode = os.waitstatus_to_exitcode(status)
                assert child.returncode == 1
                result = json.loads((target / 'result.json').read_text())
                total, aggregate = result['summary']['difference_count'], result['summary']['aggregated_difference_count']
                assert not result['summary']['difference_count_complete']
                if mode == 'new-on':
                    assert (total, aggregate) == ((12, 3) if variant == 'independent-tone' else (11, 2))
                    assert not result['summary']['aggregated_difference_count_complete']
                    for other_mode in ['old-on', 'new-off']:
                        other_path = out / f'{variant}-{direction}-{other_mode}-{attempt}'
                        other = json.loads((other_path / 'result.json').read_text())
                        for a, b in zip(other['pages'], result['pages'], strict=True):
                            for x, y in [(a['raw_evidence'][s]['image'], b['raw_evidence'][s]['image']) for s in 'ab'] + [
                                         (a['raw_evidence']['overlay'], b['raw_evidence']['overlay'])]:
                                assert (other_path / x).read_bytes() == (target / y).read_bytes()
                if attempt:
                    same_outputs(out / f'{variant}-{direction}-{mode}-0', target)
                runs.append(dict(attempt=attempt, mode=mode, seconds=seconds,
                                 peak_rss_bytes=usage.ru_maxrss * (1 if sys.platform == 'darwin' else 1024), total=total, aggregate=aggregate))
        records.append(dict(run=variant + '-' + direction, runs=runs, summary=[dict(mode=mode,
                            median_seconds=statistics.median(r['seconds'] for r in runs if r['mode'] == mode),
                            max_rss_bytes=max(r['peak_rss_bytes'] for r in runs if r['mode'] == mode)) for mode in ['old-on', 'new-off', 'new-on']]))
        (out / 'metrics.json').write_text(json.dumps(dict(processes=sum(len(r['runs']) for r in records), repeats=3, records=records,
            rss_source='wait4.ru_maxrss', rss_unit='bytes', full_cli=True, synthetic=True, raw_evidence_byte_equal=True,
            repeated_output_equal_except_generated_at=True, concurrent_heavy_validation=False), indent=2) + '\n')
        print(variant, direction, '計測完了', flush=True)
