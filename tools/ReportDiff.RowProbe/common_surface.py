"""表示面から独立した比較面の試行。比較式と製品コードは変更しない。"""
from __future__ import annotations

import argparse
import dataclasses
import hashlib
import json
from pathlib import Path

import numpy as np

import mapped_features as m

OUTPUT = m.ROOT / 'out/t3-1b-probe/results/common-surface'


def build(a, b, bands, retain_white=True):
    """対応帯を順に転写。純白の片側余白を維持する案と全削除案を比較する。"""
    assert a.shape[1:] == b.shape[1:]
    parts_a, parts_b, pieces = [], [], []
    cy = next_display = next_a = next_b = 0
    removed_sides = set()
    for dy, length, ay, by in bands:
        assert dy == next_display and length > 0
        next_display += length
        for source, start, next_source in ((a, ay, next_a), (b, by, next_b)):
            if start is not None:
                assert start == next_source and start + length <= len(source)
        if ay is not None:
            next_a += length
        if by is not None:
            next_b += length
        if ay is not None and by is not None:
            # 両側の非白帯を削って同じ場所の内容変更を消す写像を拒否する。
            if len(removed_sides) > 1:
                raise ValueError('unanchored_join')
            removed_sides.clear()
            pa, pb, kind = a[ay:ay + length], b[by:by + length], 'paired'
        else:
            assert (ay is None) != (by is None)
            source, start, side = (a, ay, 'a') if ay is not None else (b, by, 'b')
            pure_white = bool(np.all(source[start:start + length] == 255))
            if not pure_white:
                removed_sides.add(side)
            if not retain_white or not pure_white:
                continue
            pa = pb = np.full((length,) + a.shape[1:], 255, np.uint8)
            kind = 'white_space'
        pieces.append(dict(common_y=cy, display_y=dy, length=length, a_y=ay, b_y=by, kind=kind))
        parts_a.append(pa)
        parts_b.append(pb)
        cy += length
    if len(removed_sides) > 1:
        raise ValueError('unanchored_join')
    assert next_a == len(a) and next_b == len(b)
    if not pieces or not any(p['kind'] == 'paired' for p in pieces):
        raise ValueError('no_common_bands')
    return np.concatenate(parts_a), np.concatenate(parts_b), pieces


def spread(raw, pieces, height):
    result = np.zeros((height,) + raw.shape[1:], raw.dtype)
    for p in pieces:
        y, d, length = p['common_y'], p['display_y'], p['length']
        result[d:d + length] = raw[y:y + length]
    assert np.count_nonzero(raw) == np.count_nonzero(result)
    return result


def fragments(cluster, pieces):
    result = []
    for piece in pieces:
        top, bottom = max(cluster.y, piece['common_y']), min(cluster.y + cluster.h, piece['common_y'] + piece['length'])
        if bottom > top:
            result.append(dict(x=cluster.x, y=piece['display_y'] + top - piece['common_y'],
                               w=cluster.w, h=bottom - top))
    return result


