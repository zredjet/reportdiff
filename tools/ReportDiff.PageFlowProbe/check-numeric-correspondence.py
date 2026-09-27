#!/usr/bin/env python3
"""元本文・編集列・画素から数値対応の診断結果を照合する。製品の期待値生成には使わない。"""
import difflib
import hashlib
import importlib.util
import json
import sys
from collections import Counter
from decimal import Decimal, InvalidOperation
from pathlib import Path

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
spec = importlib.util.spec_from_file_location('numeric_reference', root / 'reference/prototype.py')
ref = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = ref
spec.loader.exec_module(ref)
records = json.loads((folder / 'numeric.json').read_text())
manifest = json.loads((folder / 'fixtures.json').read_text())
for entry in manifest:
    assert hashlib.sha256((folder / entry['id'] / (entry['side'] + '.pdf')).read_bytes()).hexdigest() == entry['sha256']
metrics = Counter()
results = []


def read(path, mode=cv2.IMREAD_COLOR):
    result = cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), mode)
    assert result is not None, path
    return result


def numeric(token):
    if not token or any(c not in '+-.0123456789' for c in token) or token.endswith('.'):
        return False
    try:
        return Decimal(token).is_finite()
    except InvalidOperation:
        return False


def skeleton(text):
    return tuple(None if numeric(t) else t for t in text.split())


def render(original, segments, side):
    result = np.full((sum(s['length'] for s in segments), original.shape[1], 3), 255, np.uint8)
    for segment in segments:
        start = segment[side + '_start']
        if start is not None:
            result[segment['canvas_start']:segment['canvas_start'] + segment['length']] = original[start:start + segment['length']]
    return result


