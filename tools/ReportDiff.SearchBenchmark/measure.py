"""ビルド済み比較コピーを交互順で測定し、JSONと全PNGの一致を確認する。"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import statistics
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('variants', type=Path)
parser.add_argument('input_a', type=Path)
parser.add_argument('input_b', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--names', default='baseline,linear,inner2,inner4')
parser.add_argument('--dpi', type=int, default=300)
parser.add_argument('--repeats', type=int, default=5)
parser.add_argument('--probe', action='store_true')
args = parser.parse_args()
if args.output.exists() or args.repeats < 1:
    parser.error('未使用の出力先と1以上の反復数を指定してください。')
args.output.mkdir(parents=True)
names = args.names.split(',')
reference = None
reference_png = None
records = []
for iteration in range(args.repeats + 1):
    for name in names if iteration % 2 == 0 else reversed(names):
        binary = args.variants / name / 'Probe/bin/Release/net10.0'
        output = args.output / f'{name}-{iteration}'
        pair = [str(args.input_a.resolve()), str(args.input_b.resolve())]
        command = (['dotnet', str(binary / 'ReportDiff.Tests.dll'), *pair, str(output), str(args.dpi)]
                   if args.probe else ['dotnet', str(binary / 'reportdiff.dll'), 'compare', *pair,
                                       '--out', str(output), '--dpi', str(args.dpi), '--save-all-pages'])
        started = time.perf_counter()
        run = subprocess.run(['/usr/bin/time', '-l', *command], capture_output=True, text=True)
        elapsed = (time.perf_counter() - started) * 1000
        (args.output / f'{name}-{iteration}.stdout').write_text(run.stdout)
        (args.output / f'{name}-{iteration}.stderr').write_text(run.stderr)
        assert run.returncode == (0 if args.probe else 1), (name, run.stdout, run.stderr)
        report = json.loads((output / 'result.json').read_text())
        report = {key: report[key] for key in ['schema_version', 'tool', 'inputs', 'config', 'summary', 'warnings', 'pages']}
        png = {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest()
               for p in sorted(output.rglob('*.png'))}
        if reference is None:
            reference, reference_png = report, png
        assert report == reference and png == reference_png, (name, '比較結果が一致しません')
        record = dict(variant=name, iteration=iteration, warmup=iteration == 0, elapsed_ms=elapsed,
                      peak_rss_bytes=int(re.search(r'(\d+)\s+maximum resident set size', run.stderr)[1]),
                      json_equal=True, png_sha256_equal=True, png_count=len(png),
                      cpu_count_override=os.environ.get('DOTNET_PROCESSOR_COUNT'))
        if args.probe:
            record['profile'] = json.loads(run.stdout)
        records.append(record)
        print(name, iteration, round(elapsed, 2), record['peak_rss_bytes'], 'exact', flush=True)
summary = dict(dpi=args.dpi, probe=args.probe, records=records,
               inputs={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in [args.input_a, args.input_b]},
               core_sha256={n: hashlib.sha256((args.variants/n/'Probe/bin/Release/net10.0/ReportDiff.Core.dll').read_bytes()).hexdigest() for n in names},
               medians={n: {k: statistics.median(r[k] for r in records if r['variant'] == n and not r['warmup'])
                             for k in ['elapsed_ms', 'peak_rss_bytes']} for n in names})
(args.output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
