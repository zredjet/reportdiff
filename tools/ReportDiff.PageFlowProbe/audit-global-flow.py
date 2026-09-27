#!/usr/bin/env python3
"""合成入力・O/G/C/D・文字・元帯・構造参照をNumPyで独立に照合する。"""
import hashlib
import json
from pathlib import Path
import sys

import numpy as np
from PIL import Image

inputs, output = map(lambda p: Path(p).resolve(), sys.argv[1:3])
runs = json.loads((output / 'global-flow.json').read_text())
load = lambda p: np.array(Image.open(p))
hashfile = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
checks = []


def shift(image, dx, dy):
    h, w = image.shape[:2]
    result = np.full_like(image, 255)
    x0, y0, x1, y1 = max(0, dx), max(0, dy), min(w, w + dx), min(h, h + dy)
    if x1 > x0 and y1 > y0:
        result[y0:y1, x0:x1] = image[y0-dy:y1-dy, x0-dx:x1-dx]
    return result


def anchored(image):
    h, w = image.shape[:2]
    result = np.full((h + 512, w, 3), 255, np.uint8)
    result[256:256+h] = image
    for start in [64, result.shape[0] - 248]:
        for y in range(0, 184, 9):
            for x in range(64, w - 72, 9):
                if (x * 37 + y * 17 + x * y * 13) % 11 < 9:
                    result[start+y:start+y+8, x:x+8] = 0
    return result


def render(source, segments, side, dx=0):
    h = segments[-1]['canvas_start'] + segments[-1]['length']
    w = source.shape[1]
    result = np.full((h, w, 3), 255, np.uint8)
    for segment in segments:
        start = segment[side.lower() + '_start']
        if start is None:
            continue
        c, length = segment['canvas_start'], segment['length']
        x0, x1 = max(0, dx), min(w, w + dx)
        offset0, offset1 = max(0, -start), min(length, source.shape[0] - start)
        if offset1 > offset0 and x1 > x0:
            result[c+offset0:c+offset1, x0:x1] = source[start+offset0:start+offset1, x0-dx:x1-dx]
    return result


def back(bounds, side, page, width, height):
    dx = page['dx'] if side == 'B' else 0
    y = page['global_segments'][0]['b_start'] if side == 'B' else 0
    return dict(left=max(0, bounds['left'] - dx), right=min(width, bounds['right'] - dx),
                top=max(0, bounds['top'] + y), bottom=min(height, bounds['bottom'] + y))


