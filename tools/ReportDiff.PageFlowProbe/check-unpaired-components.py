#!/usr/bin/env python3
"""元画素の全被覆・本文の編集列・Python参照を別々に照合し、未接続の現CLIを記録する。"""
import difflib
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
records = json.loads((folder / 'unpaired-components.json').read_text())
fixtures = json.loads((folder / 'fixtures.json').read_text())
spec = importlib.util.spec_from_file_location('unpaired_reference', root / 'reference/prototype.py')
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)
metrics = dict(runs=0, pdfs=len(fixtures), reference_pages=0, surface_images=0, mask_images=0,
               covered_pages=0, residual_pages=0, exact_bands=0, nonflow_boundaries=0,
               oracle_groups=0, structures=0, cli_processes=0, raw_png_pairs=0, original_png_pairs=0)
results = []
for fixture in fixtures:
    path = folder / 'inputs' / fixture['id'] / (fixture['side'] + '.pdf')
    assert hashlib.sha256(path.read_bytes()).hexdigest() == fixture['sha256']
for enabled in (False, True):
    (folder / ('on.yaml' if enabled else 'off.yaml')).write_text(
        'rows: {enabled: true, carry_enabled: ' + str(enabled).lower() + '}\nreport: {raw_overlay: true}\n')


def read(path, mode=cv2.IMREAD_COLOR):
    image = cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), mode)
    assert image is not None, path
    return image


def render(original, segments, side):
    result = np.full((sum(s['length'] for s in segments), original.shape[1], 3), 255, np.uint8)
    for segment in segments:
        start = segment[side + '_start']
        if start is not None:
            result[segment['canvas_start']:segment['canvas_start'] + segment['length']] = original[start:start + segment['length']]
    return result


def band_image(directory, band):
    key = band['page']
    image = read(directory / f"p{key['page']}-O-{'AB'[key['side']]}.png")
    return image[band['top']:band['top'] + band['height']]


