#!/usr/bin/env python3
"""数値・色・全体補正・領域条件・選択と境界証明の併用を実CLIで検証する。"""
import hashlib, importlib.util, json, subprocess, sys
import cv2, numpy as np
from pathlib import Path

root = Path(__file__).resolve().parents[2]; out = Path(sys.argv[1]).resolve(); out.mkdir(parents=True, exist_ok=True)
fixtures = root/'tests/ReportDiff.Tests/Fixtures/page-flow-ambiguity'
cli = root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
cases = json.loads((fixtures/'cases.json').read_text()); records = []
spec = importlib.util.spec_from_file_location('reference', root/'reference/prototype.py'); ref = importlib.util.module_from_spec(spec); sys.modules[spec.name] = ref; spec.loader.exec_module(ref)
binaries = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in cli.parent.glob('*.dll')}
if (out/'binaries.json').exists(): assert json.loads((out/'binaries.json').read_text()) == binaries
else: (out/'binaries.json').write_text(json.dumps(binaries, indent=2)+'\n')
for path, sha in json.loads((fixtures/'sha256.json').read_text()).items():
    assert hashlib.sha256((fixtures/path).read_bytes()).hexdigest() == sha
for case in cases:
    for reverse in [False, True]:
        name = case['id'] + ('-ba' if reverse else '-ab')
        variants = [('plain', '', [], case['expected'] if case['expected'] is not None else not reverse)]
        if case['id'] == 'chain-number':
            variants += [('all-selected', '', ['--pages', '1-3'], False), ('partial', '', ['--pages', '1-2'], False),
                         ('support-excluded', 'exclude: [{page: 1, x: 0, y: 0, w: 100, h: 110}]', [], False),
                         ('strict', 'regions: [{page: 3, name: strict, mode: compare, x: 0, y: 0, w: 100, h: 110, diff: {max_shift_mm: 0, edge_tolerance: 0}}]', [], True)]
        for variant, extra, args, wanted in variants:
            results = {}
            for carry in [False, True]:
                config = out/f'{name}-{variant}-{carry}.yaml'
                config.write_text(f'rows: {{enabled: true, carry_enabled: {str(carry).lower()}}}\nreport: {{raw_overlay: true}}\n' + ('align: {enabled: true}\n' if case['global_shifts'] else '') + extra + '\n')
                directory = out/f'{name}-{variant}-{carry}'
                paths = [fixtures/case['id']/(s+'.pdf') for s in (['b', 'a'] if reverse else ['a', 'b'])]
                if not (directory/'result.json').exists():
                    p = subprocess.run(['dotnet', str(cli), 'compare', *map(str, paths), '--config', str(config), '--out', str(directory), '--quiet', *args], capture_output=True, text=True)
                    assert p.returncode == 1, (name, variant, carry, p.stdout, p.stderr)
                results[carry] = directory, json.loads((directory/'result.json').read_text())
            directory, report = results[True]; offdir, off = results[False]; flow = report['page_flow']
            applied = flow['status'] == 'applied'
            record = dict(run=name+'-'+variant, expected=wanted, applied=applied, summary=report['summary'], reasons=flow['reasons'], links=flow['links'])
            records.append(record); (out/'verification.json').write_text(json.dumps(records, indent=2)+'\n')
            assert applied == wanted, (record['run'], wanted, flow['reasons'])
            if applied:
                assert sum('ambiguity' in l for l in flow['links']) == 1
                assert report['summary']['aggregated_difference_count'] == (3 if 'independent' in case['id'] else 2) + (1 if case['id'].endswith(('number', 'tone')) else 0)
                if case['id'].endswith('number'):
                    assert sum(p['raw_pixels'] for p in report['pages']) == (48 if variant == 'strict' else 40) and report['summary']['clusters'] == 1
                    assert len(flow['numeric_matches']) == 1 and not flow['numeric_matches'][0]['used_as_exact_support']
                if case['id'].endswith('tone'): assert report['summary']['clusters'] > 0
                for page in report['pages']:
                    images = [cv2.imdecode(np.frombuffer((directory/page['images']['content_'+side]).read_bytes(), np.uint8), cv2.IMREAD_COLOR) for side in ['a', 'b']]
                    params = ref.Params(max_shift_mm=0, edge_tolerance=0) if variant == 'strict' and page['page'] == 3 else ref.Params()
                    compared = ref.compare_page(*images, params)
                    assert compared.raw_pixels == page['raw_pixels'] and len(compared.clusters) == len(page['clusters']), (name, variant, page['page'])
                if case['global_shifts']:
                    assert any(p['alignment']['status'] == 'applied' for p in report['pages'])
                    for link in flow['links']:
                        proof = link.get('ambiguity')
                        if not proof: continue
                        rows = proof['crossing_rows'] + [row for alt in proof['alternatives'] for row in alt['source_rows'] + alt['target_rows']]
                        for row in rows:
                            assert row['row']['coordinate_system'] == row['counterpart']['coordinate_system'] == 'original_top_left'
            else:
                assert report['pages'] == off['pages']
                assert not flow['aggregation']['groups'] and not flow['aggregation'].get('shared_components')
                assert report['summary']['difference_count'] == report['summary']['aggregated_difference_count']
            for a, b in zip(report['pages'], off['pages'], strict=True):
                for x, y in [(a['raw_evidence'][s]['image'], b['raw_evidence'][s]['image']) for s in ['a', 'b']] + [(a['raw_evidence']['overlay'], b['raw_evidence']['overlay'])]:
                    assert (directory/x).read_bytes() == (offdir/y).read_bytes()
            print(record['run'], applied, report['summary']['difference_count'], report['summary']['aggregated_difference_count'], flush=True)