def prepare():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    jobs = []
    def add(name, a, b, baseline_a, baseline_b, bands, profiles=('normal', 'strict', 'loose'), params=None,
            directions=(False, True), variants=(False, True)):
        for suffix, image in [('a', a), ('b', b), ('baseline-a', baseline_a), ('baseline-b', baseline_b)]:
            m.save(OUTPUT / f'{name}-{suffix}.png', image)
        for profile in profiles:
            for reverse in directions:
                for retain in variants:
                    jobs.append(dict(id=f'{name}-{profile}-' + ('reverse' if reverse else 'forward') + ('-retain' if retain else '-compact'),
                                     name=name, profile=profile, reverse=reverse, retain_white=retain, bands=bands,
                                     params=params or dataclasses.asdict(m.profile(profile))))

    # 既存の PDF 描画結果は元のまま読み込み、同じ入力を新しい比較面で照合する。
    for suite, names in [('white-band-pdf', ['unchanged', 'number', 'boundary-0', 'boundary-1', 'boundary-2', 'boundary-4', 'boundary-8']),
                         ('mapped-features', ['gray-unchanged', 'gray-tone', 'patch-0', 'patch-1', 'patch-3', 'patch-20'])]:
        source = m.ROOT / 'out/t3-1b-probe/results' / suite
        for name in names:
            a, b, baseline_b = [m.read(source / f'{name}-{suffix}.png') for suffix in ('source-a', 'source-b', 'baseline-b')]
            add('pdf-' + name, a, b, a, baseline_b, m.insertion_map(len(a), 400, 100))

    # 固定フッター。行の追加は表を送り、その分の純白余白が減る。footer の位置は不変。
    for gray, tone in [(200, 200), (192, 100), (200, 128), (224, 160)]:
        a = np.full((240, 160, 3), 255, np.uint8)
        a[30:80, 40:46] = gray
        a[140:170, 40:46] = 0
        a[25:35, 100:130] = 0
        a[50:55, 100:130] = 0
        baseline_b = a.copy()
        baseline_b[78:80, 40:46] = tone
        b = baseline_b.copy()
        b[60:120] = 255
        b[60:120, 40:46] = gray
        b[80:90, 100:130] = 0
        b[120:140] = baseline_b[60:80]
        bands = [(0, 60, 0, 0), (60, 60, None, 60), (120, 20, 60, 120),
                 (140, 60, 80, None), (200, 100, 140, 140)]
        add(f'footer-{gray}-{tone}', a, b, a, baseline_b, bands)

    # 別々の場所への挿入と削除。比較面では双方の残存内容の順序を維持する。
    for changed in (False, True):
        base = np.full((360, 160, 3), 255, np.uint8)
        base[25:320, 40:46] = 128
        for y in (35, 65, 135, 165, 265, 295):
            base[y:y + 9, 100:125] = 0
        target = base.copy()
        if changed:
            target[160:162, 40:46] = 160
        extra = np.full((60, 160, 3), 255, np.uint8)
        extra[:, 40:46] = 128
        extra[20:30, 100:125] = 0
        a = np.concatenate([base[:100], extra, base[100:]])
        b = np.concatenate([target[:240], extra, target[240:]])
        bands = [(0, 100, 0, 0), (100, 60, 100, None), (160, 140, 160, 100),
                 (300, 60, None, 240), (360, 120, 300, 300)]
        add('mixed-' + ('changed' if changed else 'unchanged'), a, b, base, target, bands)

    # 挿入位置をまたぐ小変更。表示上は断片でも、比較面のクラスタ ID は分割しない。
    a = np.full((240, 160, 3), 255, np.uint8)
    a[30:170, 40:46] = 128
    target = a.copy()
    target[78:82, 40:46] = 160
    b = np.full_like(a, 255)
    b[:80] = target[:80]
    b[140:] = target[80:180]
    b[80:140, 40:46] = 128
    add('crossing', a, b, a, target, m.insertion_map(240, 80, 60))
    # 2箇所の挿入。下端から外れる範囲は基準画像の純白余白だけ。
    for changed in (False, True):
        a = np.full((480, 160, 3), 255, np.uint8)
        a[25:320, 40:46] = 128
        for y in (35, 65, 135, 165, 265, 295):
            a[y:y + 9, 100:125] = 0
        target = a.copy()
        if changed:
            target[240:242, 40:46] = 160
        extra = np.full((60, 160, 3), 255, np.uint8)
        extra[:, 40:46] = 128
        extra[20:30, 100:125] = 0
        b = np.concatenate([target[:100], extra, target[100:240], extra, target[240:360]])
        bands = [(0, 100, 0, 0), (100, 60, None, 100), (160, 140, 100, 160),
                 (300, 60, None, 300), (360, 120, 240, 360), (480, 120, 360, None)]
        add('double-' + ('changed' if changed else 'unchanged'), a, b, a, target, bands)
    # 恒等写像。プロファイルだけでなく、各ゴールデン固有の全パラメータを引き継ぐ。
    golden = m.ROOT / 'reference/golden'
    for item in json.loads((golden / 'expected.json').read_text())['cases']:
        a, b = [m.read(golden / (item['id'] + '_' + side + '.png')) for side in ('a', 'b')]
        add('golden-' + item['id'], a, b, a, b, [(0, len(a), 0, 0)], profiles=('golden',),
            params=item['params'], directions=(False,), variants=(True,))
    (OUTPUT / 'jobs.json').write_text(json.dumps(jobs, indent=2) + '\n')
    print(f'{len(jobs)} 比較条件を準備', flush=True)


