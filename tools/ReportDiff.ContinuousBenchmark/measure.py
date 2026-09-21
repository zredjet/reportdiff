"""反復PDF専用。通常CLIと別プローブを逐次実行し、単ページとのJSON・全PNG一致を検査する。"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def expand_page(page, number):
    result = copy.deepcopy(page)
    result['page'] = number
    for paths in [result['images'], *(c['crops'] for c in result['clusters'])]:
        for key, value in paths.items():
            if value is not None:
                assert re.match(r'^(pages|crops)/p001_', value), value
                paths[key] = value.replace('/p001_', f'/p{number:03d}_', 1)
    return result


def check_report(output, reference, count, pair):
    report = json.loads((output / 'result.json').read_text())
    for key in ['schema_version', 'tool', 'config']:
        assert report[key] == reference[key], key
    assert report['pages'] == [expand_page(reference['pages'][0], n) for n in range(1, count + 1)], 'ページ内容'
    summary = reference['summary'].copy()
    for key in ['pages_compared', 'pages_different', 'clusters', 'absorbed_groups']:
        summary[key] *= count
    assert report['summary'] == summary, '集計'
    warnings = []
    for n in range(1, count + 1):
        for warning in reference['warnings']:
            item = warning.copy()
            message, replacements = re.subn(r'^((?:[AB]・)?)1 ページ:', rf'\g<1>{n} ページ:', item['message'])
            assert replacements == 1, 'ページ別でない警告はこのツールの対象外'
            item['message'] = message
            warnings.append(item)
    assert report['warnings'] == warnings, '警告'
    for side, path in zip(['a', 'b'], pair):
        assert report['inputs'][side] == dict(path=str(path.resolve()), type='pdf', pages=count, sha256=sha256(path)), side
    return report


def check_pngs(output, reference, count):
    expected = {name.replace('/p001_', f'/p{n:03d}_', 1): digest
                for n in range(1, count + 1) for name, digest in reference.items()}
    actual = {str(p.relative_to(output)): sha256(p) for p in sorted(output.rglob('*.png'))}
    assert actual == expected, 'PNGのパス・件数・SHA-256'
    assert (output / 'report.html').is_file(), 'HTML未生成'
    return len(actual)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('binary', type=Path)
    parser.add_argument('inputs', type=Path, help='1/20/100-a.pdfと1/20/100-b.pdfのディレクトリ')
    parser.add_argument('output', type=Path)
    parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--probe-repeats', type=int, default=2)
    args = parser.parse_args()
    if sys.platform != 'darwin' or args.output.exists() or min(args.repeats, args.probe_repeats) < 1:
        parser.error('macOS上で未使用出力先と正の反復数を指定してください。')
    args.output.mkdir(parents=True)
    records = []

    def run(dpi, scenario, count, kind, iteration, reference=None, png_reference=None):
        pair = [(args.inputs / f'{count}-{side}.pdf').resolve() for side in ('a', 'b' if scenario == 'different' else 'a')]
        name = f'{dpi}-{scenario}-{count}-{kind}-{iteration}'
        output = args.output / name
        command = (['dotnet', str(args.binary / 'ReportDiff.Tests.dll'), 'probe', *map(str, pair), str(output), str(dpi)]
                   if kind == 'probe' else ['dotnet', str(args.binary / 'reportdiff.dll'), 'compare', *map(str, pair),
                                           '--out', str(output), '--dpi', str(dpi), '--save-all-pages'])
        started = time.perf_counter()
        execution = subprocess.run(['/usr/bin/time', '-l', *command], capture_output=True, text=True)
        elapsed = (time.perf_counter() - started) * 1000
        (args.output / f'{name}.stdout').write_text(execution.stdout)
        (args.output / f'{name}.stderr').write_text(execution.stderr)
        assert execution.returncode == (0 if kind == 'probe' or scenario == 'same' else 1), (name, execution.stderr)
        report = json.loads((output / 'result.json').read_text())
        if reference is None:
            reference = report
            png_reference = {str(p.relative_to(output)): sha256(p) for p in output.rglob('*.png')}
        check_report(output, reference, count, pair)
        png_count = check_pngs(output, png_reference, count)
        record = dict(name=name, dpi=dpi, scenario=scenario, count=count, kind=kind, iteration=iteration,
                      elapsed_ms=elapsed, peak_rss_bytes=int(re.search(r'(\d+)\s+maximum resident set size', execution.stderr)[1]),
                      json_equal=True, png_sha256_equal=True, png_count=png_count)
        if kind == 'probe':
            record['profile'] = json.loads(execution.stdout)
            assert len(record['profile']['pages']) == count
            assert all(s['rss_bytes'] > 0 for s in record['profile']['stages'])
            cli = json.loads((args.output / f'{dpi}-{scenario}-{count}-cli-1/result.json').read_text())
            assert {k: v for k, v in cli.items() if k != 'generated_at'} == {k: v for k, v in report.items() if k != 'generated_at'}, 'CLIとプローブ'
        records.append(record)
        (args.output / 'records.json').write_text(json.dumps(records, indent=2) + '\n')
        print(name, f'{elapsed / 1000:.3f}s', f'{record["peak_rss_bytes"] / 1e9:.3f}GB', f'{png_count} PNG一致', flush=True)
        return reference, png_reference

    for dpi in (300, 400):
        for scenario in ('different', 'same'):
            reference, png = run(dpi, scenario, 1, 'reference', 0)
            run(dpi, scenario, 20, 'warmup', 0, reference, png)
            for iteration in range(1, args.repeats + 1):
                for count in ((20, 100) if iteration % 2 else (100, 20)):
                    run(dpi, scenario, count, 'cli', iteration, reference, png)
            for iteration in range(1, args.probe_repeats + 1):
                run(dpi, scenario, 100, 'probe', iteration, reference, png)
    summary = []
    for dpi in (300, 400):
        for scenario in ('different', 'same'):
            for count, kind in ((20, 'cli'), (100, 'cli'), (100, 'probe')):
                group = [r for r in records if (r['dpi'], r['scenario'], r['count'], r['kind']) == (dpi, scenario, count, kind)]
                summary.append(dict(dpi=dpi, scenario=scenario, count=count, kind=kind,
                                    **{key: dict(median=statistics.median(r[key] for r in group),
                                                 min=min(r[key] for r in group), max=max(r[key] for r in group))
                                       for key in ['elapsed_ms', 'peak_rss_bytes']}))
    result = dict(records=records, summary=summary,
                  binaries={p.name: sha256(p) for p in args.binary.glob('*.dll')},
                  inputs={p.name: sha256(p) for p in args.inputs.glob('*.pdf')})
    (args.output / 'summary.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__':
    main()