def overlay(a, b, color):
    ga, gb = [((x.astype(np.int32) * [114, 587, 299]).sum(axis=2) + 500) // 1000 for x in (a, b)]
    rgb = list(bytes.fromhex(color[1:]))
    common = 255 - np.maximum(ga, gb)
    return np.stack([ga + (common * rgb[2] + 127) // 255,
                     np.minimum(ga, gb) + (common * rgb[1] + 127) // 255,
                     gb + (common * rgb[0] + 127) // 255], axis=2).astype(np.uint8)


for run in records:
    name = run['run']
    assert run['matched'], (name, '事前期待と不一致')
    directory = folder / name
    inp = run['input']
    decision = run['candidate']
    actual = run['actual_rows']
    proposed = run['numeric']['pairs']
    originals = run['numeric_input']['rows']
    metrics['runs'] += 1
    assert len(actual) == len(originals)
    assert actual == [dict(side=r['side'], page=r['page'], start=r['top'], length=r['height'], text=r['text']) for r in originals]
    for side in (0, 1):
        native_side = ('a', 'b')[1 - side if run['reverse'] else side]
        expected = next(m for m in manifest if m['id'] == run['id'] and m['side'] == native_side)['expected_rows']
        assert [[r['text'] for r in actual if r['side'] == side and r['page'] == n + 1] for n in range(len(expected))] == expected, (name, 'PDF本文')
    for pair in proposed:
        a, b = pair['a'], pair['b']
        assert a in originals and b in originals
        assert a['side'] == 0 and b['side'] == 1 and a['page'] == b['page']
        assert a['height'] == b['height'] and abs(a['left'] - b['left']) <= .5
        assert a['text'] != b['text'] and skeleton(a['text']) == skeleton(b['text'])
        skel = skeleton(a['text'])
        assert skel.count(None) > 0 and sum(t is not None for t in skel) >= 2
        tokens_a, tokens_b = a['text'].split(), b['text'].split()
        for tag, i, j, k, l in difflib.SequenceMatcher(a=tokens_a, b=tokens_b, autojunk=False).get_opcodes():
            if tag != 'equal':
                assert tag == 'replace' and j - i == l - k
                assert all(numeric(t) for t in tokens_a[i:j] + tokens_b[k:l])
        for side in (0, 1):
            rows = [r for r in originals if r['side'] == side]
            assert len(set(r['text'] for r in rows)) == len(rows)
            assert sum(skeleton(r['text']) == skel for r in rows) == 1
        assert not any(r['side'] == 1 and r['text'] == a['text'] or r['side'] == 0 and r['text'] == b['text'] for r in originals)
        offsets = []
        assert len(pair['anchors']) == 2
        for anchor in pair['anchors']:
            x, y = anchor['a'], anchor['b']
            assert x in originals and y in originals and x['side'] == 0 and y['side'] == 1
            assert x['page'] == y['page'] == a['page'] and x['text'] == y['text']
            assert x['index'] - a['index'] == y['index'] - b['index']
            assert x['top'] - y['top'] == a['top'] - b['top'] and x['height'] == y['height']
            assert abs(x['left'] - y['left']) <= .5
            offsets.append(x['index'] - a['index'])
        assert offsets in ([-2, -1], [1, 2], [-1, 1])
        metrics['numeric_pairs'] += 1
    if proposed:
        texts_a, texts_b = [[r['text'] for r in originals if r['side'] == side] for side in (0, 1)]
        assert [t for t in texts_a if t in texts_b] == [t for t in texts_b if t in texts_a]
    # 対応IDとして差替えたのは採用されたB本文だけ。元本文は別に照合済み。
    expected_rows = [dict(r) for r in actual]
    for row in expected_rows:
        for pair in proposed:
            if row['side'] == 1 and row['page'] == pair['b']['page'] and row['text'] == pair['b']['text']:
                row['text'] = pair['a']['text']
    assert inp['rows'] == expected_rows
    ready = run['mapping_gate']['ready'] and all(a['accepted'] for a in run['adoptions'])
    assert inp['gate_ready'] == ready
    if not ready:
        assert decision['status'] != 'grouped' and not decision['groups']
    if run['id'] == 'weak-actual-support':
        assert run['mapping_gate']['ready'] and not ready
        assert all(a['accepted'] for a in run['alias_adoptions'])
        metrics['false_support_prevented'] += 1
    report = json.loads((directory / 'cli/result.json').read_text())
    for page in report['pages']:
        images = [read(directory / f"p{page['page']}-O-{side}.png") for side in ('A', 'B')]
        evidence = page['raw_evidence']
        for side, original in zip(('a', 'b'), images):
            assert np.array_equal(original, read(directory / 'cli' / evidence[side]['image']))
            metrics['raw_source_images'] += 1
        assert np.array_equal(overlay(*images, evidence['common_color']), read(directory / 'cli' / evidence['overlay']))
        metrics['raw_overlays'] += 1
    if ready:
        # 元本文の完全一致行が跨いだページから成分を構成。候補リンクは使わない。
        a = sorted([r for r in actual if r['side'] == 0], key=lambda r: (r['page'], r['start']))
        b = sorted([r for r in actual if r['side'] == 1], key=lambda r: (r['page'], r['start']))
        byb = {r['text']: r for r in b}
        adjacency = {p['number']: set() for p in inp['pages']}
        for row in a:
            if row['text'] in byb and row['page'] != byb[row['text']]['page']:
                other = byb[row['text']]['page']
                adjacency[row['page']].add(other)
                adjacency[other].add(row['page'])
        unseen = set(adjacency)
        groups = []
        while unseen:
            todo, group = [min(unseen)], set()
            while todo:
                page = todo.pop()
                if page not in group:
                    group.add(page)
                    todo.extend(adjacency[page] - group)
            unseen -= group
            groups.append(group)
        oracle = []
        for group in groups:
            aa, bb = [sorted([r for r in expected_rows if r['side'] == side and r['page'] in group], key=lambda r: (r['page'], r['start'])) for side in (0, 1)]
            edits = [op for op in difflib.SequenceMatcher(a=[r['text'] for r in aa], b=[r['text'] for r in bb], autojunk=False).get_opcodes() if op[0] != 'equal']
            structures = [s for p in inp['pages'] if p['number'] in group for s in p['structures']]
            if len(group) == 1:
                assert not edits and not structures
                continue
            assert len(edits) == 1 and edits[0][0] in ('insert', 'delete')
            tag, i, j, k, l = edits[0]
            extra, side = (bb[k:l], 1) if tag == 'insert' else (aa[i:j], 0)
            assert len({r['page'] for r in extra}) == 1
            band = dict(page=dict(side=side, page=extra[0]['page']), top=extra[0]['start'], height=sum(r['length'] for r in extra), bottom=extra[-1]['start'] + extra[-1]['length'])
            causes = [s for s in structures if s['kind'] == ('inserted' if side else 'deleted') and s['b' if side else 'a'] == band]
            assert len(causes) == 1
            oracle.append(dict(cause=causes[0]['reference'], pages=sorted(group), refs=sorted((s['reference']['page'], s['reference']['structural_change_id']) for s in structures)))
        assert decision['status'] == 'grouped' and len(oracle) == len(decision['groups'])
        for expected, found in zip(oracle, decision['groups']):
            assert expected['cause'] == found['cause']
            assert expected['pages'] == [v['page'] for v in found['balance']]
            assert expected['refs'] == sorted((r['page'], r['structural_change_id']) for r in found['structures'])
        refs = [r for g in oracle for r in g['refs']]
        assert len(refs) == len(set(refs))
        assert decision['difference_count'] == sum(p['clusters'] for p in inp['pages']) + len(refs)
        assert decision['aggregated_difference_count'] == sum(p['clusters'] for p in inp['pages']) + len(oracle)
        metrics['oracle_groups'] += len(oracle)
    for projection in run['projections']:
        n = projection['page']
        raw = read(directory / f'p{n}-C-raw.png', cv2.IMREAD_GRAYSCALE)
        ca, cb = [read(directory / f'p{n}-C-{side}.png') for side in ('A', 'B')]
        compared = ref.compare_page(ca, cb, ref.Params())
        assert np.array_equal(compared.raw_mask, raw > 0), (name, n, '生差分')
        assert compared.raw_pixels == projection['raw_pixels'] and len(compared.clusters) == len(projection['content_clusters'])
        assert sorted((c.x, c.y, c.w, c.h, c.pixels) for c in compared.clusters) == sorted(
            (c['bounds']['left'], c['bounds']['top'], c['bounds']['right'] - c['bounds']['left'],
             c['bounds']['bottom'] - c['bounds']['top'], c['pixels']) for c in projection['content_clusters'])
        metrics['reference_pages'] += 1
        display = read(directory / f'p{n}-D-raw.png', cv2.IMREAD_GRAYSCALE)
        expected = np.zeros_like(display)
        for piece in projection['pieces']:
            expected[piece['display_start']:piece['display_start'] + piece['length']] = raw[piece['content_start']:piece['content_start'] + piece['length']]
        assert np.array_equal(display, expected) and np.count_nonzero(display) == np.count_nonzero(raw)
        for side in ('a', 'b'):
            original = read(directory / f'p{n}-O-{side.upper()}.png')
            for mode, key in (('C', 'content_map'), ('D', 'display_map')):
                reconstructed = render(original, projection[key], side)
                assert np.array_equal(reconstructed, read(directory / f'p{n}-{mode}-{side.upper()}.png'))
                if mode == 'D':
                    assert np.count_nonzero(np.any(reconstructed != 255, axis=2)) == np.count_nonzero(np.any(original != 255, axis=2))
                metrics['surface_images'] += 1
                for pair in [p for p in proposed if p['a']['page'] == n]:
                    row = pair[side]
                    coverage = np.zeros(row['height'], np.int32)
                    for segment in projection[key]:
                        start = segment[side + '_start']
                        if start is None:
                            continue
                        lo, hi = max(row['top'], start), min(row['top'] + row['height'], start + segment['length'])
                        if hi > lo:
                            coverage[lo - row['top']:hi - row['top']] += 1
                    assert np.all(coverage == 1), (name, n, side, mode, '数値行全体を一度だけ保持')
                    metrics['numeric_row_coverage'] += 1
            for pair in [p for p in proposed if p['a']['page'] == n]:
                row = pair[side]
                assert not any(s[side + '_start'] is not None and s[side + '_start'] < row['top'] + row['height'] and s[side + '_start'] + s['length'] > row['top'] for s in projection['omitted'])
        for item in [r for r in run['numeric_ranges'] if r['pair']['a']['page'] == n]:
            locations = []
            for side in ('a', 'b'):
                row = item['pair'][side]
                segments = [s for s in projection['content_map'] if s[side + '_start'] is not None and s[side + '_start'] <= row['top'] and s[side + '_start'] + s['length'] >= row['top'] + row['height']]
                assert len(segments) == 1
                segment = segments[0]
                locations.append(segment['canvas_start'] + row['top'] - segment[side + '_start'])
            assert locations == [item['content_top']] * 2
            top, height = item['content_top'], item['pair']['a']['height']
            assert np.count_nonzero(raw[top:top + height]) == item['raw_pixels'] > 0
            assert sum(c['bounds']['top'] < top + height and c['bounds']['bottom'] > top for c in projection['content_clusters']) == item['clusters'] > 0
            metrics['changed_numeric_ranges'] += 1
        page = next(p for p in inp['pages'] if p['number'] == n)
        assert [s['id'] for s in projection['structures']] == [s['reference']['structural_change_id'] for s in page['structures']]
        assert page['clusters'] == len(projection['content_clusters'])
        metrics['structures'] += len(projection['structures'])
    results.append(dict(run=name, pairs=len(proposed), mapping_ready=run['mapping_gate']['ready'], adopted=ready,
                        grouped=decision['status'] == 'grouped', candidate_count=decision['difference_count'],
                        candidate_aggregate=decision['aggregated_difference_count'], actual_cli_count=run['cli']['summary']['difference_count']))
    print(name, decision['status'], decision['aggregated_difference_count'], flush=True)
(folder / 'independent-check.json').write_text(json.dumps(dict(metrics=dict(metrics), records=results), indent=2) + '\n')
