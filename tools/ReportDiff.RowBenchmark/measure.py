#!/usr/bin/env python3
"""通常CLIと計測コピーを別プロセスで逐次測定し、全出力の一致も確認する（macOS）。"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import statistics
import subprocess
import time

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('compat', root / 'tools/verify-pagemap-compatibility.py')
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('inputs', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--cli', type=Path, default=root / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll')
parser.add_argument('--instrumented', type=Path, required=True)
parser.add_argument('--iterations', type=int, default=3)
args = parser.parse_args()
if platform.system() != 'Darwin' or args.iterations < 1:
    parser.error('macOSで1回以上の計測を指定してください。')
args.output = args.output.resolve()
args.inputs = args.inputs.resolve()
args.output.mkdir(parents=True, exist_ok=False)


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


regions = [dict(name='左の厳密比較', page='all', x=0, y=0, w=105, h=297, profile='strict'),
           dict(name='右の位置許容', page='all', x=110, y=0, w=100, h=297, profile='loose')]
cases = []
for scenario, variants in [('identical', 1), ('adopted', 1), ('columns', 1), ('low-improvement', 1), ('regions-2', 2), ('regions-3', 3)]:
    pair = 'columns' if scenario == 'columns' else 'adopted' if scenario == 'low-improvement' else 'short'
    for enabled in [False, True]:
        name = scenario + ('-on' if enabled else '-off')
        config = args.output / (name + '.yaml')
        config.write_text(json.dumps(dict(dpi=300, rows=dict(enabled=enabled), report=dict(raw_overlay=True),
                                         regions=regions[:variants - 1]), ensure_ascii=False))
        cases.append(dict(id=name, scenario=scenario, enabled=enabled, variants=variants, config=str(config),
                          a=str(args.inputs / (pair + '-a.pdf')),
                          b=str(args.inputs / (pair + ('-a.pdf' if scenario == 'identical' else '-b.pdf')))))


def run(case, kind, iteration):
    name = f'{case["id"]}-{kind}-{iteration}'
    output = args.output / name
    env = os.environ.copy()
    cli = args.cli if kind == 'product' else args.instrumented
    metric_path = args.output / (name + '-stages.json')
    if kind != 'product':
        env['REPORTDIFF_ROW_METRICS'] = str(metric_path)
    command = ['dotnet', str(cli.resolve()), 'compare', case['a'], case['b'],
               '--config', case['config'], '--out', str(output), '--quiet']
    start = time.perf_counter()
    # wait4はこの子プロセスの最大RSSを返す。累積したRUSAGE_CHILDRENの最大値は使わない。
    with (args.output / (name + '.stderr')).open('w+') as stderr:
        process = subprocess.Popen(command, env=env, stdout=subprocess.DEVNULL, stderr=stderr)
        _, status, usage = os.wait4(process.pid, 0)
        process.returncode = os.waitstatus_to_exitcode(status)
        stderr.seek(0)
        error = stderr.read()
    elapsed = (time.perf_counter() - start) * 1000
    assert process.returncode == (0 if case['scenario'] == 'identical' else 1), (name, error)
    assert usage.ru_maxrss > 0
    report = json.loads((output / 'result.json').read_text())
    page = report['pages'][0]
    expected = 'disabled' if not case['enabled'] else 'identical' if case['scenario'] == 'identical' else 'column_conflict' if case['scenario'] == 'columns' else 'low_improvement' if case['scenario'] == 'low-improvement' else 'applied'
    assert page['row_alignment']['reason'] == expected, (name, page['row_alignment'])
    assert len(page.get('regions', {}).get('runs', [0])) == case['variants']
    if expected == 'applied' and case['variants'] == 1:
        assert report['summary']['difference_count'] == 3 and report['summary']['clusters'] == 1
    snapshot = compat.snapshot(output)
    prior = expected_outputs.setdefault(case['id'], snapshot)
    assert snapshot == prior, (name, [k for k in snapshot.keys() | prior.keys() if snapshot.get(k) != prior.get(k)])
    stages = json.loads(metric_path.read_text()) if kind != 'product' else []
    result = dict(id=name, elapsed_ms=elapsed, peak_rss_bytes=usage.ru_maxrss, exit_code=process.returncode,
                  output_bytes=sum(p.stat().st_size for p in output.rglob('*') if p.is_file()),
                  png_bytes=sum(p.stat().st_size for p in output.rglob('*.png')),
                  png_count=len(list(output.rglob('*.png'))), json_bytes=(output / 'result.json').stat().st_size,
                  html_bytes=(output / 'report.html').stat().st_size, reason=expected,
                  summary=report['summary'], aligned_size_px=page['row_alignment']['aligned_size_px'],
                  display_size_px=page['size_px'], stages=stages)
    print(name, round(elapsed, 2), result['peak_rss_bytes'], expected, flush=True)
    return result


expected_outputs = {}
records = []
# 準備実行は条件ごと・バイナリごとに1回。製品/計測コピーは並行しない。
for iteration in range(args.iterations + 1):
    for case in (cases if iteration % 2 == 0 else list(reversed(cases))):
        for kind in (['product', 'instrumented'] if iteration % 2 == 0 else ['instrumented', 'product']):
            result = run(case, kind, iteration)
            records.append(dict(case=case['id'], kind=kind, warmup=iteration == 0, **result))
    (args.output / 'checkpoint.json').write_text(json.dumps(records, ensure_ascii=False, indent=2) + '\n')
summary = []
for case in cases:
    item = dict(case=case, output_hashes=expected_outputs[case['id']])
    for kind in ['product', 'instrumented']:
        runs = [r for r in records if r['case'] == case['id'] and r['kind'] == kind and not r['warmup']]
        item[kind] = dict(median_ms=statistics.median(r['elapsed_ms'] for r in runs),
                          min_ms=min(r['elapsed_ms'] for r in runs), max_ms=max(r['elapsed_ms'] for r in runs),
                          median_peak_rss_bytes=statistics.median(r['peak_rss_bytes'] for r in runs),
                          max_peak_rss_bytes=max(r['peak_rss_bytes'] for r in runs),
                          output_bytes_median=statistics.median(r['output_bytes'] for r in runs),
                          png_bytes=runs[0]['png_bytes'], png_count=runs[0]['png_count'])
        if kind == 'instrumented':
            labels = sorted({s['stage'] for r in runs for s in r['stages']})
            item[kind]['stages_median_ms'] = {label: statistics.median(sum(s['ms'] for s in r['stages'] if s['stage'] == label) for r in runs) for label in labels}
    summary.append(item)
data = dict(platform=platform.platform(), cpu_count=os.cpu_count(), dpi=300, iterations=args.iterations, warmups=1,
            cli_sha256=digest(args.cli), instrumented_cli_sha256=digest(args.instrumented),
            input_sha256={str(p): digest(p) for p in args.inputs.glob('*.pdf')},
            scope='独立したCLIプロセスの起動・PDF描画・比較・注釈・HTML/JSON/PNG/raw evidence保存。macOS wait4の子プロセス別最大RSS（bytes）。全実行は逐次。',
            all_product_instrumented_outputs_equal_except_generated_at=True, measurements=summary, runs=records)
(args.output / 'verification.json').write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n')