def run():
    results = []
    for job in json.loads((OUTPUT / 'jobs.json').read_text()):
        a, b, baseline_a, baseline_b = [m.read(OUTPUT / f"{job['name']}-{suffix}.png") for suffix in ('a', 'b', 'baseline-a', 'baseline-b')]
        before = [hashlib.sha256(x.tobytes()).hexdigest() for x in (a, b)]
        ca, cb, pieces = build(a, b, job['bands'], job['retain_white'])
        p = m.reference.Params(**job['params'])
        if job['reverse']:
            ca, cb = cb, ca
            baseline_a, baseline_b = baseline_b, baseline_a
        input_equal = (np.array_equal(ca, baseline_a) and np.array_equal(cb, baseline_b))
        if job['retain_white']:
            assert input_equal, (job['id'], '比較面が既知の基準画像を復元していません')
        result = m.reference.compare_page(ca, cb, p)
        baseline = m.reference.compare_page(baseline_a, baseline_b, p)
        display = spread(result.raw_mask, pieces, sum(x[1] for x in job['bands']))
        m.save(OUTPUT / (job['id'] + '-raw.png'), result.raw_mask.astype(np.uint8) * 255)
        m.save(OUTPUT / (job['id'] + '-display-raw.png'), display.astype(np.uint8) * 255)
        assert before == [hashlib.sha256(x.tobytes()).hexdigest() for x in (a, b)]
        # 固定フッター以外の基準は、比較面と同じレイアウト（末尾の純白だけ短くなり得る）。
        mask_equal = result.raw_mask.shape == baseline.raw_mask.shape and np.array_equal(result.raw_mask, baseline.raw_mask)
        if job['name'].startswith('pdf-') and not job['retain_white']:
            mask_equal = np.array_equal(result.raw_mask, baseline.raw_mask[:len(result.raw_mask)]) and not baseline.raw_mask[len(result.raw_mask):].any()
        results.append(dict(id=job['id'], name=job['name'], profile=job['profile'], reverse=job['reverse'], retain_white=job['retain_white'],
                            baseline=m.snapshot(baseline), candidate=m.snapshot(result), baseline_mask_equal=mask_equal,
                            baseline_inputs_equal=input_equal,
                            detection_lost=bool(baseline.clusters and not result.clusters),
                            common_size=[ca.shape[1], ca.shape[0]], pieces=pieces,
                            clusters_display_fragments=[dict(id=c.id, fragments=fragments(c, pieces)) for c in result.clusters]))
        print(f"{job['id']}: 基準={baseline.raw_pixels}/{len(baseline.clusters)} 今回={result.raw_pixels}/{len(result.clusters)}", flush=True)
    (OUTPUT / 'python.json').write_text(json.dumps(results, indent=2) + '\n')


def rejection():
    a = np.full((120, 80, 3), 255, np.uint8)
    b = a.copy()
    a[40:80, 20:30] = 0
    b[40:80, 50:60] = 0
    # 同じ位置の置換を削除＋挿入として隠す不正な仮説。
    bands = [(0, 40, 0, 0), (40, 40, 40, None), (80, 40, None, 40), (120, 40, 80, 80)]
    for retain in (False, True):
        try:
            build(a, b, bands, retain)
        except ValueError as e:
            assert str(e) == 'unanchored_join'
        else:
            raise AssertionError('内容置換の隠蔽を拒否しませんでした')
    (OUTPUT / 'rejection.json').write_text(json.dumps(dict(reason='unanchored_join', variants=2, rejected=True), indent=2) + '\n')


def parity():
    actual = {c['id']: c for c in json.loads((OUTPUT / 'csharp.json').read_text())}
    python = json.loads((OUTPUT / 'python.json').read_text())
    for item in python:
        expected = actual[item['id']]
        for suffix in ('raw', 'display-raw'):
            a = m.read(OUTPUT / (item['id'] + '-' + suffix + '.png'))
            b = m.read(OUTPUT / (item['id'] + '-csharp-' + suffix + '.png'))
            assert np.array_equal(a, b), (item['id'], suffix)
        p, c = item['candidate'], expected['candidate']
        for key, ckey in [('status', 'Status'), ('raw_pixels', 'RawPixels'), ('noise_dropped', 'NoiseDropped'),
                          ('absorbed_groups', 'AbsorbedGroups'), ('max_shift_px', 'MaxShiftPx')]:
            assert p[key] == c[ckey], (item['id'], key)
        assert len(p['clusters']) == len(c['clusters'])
        for pc, cc in zip(p['clusters'], c['clusters']):
            assert tuple(pc[k] for k in ('x', 'y', 'w', 'h', 'pixels')) == tuple(cc[k] for k in ('x', 'y', 'w', 'h', 'Pixels'))
        assert item['pieces'] == expected['pieces']
    (OUTPUT / 'parity.json').write_text(json.dumps(dict(comparisons=len(python), equal=True), indent=2) + '\n')
    print(f'{len(python)} 条件で C# と全マスク・統計・クラスタ・写像が一致')


