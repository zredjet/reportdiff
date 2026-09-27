#!/usr/bin/env python3
"""実CLIの領域・全内容除外・L03と、A4計測出力の不変性を検査する。"""
import argparse
import copy
import hashlib
import html
import importlib.util
import json
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('compat', root / 'tools/verify-row-compatibility.py')
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('inputs', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--measurements', type=Path, required=True)
args = parser.parse_args()
args.inputs = args.inputs.resolve()
args.output = args.output.resolve()
args.output.mkdir(parents=True, exist_ok=False)
records = []


def run(name, pair, reverse, config, extra=()):
    a = args.inputs / (pair + ('-b.pdf' if reverse else '-a.pdf'))
    b = args.inputs / (pair + ('-a.pdf' if reverse else '-b.pdf'))
    path = args.output / name
    settings = args.output / (name + '.yaml')
    settings.write_text(json.dumps(config, ensure_ascii=False))
    p = subprocess.run(['dotnet', str(root / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'),
                        'compare', str(a), str(b), '--config', str(settings), '--out', str(path), '--quiet', *extra],
                       capture_output=True, text=True)
    assert not p.stderr, (name, p.stderr)
    report = json.loads((path / 'result.json').read_text())
    assert p.returncode == (0 if report['summary']['status'] == 'same' else 1)
    page = report['pages'][0]
    records.append(dict(id=name, exit_code=p.returncode, summary=report['summary'], row_alignment=page['row_alignment'],
                        clusters=page['clusters'], regions=page.get('regions'),
                        output_sha256={str(f.relative_to(path)): hashlib.sha256(f.read_bytes()).hexdigest()
                                       for f in sorted(path.rglob('*')) if f.is_file()}))
    print(name, page['row_alignment']['reason'], report['summary']['difference_count'], flush=True)
    return path, report


def same_raw(one, two):
    p, a = one
    q, b = two
    a = a['pages'][0]['raw_evidence']
    b = b['pages'][0]['raw_evidence']
    for x, y in [(a['overlay'], b['overlay']), (a['a']['image'], b['a']['image']), (a['b']['image'], b['b']['image'])]:
        assert (p / x).read_bytes() == (q / y).read_bytes()


def disabled(config):
    result = copy.deepcopy(config)
    result['rows']['enabled'] = False
    return result


base = dict(rows=dict(enabled=True), report=dict(raw_overlay=True))
for reverse in [False, True]:
    suffix = '-reverse' if reverse else ''
    config = dict(**base, regions=[dict(name='外側', page='all', x=0, y=0, w=80, h=105, profile='strict'),
                                  dict(name='内側', page='all', x=20, y=15, w=45, h=85, profile='normal')])
    normal = run('nested' + suffix, 'R02', reverse, config)
    page = normal[1]['pages'][0]
    assert page['row_alignment']['reason'] == 'applied' and normal[1]['summary']['difference_count'] == 3
    assert len(page['regions']['runs']) == 2
    assert any(r['display_bounds_px']['h'] > r['content_bounds_px']['h'] for r in page['row_alignment']['regions'])
    text = (normal[0] / 'report.html').read_text()
    snippet = html.unescape(re.search(r'<textarea[^>]*>(.*?)</textarea>', text, re.S)[1]).strip()
    exclusion = json.loads(re.sub(r'(\w+):', r'"\1":', snippet.removeprefix('- ')))
    excluded_config = dict(config, exclude=[exclusion])
    excluded = run('nested-excluded' + suffix, 'R02', reverse, excluded_config)
    assert excluded[1]['summary']['clusters'] == 0 and excluded[1]['summary']['difference_count'] == 2
    audited = run('nested-audit' + suffix, 'R02', reverse, excluded_config, ['--no-regions'])
    assert audited[1]['summary']['difference_count'] == 3 and audited[1]['config']['rows']['enabled']
    assert audited[1]['config']['regions'] == [] and audited[1]['config']['exclude'] == []
    before = run('nested-disabled' + suffix, 'R02', reverse, disabled(config))
    for result in [normal, excluded, audited]:
        same_raw(before, result)

    # 同じ物理対象になるよう、逆方向では削除帯100pxぶんを元Aの除外へ含める。
    exclusion = dict(page='all', x=0, y=430 * 25.4 / 300, w=84.7, h=(120 if reverse else 20) * 25.4 / 300)
    config = dict(base, exclude=[exclusion])
    before = run('structure-disabled' + suffix, 'excluded-blocks', reverse, disabled(config))
    for mode in ['exclude', 'region']:
        cfg = config if mode == 'exclude' else dict(base, regions=[dict(exclusion, name='構造帯の除外', mode='exclude')])
        result = run('structure-' + mode + suffix, 'excluded-blocks', reverse, cfg)
        row = result[1]['pages'][0]['row_alignment']
        assert row['reason'] == 'applied' and result[1]['summary']['difference_count'] == 1
        operation = next(c for c in row['structural_changes'] if c['kind'] == ('deleted' if reverse else 'inserted'))
        assert operation['excluded'] and operation['exclusion_reason'] == 'all_content_excluded'
        assert next(c for c in row['structural_changes'] if c['kind'] == 'block_moved')['excluded'] is False
        assert row['omission_audit']['omitted_candidate_raw_pixels'] is None
        same_raw(before, result)
    unsupported = run('all-support-excluded' + suffix, 'excluded-blocks', reverse,
                      dict(base, exclude=[dict(page='all', x=0, y=0, w=85, h=106)]))
    assert unsupported[1]['pages'][0]['row_alignment']['reason'] == 'insufficient_support'
    assert unsupported[1]['summary']['difference_count'] == 0

    # L03は観測。採用や件数を固定した検出必須のケースにはしない。
    before = run('L03-disabled' + suffix, 'fixed-grid', reverse, disabled(base))
    after = run('L03-enabled' + suffix, 'fixed-grid', reverse, base)
    same_raw(before, after)
    if after[1]['pages'][0]['row_alignment']['status'] == 'skipped':
        assert compat.snapshot(before[0], False) == compat.snapshot(after[0], True)

measurement_audit = []
for scenario in ['identical', 'adopted', 'columns', 'low-improvement', 'regions-2', 'regions-3']:
    off = args.measurements / (scenario + '-off-product-0')
    on = args.measurements / (scenario + '-on-product-0')
    one, two = json.loads((off / 'result.json').read_text()), json.loads((on / 'result.json').read_text())
    same_raw((off, one), (on, two))
    fallback = two['pages'][0]['row_alignment']['status'] != 'applied'
    if fallback:
        assert compat.snapshot(off, False) == compat.snapshot(on, True)
    measurement_audit.append(dict(scenario=scenario, raw_evidence_identical=True, fallback_existing_outputs_identical=fallback))

(args.output / 'verification.json').write_text(json.dumps(dict(cases=records, measurements=measurement_audit), ensure_ascii=False, indent=2) + '\n')
