"""元画像特徴量の写像案を、以前の反例と境界の近傍で検証する。"""
import argparse
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np

import mapped_features as m


def pdf_cases(output):
    folder = m.ROOT / 'out/t3-1b-probe/results/white-band-pdf'
    cases = []
    for old in json.loads((folder / 'white-band-pdf.json').read_text()):
        name, reverse = old['name'], old['reverse']
        a, b, baseline_b = [m.read(folder / (name + '-' + suffix + '.png'))
                            for suffix in ('source-a', 'source-b', 'baseline-b')]
        before = [hashlib.sha256(v.tobytes()).hexdigest() for v in (a, b)]
        p = m.profile(old['profile'])
        bands = m.insertion_map(a.shape[0], 400, 100)
        result = m.compare(a, b, p, bands, reverse)
        baseline = m.reference.compare_page(baseline_b if reverse else a,
                                            a if reverse else baseline_b, p)
        expected_mask = m.map_array(baseline.raw_mask, bands, 'a', False)
        mask_equal = np.array_equal(result.raw_mask, expected_mask)
        assert before == [hashlib.sha256(v.tobytes()).hexdigest() for v in (a, b)]
        m.save(output / (old['id'] + '-raw.png'), result.raw_mask.astype(np.uint8) * 255)
        cases.append(dict(id=old['id'], baseline=m.snapshot(baseline), candidate=m.snapshot(result),
                          baseline_mask_equal=mask_equal,
                          detection_lost=bool(baseline.clusters and not result.clusters)))
        print(f"{old['id']}: {result.raw_pixels}px/{len(result.clusters)}件 マスク一致={mask_equal}", flush=True)
    return cases


def context_cases(output):
    # 切断行は白。挿入帯の内容は対応帯に触れないが、その局所近傍に入る。
    cases = []
    width, height, cut, gap = 160, 240, 80, 60
    bands = m.insertion_map(height, cut, gap)
    for distance in (1, 2, 3, 4, 8, 20):
        for tone in (0, 32, 64, 128, 192):
            a = np.full((height, width, 3), 255, np.uint8)
            # 既存の内容は継ぎ目から離す。上下に同一の内容を維持。
            a[30:40, 100:125] = 0
            a[160:170, 100:125] = 0
            b = np.full_like(a, 255)
            b[:cut] = a[:cut]
            b[cut + gap:] = a[cut:height - gap]
            # 挿入された帯の下端に横線。境界直前の distance 行は純白。
            b[cut + gap - distance - 6:cut + gap - distance, 30:100] = tone
            for profile in ('normal', 'strict', 'loose'):
                for reverse in (False, True):
                    p = m.profile(profile)
                    result = m.compare(a, b, p, bands, reverse)
                    name = f'context-d{distance}-tone{tone}-{profile}-' + ('reverse' if reverse else 'forward')
                    cases.append(dict(id=name, distance=distance, tone=tone, profile=profile, reverse=reverse,
                                      candidate=m.snapshot(result), expected_raw_pixels=0,
                                      false_detection=bool(result.clusters)))
                    if result.clusters:
                        m.save(output / (name + '-a.png'), a)
                        m.save(output / (name + '-b.png'), b)
                        m.save(output / (name + '-raw.png'), result.raw_mask.astype(np.uint8) * 255)
                        print(f'{name}: 偽差分 {result.raw_pixels}px/{len(result.clusters)}件', flush=True)
    return cases


def tone_cases(output):
    cases = []
    width, height, cut, gap = 120, 180, 70, 40
    bands = m.insertion_map(height, cut, gap)
    for distance in (None, 0, 1, 2, 3, 8, 20):
        a = np.full((height, width, 3), 255, np.uint8)
        a[30:120, 40:46] = 128
        baseline_b = a.copy()
        baseline_b[cut:cut + 2, 40:46] = 160
        b = np.full_like(a, 255)
        b[:cut] = baseline_b[:cut]
        b[cut + gap:] = baseline_b[cut:height - gap]
        b[cut:cut + gap, 40:46] = 128
        if distance is not None:
            b[cut + gap - distance - 6:cut + gap - distance, 40:46] = 0
        valid = m.paired_mask((height + gap, width), bands)
        assert np.array_equal(m.map_array(b, bands, 'b', 255)[valid],
                              m.map_array(baseline_b, bands, 'a', 255)[valid])
        for profile in ('normal', 'strict', 'loose'):
            for reverse in (False, True):
                p = m.profile(profile)
                baseline = m.reference.compare_page(baseline_b if reverse else a, a if reverse else baseline_b, p)
                trace = {}
                result = m.compare(a, b, p, bands, reverse, trace)
                name = f'tone-patch-{distance}-{profile}-' + ('reverse' if reverse else 'forward')
                cases.append(dict(id=name, patch_distance=distance, profile=profile, reverse=reverse,
                                  baseline=m.snapshot(baseline), candidate=m.snapshot(result),
                                  initial_paired_pixels=int((trace['initial'] & valid).sum()),
                                  groups=trace.get('groups', []),
                                  detection_lost=bool(baseline.clusters and not result.clusters)))
                m.save(output / (name + '-raw.png'), result.raw_mask.astype(np.uint8) * 255)
    return cases