def swap():
    checked = 0
    for job in json.loads((OUTPUT / 'jobs.json').read_text()):
        a, b = [m.read(OUTPUT / f"{job['name']}-{s}.png") for s in ('a', 'b')]
        ca, cb, pieces = build(a, b, job['bands'], job['retain_white'])
        swapped = [(d, length, by, ay) for d, length, ay, by in job['bands']]
        sa, sb, sp = build(b, a, swapped, job['retain_white'])
        assert np.array_equal(ca, sb) and np.array_equal(cb, sa), job['id']
        assert [dict(p, a_y=p['b_y'], b_y=p['a_y']) for p in pieces] == sp
        checked += 1
    (OUTPUT / 'swap.json').write_text(json.dumps(dict(conditions=checked, common_inputs_and_maps_equal=True), indent=2) + '\n')
    print(f'{checked} 条件で元 A/B・写像の交換が比較面の交換と一致')


def unsupported_rows(pieces, source_heights, radius):
    """各近傍が少なくとも片側の元画像の連続区間に対応する、保守的な採用条件。"""
    height = sum(p['length'] for p in pieces)
    anchored = np.zeros(height, bool)
    y = np.arange(height)
    left, right = np.maximum(0, y - radius), np.minimum(height, y + radius + 1)
    for side, source_height in zip(('a_y', 'b_y'), source_heights):
        original = np.full(height, -1, int)
        for p in pieces:
            if p[side] is not None:
                original[p['common_y']:p['common_y'] + p['length']] = np.arange(p[side], p[side] + p['length'])
        absent = np.r_[0, np.cumsum(original < 0)]
        jumps = np.r_[0, np.cumsum(np.r_[False, np.diff(original) != 1])]
        valid = (absent[right] == absent[left]) & (jumps[right] == jumps[left + 1])
        valid &= (left != 0) | (original[0] == 0)
        valid &= (right != height) | (original[-1] == source_height - 1)
        anchored |= valid
    return np.flatnonzero(~anchored).tolist()


def guard():
    accepted = []
    for job in json.loads((OUTPUT / 'jobs.json').read_text()):
        if not job['retain_white']:
            continue
        a, b = [m.read(OUTPUT / f"{job['name']}-{s}.png") for s in ('a', 'b')]
        _, _, pieces = build(a, b, job['bands'], True)
        p = m.reference.Params(**job['params'])
        s = round(p.px(p.max_shift_mm))
        radius = max(2 + s, max(1, round(p.px(p.ink_background_radius_mm))))
        bad = unsupported_rows(pieces, (len(a), len(b)), radius)
        assert not bad, (job['id'], bad)
        accepted.append(dict(id=job['id'], radius=radius, unsupported_rows=0))
    # 近接した別々の挿入・削除。局所窓が双方の切断をまたぐため採用を見送る。
    pieces = [dict(common_y=0, length=60, a_y=0, b_y=0),
              dict(common_y=60, length=4, a_y=100, b_y=60),
              dict(common_y=64, length=76, a_y=104, b_y=104)]
    bad = unsupported_rows(pieces, (180, 180), 18)
    assert bad
    swapped = [dict(p, a_y=p['b_y'], b_y=p['a_y']) for p in pieces]
    assert bad == unsupported_rows(swapped, (180, 180), 18)
    (OUTPUT / 'guard.json').write_text(json.dumps(dict(accepted=accepted,
        close_opposed_seams=dict(unsupported_rows=bad, rejected=True, swap_equal=True)), indent=2) + '\n')
    print(f'{len(accepted)} 条件を維持し、近接する逆向きの切断を両方向で拒否')


def direction():
    rows = json.loads((OUTPUT / 'python.json').read_text())
    lookup = {r['id']: r for r in rows}
    pairs = []
    for r in rows:
        if not r['retain_white'] or r['reverse'] or r['name'].startswith('golden-'):
            continue
        other = lookup[r['id'].replace('-forward-retain', '-reverse-retain')]
        equal = np.array_equal(m.read(OUTPUT / (r['id'] + '-raw.png')), m.read(OUTPUT / (other['id'] + '-raw.png')))
        assert r['baseline_mask_equal'] and other['baseline_mask_equal']
        pairs.append(dict(forward=r['id'], reverse=other['id'], same_mask=equal,
                          forward_baseline_equal=True, reverse_baseline_equal=True,
                          forward_clusters=r['candidate']['clusters'], reverse_clusters=other['candidate']['clusters']))
    assert len(pairs) == 66
    (OUTPUT / 'direction.json').write_text(json.dumps(dict(pairs=len(pairs),
        identical_masks=sum(p['same_mask'] for p in pairs), existing_core_directional_mask_pairs=sum(not p['same_mask'] for p in pairs),
        conditions=pairs), indent=2) + '\n')
    print(f"{len(pairs)} 組が方向別の基準と一致。逆順の全画素一致は {sum(p['same_mask'] for p in pairs)} 組")


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=('prepare', 'run', 'rejection', 'parity', 'swap', 'guard', 'direction'))
    args = parser.parse_args()
    globals()[args.action]()
