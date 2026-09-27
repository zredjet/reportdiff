"""元画像で計算した特徴量を写す独立試行。製品・参照実装は変更しない。"""
from __future__ import annotations

import dataclasses
import importlib.util
from pathlib import Path
import sys
import types

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('mapped_feature_reference', ROOT / 'reference/prototype.py')
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)


def profile(name):
    p = reference.Params()
    if name == 'strict':
        p.max_shift_mm, p.edge_tolerance = 0, 0
    elif name == 'loose':
        p.max_shift_mm = .30
    else:
        assert name == 'normal'
    return p


def features(image, p):
    lab = reference.to_lab(image)
    if p.edge_tolerance == 0:
        values, contrast = lab, np.zeros_like(lab)
    else:
        values, contrast = cv2.blur(lab, (3, 3)), reference._contrast(lab, 5)
    ink = reference._ink(lab, p)
    return values, contrast, ink


def insertion_map(height, cut, gap):
    return [(0, cut, 0, 0), (cut, gap, None, cut),
            (cut + gap, height - cut - gap, cut, cut + gap),
            (height, gap, height - gap, None)]


def map_array(source, bands, side, fill):
    """PageMap と同じ整数帯の転写。入力の全行を順序どおり一度ずつ写す。"""
    height = sum(b[1] for b in bands)
    result = np.empty((height,) + source.shape[1:], source.dtype)
    result[...] = fill
    next_canvas = next_source = 0
    for cy, length, ay, by in bands:
        assert cy == next_canvas and length > 0
        next_canvas += length
        sy = ay if side == 'a' else by
        if sy is not None:
            assert sy == next_source
            result[cy:cy + length] = source[sy:sy + length]
            next_source += length
    assert next_source == source.shape[0]
    return result


def map_features(source, bands, side):
    # 詰め物は一様な純白の特徴量。隣の実画素から再計算しない。
    return tuple(map_array(v, bands, side, fill)
                 for v, fill in zip(source, ([100, 0, 0], 0, False)))


def paired_mask(shape, bands):
    valid = np.zeros(shape, bool)
    for y, length, ay, by in bands:
        if ay is not None and by is not None:
            valid[y:y + length] = True
    return valid


def difference(a, b, p, trace=None):
    """参照実装 5.2/5.3 と同じ式・group・シフト順。入力だけが写像済み特徴量。"""
    va, ca, ia = a
    vb, cb, ib = b
    stats = dict(absorbed_groups=0, max_shift_px=0)
    def candidates(dx, dy):
        return (np.abs(va - reference._shift(vb, dx, dy)) >
                p.color_threshold + p.edge_tolerance *
                np.maximum(ca, reference._shift(cb, dx, dy))).any(axis=2)
    cand0 = candidates(0, 0)
    if trace is not None:
        trace['initial'] = cand0.copy()
    s = int(round(p.px(p.max_shift_mm)))
    if s <= 0 or not cand0.any():
        return cand0, stats
    region = cv2.dilate(cand0.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
    n_ink, ink_labels = cv2.connectedComponents((ia | ib).astype(np.uint8), connectivity=8)
    touched = np.zeros(n_ink, bool)
    touched[np.unique(ink_labels[cand0])] = True
    touched[0] = False
    region |= touched[ink_labels]
    region = cv2.dilate(region.astype(np.uint8), np.ones((2 * s + 1, 2 * s + 1), np.uint8))
    n, labels = cv2.connectedComponents(region, connectivity=8)
    shifts = sorted(((dx, dy) for dx in range(-s, s + 1) for dy in range(-s, s + 1)),
                    key=lambda t: (abs(t[0]) + abs(t[1]), t))
    masks, counts = [], []
    for dx, dy in shifts:
        c = cand0 if (dx, dy) == (0, 0) else candidates(dx, dy)
        masks.append(c)
        counts.append(np.bincount(labels[c], minlength=n))
    counts = np.array(counts)
    best = counts.argmin(axis=0)
    raw = np.zeros(cand0.shape, bool)
    for g in range(1, n):
        if counts[0, g] == 0:
            continue
        bi = int(best[g])
        if counts[bi, g] == 0:
            stats['absorbed_groups'] += 1
            stats['max_shift_px'] = max(stats['max_shift_px'], max(abs(v) for v in shifts[bi]))
        else:
            raw |= masks[bi] & (labels == g)
    if trace is not None:
        trace['groups'] = [dict(id=g, before=int(counts[0, g]), after=int(counts[best[g], g]),
                                shift=shifts[best[g]]) for g in range(1, n) if counts[0, g]]
    return raw, stats


def cluster(raw, stats, p):
    """参照実装の 5.5 を再利用。関数の大域変数・製品ファイルは書き換えない。"""
    def provided(_a, _b, _p, st):
        st.update(stats)
        return raw.copy()
    compare = types.FunctionType(reference.compare_page.__code__,
                                 dict(reference.compare_page.__globals__, tolerant_diff=provided))
    dummy = np.empty(raw.shape + (3,), np.uint8)
    return compare(dummy, dummy, p)


def compare(a, b, p, bands, reverse=False, trace=None):
    fa, fb = map_features(features(a, p), bands, 'a'), map_features(features(b, p), bands, 'b')
    raw, stats = difference(fb if reverse else fa, fa if reverse else fb, p, trace)
    # 承認済み案と同じく、構造帯の除外は二段目が終わってから。
    raw &= paired_mask(raw.shape, bands)
    return cluster(raw, stats, p)


def snapshot(result):
    return dict(status=result.status, raw_pixels=result.raw_pixels, noise_dropped=result.noise_dropped,
                absorbed_groups=result.absorbed_groups, max_shift_px=result.max_shift_px,
                clusters=[dataclasses.asdict(c) for c in result.clusters])


def read(path):
    return cv2.imdecode(np.frombuffer(Path(path).read_bytes(), np.uint8), cv2.IMREAD_COLOR)


def save(path, image):
    Path(path).write_bytes(cv2.imencode('.png', image)[1].tobytes())