def identity_cases(output):
    cases = []
    folder = m.ROOT / 'reference/golden'
    for item in json.loads((folder / 'expected.json').read_text())['cases']:
        a, b = [m.read(folder / (item['id'] + '_' + s + '.png')) for s in ('a', 'b')]
        p = m.reference.Params(**item['params'])
        baseline = m.reference.compare_page(a, b, p)
        result = m.compare(a, b, p, [(0, a.shape[0], 0, 0)])
        assert np.array_equal(result.raw_mask, baseline.raw_mask), item['id']
        assert np.array_equal(result.label_mask, baseline.label_mask), item['id']
        assert m.snapshot(result) == m.snapshot(baseline), item['id']
        for field in ('status', 'raw_pixels', 'noise_dropped', 'absorbed_groups', 'max_shift_px'):
            assert getattr(result, field) == item[field], (item['id'], field)
        cases.append(dict(id=item['id'], reference_equal=True, result=m.snapshot(result)))
        print(item['id'] + ': 恒等写像が参照と一致', flush=True)
    return cases


def csharp_cases(output):
    cases = []
    for item in json.loads((output / 'new-pdf-csharp.json').read_text()):
        a, b, baseline_b = [m.read(output / (item['name'] + '-' + suffix + '.png'))
                            for suffix in ('source-a', 'source-b', 'baseline-b')]
        p = m.profile(item['profile'])
        bands = m.insertion_map(a.shape[0], 400, 100)
        trace = {}
        candidate = m.compare(a, b, p, bands, item['reverse'], trace)
        baseline = m.reference.compare_page(baseline_b if item['reverse'] else a,
                                            a if item['reverse'] else baseline_b, p)
        for variant, actual in [('baseline', baseline), ('candidate', candidate)]:
            suffix = '-baseline-raw.png' if variant == 'baseline' else '-csharp-raw.png'
            expected_mask = m.read(output / (item['id'] + suffix))[:, :, 0] > 0
            assert np.array_equal(actual.raw_mask, expected_mask), (item['id'], variant)
            expected = item[variant]
            for key, cskey in [('status', 'Status'), ('raw_pixels', 'RawPixels'), ('noise_dropped', 'NoiseDropped'),
                               ('absorbed_groups', 'AbsorbedGroups'), ('max_shift_px', 'MaxShiftPx')]:
                assert getattr(actual, key) == expected[cskey], (item['id'], variant, key)
            assert len(actual.clusters) == len(expected['clusters'])
            for ac, ec in zip(actual.clusters, expected['clusters']):
                assert (ac.x, ac.y, ac.w, ac.h, ac.pixels) == tuple(ec[k] for k in ('x', 'y', 'w', 'h', 'Pixels'))
        paired = m.paired_mask(trace['initial'].shape, bands)
        assert int((trace['initial'] & paired).sum()) == item['initial_paired_pixels']
        assert [(g['id'], g['before'], g['after'], *g['shift']) for g in trace.get('groups', [])] == [
            tuple(g[k] for k in ('Id', 'Before', 'After', 'Dx', 'Dy')) for g in item['groups']]
        cases.append(dict(id=item['id'], csharp_equal=True, baseline=m.snapshot(baseline), candidate=m.snapshot(candidate)))
        print(item['id'] + ': C# と基準・初期候補・選択したずれ・最終マスク一致', flush=True)
    return cases


def causes(output):
    records = []
    p = m.profile('normal')
    for name in ('gray-tone', 'patch-0', 'patch-1', 'patch-3', 'patch-20'):
        a, b, baseline_b = [m.read(output / (name + '-' + suffix + '.png'))
                            for suffix in ('source-a', 'source-b', 'baseline-b')]
        bands = m.insertion_map(a.shape[0], 400, 100)
        fa, fb = m.map_features(m.features(a, p), bands, 'a'), m.map_features(m.features(b, p), bands, 'b')
        valid = m.paired_mask(fa[2].shape, bands)
        points = []
        for variant, xa, xb, y in [('baseline', m.features(a, p), m.features(baseline_b, p), 400),
                                   ('mapped', fa, fb, 500)]:
            delta = float(abs(xa[0][y, 102, 0] - xb[0][y, 102, 0]))
            threshold = float(3 + .3 * max(xa[1][y, 102, 0], xb[1][y, 102, 0]))
            points.append(dict(variant=variant, x=102, y=y, delta_L=delta, threshold_L=threshold, candidate=delta > threshold))
        costs = []
        for dy in (0, -1, 1):
            shifted_values, shifted_contrast = [m.reference._shift(v, 0, dy) for v in fb[:2]]
            mask = (abs(fa[0] - shifted_values) > 3 + .3 * np.maximum(fa[1], shifted_contrast)).any(2)
            # 罫線の group と交わる列だけ。挿入された本文の別 group は含めない。
            region = np.zeros(valid.shape, bool)
            region[:, 90:116] = True
            costs.append(dict(dy=dy, paired=int((mask & valid & region).sum()),
                              unpaired=int((mask & ~valid & region).sum()),
                              paired_points_yx=np.argwhere(mask & valid & region).tolist()))
        records.append(dict(name=name, pixel_values=points, shift_costs=costs))
    return records


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--suite', choices=('pdf', 'context', 'tone', 'identity', 'csharp', 'causes', 'all'), default='all')
    args = parser.parse_args()
    output = m.ROOT / 'out/t3-1b-probe/results/mapped-features'
    output.mkdir(parents=True, exist_ok=True)
    for name, run in [('pdf', pdf_cases), ('context', context_cases), ('tone', tone_cases),
                      ('identity', identity_cases), ('csharp', csharp_cases), ('causes', causes)]:
        if args.suite in ('all', name):
            result = run(output)
            (output / (name + '.json')).write_text(json.dumps(result, indent=2) + '\n')
            print(f'{name}: {len(result)} 条件を保存', flush=True)


if __name__ == '__main__':
    main()