for run in records:
    name = run['run']
    directory = folder / name
    inp = run['input']
    decision = run['candidate']
    assert run['matched'], (name, '当初の期待は変更しない')
    metrics['runs'] += 1
    rows = inp['rows']
    a = sorted((r for r in rows if r['side'] == 0), key=lambda r: (r['page'], r['start']))
    b = sorted((r for r in rows if r['side'] == 1), key=lambda r: (r['page'], r['start']))
    by_b = {r['text']: r for r in b}
    for proof in run['nonflow']:
        assert not any(min(r['page'], by_b[r['text']]['page']) <= proof['boundary'] < max(r['page'], by_b[r['text']]['page'])
                       for r in a if r['text'] in by_b)
        for side in ('source', 'target'):
            original, matched = proof[side + '_rows'], proof[side + '_counterparts']
            assert original and len(original) == len(matched)
            assert all(x['text'] == y['text'] and x['page'] == y['page'] and x['length'] == y['length'] and x['side'] != y['side']
                       for x, y in zip(original, matched))
        metrics['nonflow_boundaries'] += 1
    for link in inp['links']:
        assert np.array_equal(band_image(directory, link['source']), band_image(directory, link['target']))
        metrics['exact_bands'] += 1
    for coverage in run['coverage']:
        key = coverage['key']
        if coverage['header_end'] is None:
            assert not coverage['unpaired_covered']
            continue
        image = read(directory / f"p{key['page']}-O-{'AB'[key['side']]}.png")
        allowed = np.zeros(image.shape[:2], bool)
        allowed[:coverage['header_end']] = True
        allowed[coverage['footer_start']:] = True
        for band in coverage['bands']:
            allowed[band['top']:band['bottom']] = True
        residual = np.any(image != 255, axis=2) & ~allowed
        assert np.array_equal(read(directory / f"p{key['page']}-unpaired-residual.png", cv2.IMREAD_GRAYSCALE) > 0, residual)
        assert int(residual.sum()) == coverage['residual_pixels']
        assert coverage['unpaired_covered'] == bool(coverage['bands'] and not residual.any())
        metrics['covered_pages' if coverage['unpaired_covered'] else 'residual_pages'] += 1
        # 固定部も元画像全幅で照合し、白画素の隙間を含めて同一と確認する。
        for layout in run['layouts']:
            other = read(directory / f"p{layout['key']['page']}-O-{'AB'[layout['key']['side']]}.png")
            assert np.array_equal(image[:coverage['header_end']], other[:coverage['header_end']])
            assert np.array_equal(image[coverage['footer_start']:], other[coverage['footer_start']:])
    if inp['gate_ready']:
        for side, actual in [('a', a), ('b', b)]:
            native = ('b' if side == 'a' else 'a') if run['reverse'] else side
            expected = next(f['expected_rows'] for f in fixtures if f['id'] == run['id'] and f['side'] == native)
            assert [[r['text'] for r in actual if r['page'] == n + 1] for n in range(len(expected))] == expected
        # 候補の辺を使わず、同じ本文が物理ページを跨いだ事実から成分を作る。
        adjacency = {p['number']: set() for p in inp['pages']}
        for row in a:
            if row['text'] in by_b and row['page'] != by_b[row['text']]['page']:
                other = by_b[row['text']]['page']
                adjacency[row['page']].add(other)
                adjacency[other].add(row['page'])
        unseen = set(adjacency)
        groups = []
        valid = all(p['paired'] or p['unpaired_covered'] for p in inp['pages'])
        while unseen:
            todo, component = [min(unseen)], set()
            while todo:
                page = todo.pop()
                if page not in component:
                    component.add(page)
                    todo.extend(adjacency[page] - component)
            unseen -= component
            aa = [r for r in a if r['page'] in component]
            bb = [r for r in b if r['page'] in component]
            edits = [op for op in difflib.SequenceMatcher(a=[r['text'] for r in aa], b=[r['text'] for r in bb], autojunk=False).get_opcodes() if op[0] != 'equal']
            structures = [s for p in inp['pages'] if p['number'] in component for s in p['structures']]
            if len(component) == 1:
                valid &= not edits and not structures
                continue
            if len(edits) != 1 or edits[0][0] not in ('insert', 'delete'):
                valid = False
                continue
            tag, i, j, k, l = edits[0]
            extra = bb[k:l] if tag == 'insert' else aa[i:j]
            side = 1 if tag == 'insert' else 0
            assert len({r['page'] for r in extra}) == 1
            band = dict(page=dict(side=side, page=extra[0]['page']), top=extra[0]['start'],
                        height=sum(r['length'] for r in extra), bottom=extra[-1]['start'] + extra[-1]['length'])
            cause = [s for s in structures if s['kind'] == ('inserted' if side else 'deleted') and s['b' if side else 'a'] == band]
            assert len(cause) == 1
            groups.append(dict(cause=cause[0]['reference'], pages=sorted(component),
                               refs=sorted((s['reference']['page'], s['reference']['structural_change_id']) for s in structures)))
        assert (decision['status'] == 'grouped') == valid, (name, '独立した編集列と残余')
        if valid:
            assert len(groups) == len(decision['groups'])
            for expected, actual in zip(groups, decision['groups']):
                assert expected['cause'] == actual['cause']
                assert expected['pages'] == [p['page'] for p in actual['balance']]
                assert expected['refs'] == sorted((r['page'], r['structural_change_id']) for r in actual['structures'])
                for balance in actual['balance']:
                    assert balance['rows_b'] - balance['rows_a'] == balance['cause_delta'] + balance['incoming'] - balance['outgoing']
                for auxiliary in actual['auxiliary_bands']:
                    assert not next(p['paired'] for p in inp['pages'] if p['number'] == auxiliary['page']['page'])
            metrics['oracle_groups'] += len(groups)
            assert decision['aggregated_difference_count'] == sum(p['clusters'] for p in inp['pages']) + len(groups)
    assert not decision['difference_count_complete'] and not decision['aggregated_difference_count_complete']
    for projection in run['projections']:
        n = projection['page']
        ca, cb = [read(directory / f'p{n}-C-{side}.png') for side in 'AB']
        raw = read(directory / f'p{n}-C-raw.png', cv2.IMREAD_GRAYSCALE)
        compared = reference.compare_page(ca, cb, reference.Params())
        assert np.array_equal(compared.raw_mask, raw > 0), (name, n, '参照マスク')
        assert compared.raw_pixels == projection['raw_pixels'] and len(compared.clusters) == len(projection['clusters'])
        metrics['reference_pages'] += 1
        display = read(directory / f'p{n}-D-raw.png', cv2.IMREAD_GRAYSCALE)
        expected = np.zeros_like(display)
        for piece in projection['pieces']:
            expected[piece['display_start']:piece['display_start'] + piece['length']] = raw[piece['content_start']:piece['content_start'] + piece['length']]
        assert np.array_equal(display, expected)
        metrics['mask_images'] += 2
        for side in 'ab':
            original = read(directory / f'p{n}-O-{side.upper()}.png')
            for mode, key in [('C', 'content_map'), ('D', 'display_map')]:
                rebuilt = render(original, projection[key], side)
                assert np.array_equal(rebuilt, read(directory / f'p{n}-{mode}-{side.upper()}.png'))
                metrics['surface_images'] += 1
                if mode == 'D':
                    assert np.count_nonzero(np.any(original != 255, axis=2)) == np.count_nonzero(np.any(rebuilt != 255, axis=2))
        metrics['structures'] += len(projection['structures'])
    reports = {}
    for mode in ('off', 'on'):
        target = directory / mode
        assert not target.exists(), ('既存の結果を上書きしない', target)
        paths = [folder / 'inputs' / run['id'] / (side + '.pdf') for side in ('ba' if run['reverse'] else 'ab')]
        process = subprocess.run(['dotnet', str(root / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'),
                                  'compare', *map(str, paths), '--out', str(target), '--config', str(folder / (mode + '.yaml')), '--quiet'],
                                 cwd=root, capture_output=True, text=True, timeout=180)
        (directory / (mode + '.log')).write_text(process.stdout + process.stderr)
        assert process.returncode == 1 and not process.stderr, (name, mode, process.stderr)
        reports[mode] = json.loads((target / 'result.json').read_text())
        metrics['cli_processes'] += 1
    off, on = reports['off'], reports['on']
    assert not on['summary']['difference_count_complete'] and not on['summary']['aggregated_difference_count_complete']
    assert on['page_flow']['range_ready'] == run['product_gate']['ready']
    for before, after in zip(off['pages'], on['pages']):
        assert before['page'] == after['page']
        for side in 'ab':
            old, new = before['raw_evidence'][side]['image'], after['raw_evidence'][side]['image']
            assert (directory / 'off' / old).read_bytes() == (directory / 'on' / new).read_bytes()
            metrics['raw_png_pairs'] += 1
            original = directory / f"p{after['page']}-O-{side.upper()}.png"
            if original.exists():
                assert np.array_equal(read(original), read(directory / 'on' / new))
                metrics['original_png_pairs'] += 1
        assert (directory / 'off' / before['raw_evidence']['overlay']).read_bytes() == (directory / 'on' / after['raw_evidence']['overlay']).read_bytes()
        metrics['raw_png_pairs'] += 1
        if after['status'].startswith('only_in_'):
            assert after['status'] == before['status'] and not after['difference_count_complete']
            assert not after['clusters'] and not after['row_alignment']['structural_changes']
        if on['page_flow']['status'] == 'skipped':
            assert before == after, (name, '見送り時は全ページ従来結果を保持')
    if run['product_gate']['ready'] and inp['gate_ready']:
        product = run.get('product_aggregation') or run['legacy']
        assert on['summary']['difference_count'] == product['difference_count']
        assert on['summary']['aggregated_difference_count'] == product['aggregated_difference_count']
    results.append(dict(run=name, candidate_count=decision['difference_count'], candidate_aggregate=decision['aggregated_difference_count'],
                        candidate_grouped=decision['status'] == 'grouped', product_status=on['page_flow']['status'],
                        product_count=on['summary']['difference_count'], product_aggregate=on['summary']['aggregated_difference_count']))
    print(name, decision['status'], decision['aggregated_difference_count'], '現CLI', results[-1]['product_aggregate'], flush=True)
    (folder / 'independent-check.json').write_text(json.dumps(dict(metrics=metrics, records=results), indent=2) + '\n')
print(json.dumps(metrics), flush=True)