for run in runs:
    scenario = run['scenario']; name = scenario['id']; directory = output / name
    for side, digest in zip(['a', 'b'], run['pdf_sha256']):
        assert hashfile(inputs / scenario['fixture'] / (side + '.pdf')) == digest
    images = {}; page_checks = []
    for page in run['pages']:
        n = page['number']
        for side, side_id in [('A', 0), ('B', 1)]:
            is_base_b = (side == 'B') != scenario['reverse']
            base_side = 'b' if is_base_b else 'a'
            image = load(inputs / scenario['fixture'] / f'p{n}-{base_side}.png')
            if scenario['anchor']:
                image = anchored(image)
            pad = 256 if scenario['anchor'] else 0
            if is_base_b and n == 2 and scenario['mutation'] in ['body', 'band']:
                y = pad + (550 if scenario['mutation'] == 'body' else 350)
                image[y:y+10, 650:660] = 160
            x, y = (scenario['x'], scenario['y']) if is_base_b else (0, 0)
            if is_base_b and scenario['pattern'] == 'variable':
                y = [4, -6, 3][n-1]
            if is_base_b and scenario['pattern'] == 'mixed' and n == 1:
                y = 0
            if y % 1:
                assert y % 1 == .5 and x == 0
                image = ((shift(image, 0, int(y)).astype(np.uint16) +
                          shift(image, 0, int(y) + 1).astype(np.uint16) + 1) // 2).astype(np.uint8)
            else:
                image = shift(image, int(x), int(y))
            if is_base_b and n == 2 and scenario['mutation'] == 'edge':
                image[0, 500] = [255, 255, 254]  # 保存PNGはRGB、入力MatはBGR。
            original = load(directory / f'p{n}-{side}-O.png')
            assert np.array_equal(image, original), (name, n, side, 'input_generation')
            images[side, n] = original
            raw_hash = next(r['pixel_sha256'] for r in run['raw_sha256'] if r['key'] == dict(side=side_id, page=n))
            assert hashlib.sha256(original[:, :, ::-1].tobytes()).hexdigest() == raw_hash
            g = load(directory / f'p{n}-{side}-G.png')
            dx, dy = (page['dx'], -page['global_segments'][0]['b_start']) if side == 'B' else (0, 0)
            assert np.array_equal(g, shift(original, dx, dy)), (name, n, side, 'global_copy')
            aligned_hash = next(r['pixel_sha256'] for r in run['aligned_sha256'] if r['key'] == dict(side=side_id, page=n))
            assert hashlib.sha256(g[:, :, ::-1].tobytes()).hexdigest() == aligned_hash
            words = json.loads((directory / f'p{n}-{side}-words.json').read_text())
            assert words['original']['status'] == words['aligned']['status'] == 'available'
            assert len(words['original']['words']) == len(words['aligned']['words'])
            for o, a in zip(words['original']['words'], words['aligned']['words']):
                assert o['text'] == a['text']
                for coordinate in ['left', 'right', 'top', 'bottom']:
                    delta = dx if coordinate in ['left', 'right'] else dy
                    assert abs(a['bounds'][coordinate] - o['bounds'][coordinate] - delta) < 1e-9
                assert all(abs(yy - xx - dy) < 1e-9 for xx, yy in zip(o['baselines'], a['baselines']))
            candidate = page['candidate']
            if candidate is not None:
                for space in ['C', 'D']:
                    segments = ([dict(canvas_start=p['content_start'], length=p['length'], a_start=p['a_start'], b_start=p['b_start'])
                                 for p in candidate['pieces']] if space == 'C' else candidate['display_segments'])
                    composed = candidate['composed_content_segments' if space == 'C' else 'composed_display_segments']
                    actual = load(directory / f'p{n}-{space}-{side}.png')
                    assert np.array_equal(actual, render(g, segments, side)), (name, n, space, side, 'G_to_surface')
                    assert np.array_equal(actual, render(original, composed, side, dx)), (name, n, space, side, 'O_to_surface')
                    if space == 'D':
                        assert np.count_nonzero(np.any(actual < 255, axis=2)) == np.count_nonzero(np.any(original < 255, axis=2)), (name, n, side, 'nonwhite_coverage')
                    if side == 'B' and (dx != 0 or dy != 0):
                        twice = [dict(s, b_start=None if s['b_start'] is None else s['b_start'] - dy) for s in composed]
                        assert not np.array_equal(actual, render(original, twice, side, 2 * dx)), (name, n, side, 'double_shift_control')
                for item in candidate['structures'] + candidate['clusters']:
                    src = item['aligned'] if 'kind' in item['aligned'] and 'display_bounds' in item['aligned'] else item['aligned']['row']
                    bounds = src['source_' + side.lower()]
                    expected = [] if bounds is None else [back(p, side, page, original.shape[1], original.shape[0]) for p in bounds['parts']]
                    expected = [p for p in expected if p['left'] < p['right'] and p['top'] < p['bottom']]
                    assert expected == [{k:part[k] for k in ['left','right','top','bottom']} for part in item['original_' + side.lower()]]
        if page['candidate'] is not None:
            candidate = page['candidate']
            c = load(directory / f'p{n}-C-raw.png'); d = load(directory / f'p{n}-D-raw.png')
            projected = np.zeros_like(d)
            for piece in candidate['pieces']:
                cs, ds, length = [piece[k] for k in ['content_start','display_start','length']]
                projected[ds:ds+length] = c[cs:cs+length]
            assert np.array_equal(d, projected)
            assert int(np.count_nonzero(c)) == int(np.count_nonzero(d)) == candidate['raw_pixels']
            assert candidate['difference_count'] == candidate['content_clusters'] + sum(not s['aligned']['excluded'] for s in candidate['structures'])
            page_checks.append(dict(page=n, raw=candidate['raw_pixels'], clusters=candidate['content_clusters'], cd_equal=True, original_parts_equal=True))
    for i, link in enumerate(run['links'], 1):
        if link['original_source'] is None or link['original_target'] is None:
            assert link['reason'] == 'original_band_not_fullwidth_or_clipped'
            continue
        crops = []
        for role in ['source','target']:
            band = link['original_' + role]; side = 'A' if band['page']['side'] == 0 else 'B'
            original = images[side, band['page']['page']]
            crop = original[band['top']:band['bottom']]
            assert crop.shape[0] == band['height']
            assert np.array_equal(crop, load(directory / f'link-{i}-{role}-O.png'))
            crops.append(crop)
        assert np.array_equal(*crops) == (link['proof']['proposal']['status'] == 'band_verified')
    adopted = run['decision']['ready'] and run['original_proofs_passed'] and all(p['candidate']['adoption']['accepted'] for p in run['pages'])
    assert adopted == run['diagnostic_adoptable']
    assert run['expectation_met'] == (adopted == scenario['expected'])
    aggregate = run['candidate_aggregation']
    if aggregate is not None and aggregate['status'] == 'grouped':
        refs = {(p['number'],s['aligned']['id']) for p in run['pages'] for s in p['candidate']['structures']}
        group = aggregate['groups'][0]
        assert refs == {(r['page'],r['structural_change_id']) for r in group['structures']}
        u = sum(p['candidate']['difference_count'] for p in run['pages'])
        assert aggregate['difference_count'] == u and aggregate['aggregated_difference_count'] == u - len(refs) + 1
    assert run['changed_reread_rejected'] == len(run['pages'])
    checks.append(dict(case=name, expectation_met=run['expectation_met'], diagnostic_adoptable=adopted,
                       input_and_global_pixels_equal=True, words_shifted_once=True, pages=page_checks,
                       original_links=len(run['links']), reread_guards=run['changed_reread_rejected'],
                       mixed_frame_guards=run['mismatched_frame_rejected']))
    print(name, '入力・座標・画素・参照一致', flush=True)

# 内容変更の検出とA基準の除外。除外は元帯の証明には効かない。
by_name = {r['scenario']['id']:r for r in runs}
for name in ['body-tone','body-region']:
    assert by_name[name]['pages'][1]['candidate']['content_clusters'] > 0
    p = by_name[name]['pages'][1]
    piece = next(p for p in p['candidate']['pieces'] if p['b_start'] is not None and p['b_start'] <= 811 < p['b_start'] + p['length'])
    y = piece['content_start'] + 811 - piece['b_start']
    assert load(output/name/'p2-C-raw.png')[y,655] != 0, (name,'changed_marker')
assert by_name['body-excluded']['pages'][1]['candidate']['content_clusters'] == 0
assert not by_name['band-tone']['diagnostic_adoptable'] and not by_name['band-excluded']['diagnostic_adoptable']
assert by_name['edge-content']['pages'][1]['alignment']['reason'] == 'edge_content'
forward = [s['aligned'] for p in by_name['anchor-down']['pages'] for s in p['candidate']['structures']]
reverse = [s['aligned'] for p in by_name['anchor-reverse']['pages'] for s in p['candidate']['structures']]
assert sum(s['kind']=='inserted' for s in forward) == sum(s['kind']=='deleted' for s in reverse)
assert {s['displacement_px']['dy'] for s in forward if s['kind']=='block_moved'} == {100}
assert {s['displacement_px']['dy'] for s in reverse if s['kind']=='block_moved'} == {-100}
assert all(r['expectation_met'] for r in runs)
(output / 'audit.json').write_text(json.dumps(dict(checks=checks, input_sha256=hashfile(output / 'global-flow.json')), ensure_ascii=False, indent=2)+'\n')
