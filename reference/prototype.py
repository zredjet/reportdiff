#!/usr/bin/env python3
"""
ReportDiff 比較コアの参照実装（Python + OpenCV）

目的:
  - docs/SPEC.md の「5. 比較アルゴリズム」を実行可能な形で示す
  - 既定パラメータがゴールデンケースを満たすことを確認する
  - C# 実装 (ReportDiff.Core) の移植元・照合先にする

実行:
  pip install opencv-python-headless numpy pillow
  python reference/prototype.py                          # 全ゴールデンケースを実行
  python reference/prototype.py --dump reference/golden  # 入力 PNG と expected.json を再生成
  python reference/prototype.py --only J01,J03           # 一部だけ実行
  python reference/prototype.py --dump out --overlay     # 重ね描き画像も保存して目で確認

注意: これは製品コードではない。製品は C# で実装する。速度は最適化していない。
"""
from __future__ import annotations

import argparse
import math
import os
import sys
from dataclasses import dataclass, field

import cv2
import numpy as np

# --------------------------------------------------------------------------
# パラメータ（SPEC 7章の既定値と一致させること）
# --------------------------------------------------------------------------


@dataclass
class Params:
    dpi: int = 300                 # 200 では 9pt の 未/末・ば/ぱ を安定して検出できない (SPEC 5.6)
    max_shift_mm: float = 0.15     # 「位置ずれ」として吸収する最大量 -> s px (SPEC 5.3)
    color_threshold: float = 3.0   # 平坦部の色差しきい値 (Lab 各チャンネルの差)
    edge_tolerance: float = 0.3    # 局所コントラストに比例して許容を広げる係数 k。0 で厳密比較
    ink_background_radius_mm: float = 1.5  # 局所背景の半径（YAML: ink.background_radius_mm）
    ink_contrast_threshold: float = 25.0   # インクとする Lab L 差（YAML: ink.contrast_threshold）
    reading_band_mm: float = 5.0           # クラスタの読み順（YAML: cluster.reading_band_mm）
    merge_x_mm: float = 3.0        # この間隔以下の差分画素は横方向に結合
    merge_y_mm: float = 1.0        # 同、縦方向
    min_pixels: int = 4            # 生の差分画素数がこれ未満のクラスタは捨てる
    max_diff_ratio: float = 0.30   # 生の差分がページのこの割合を超えたら too_different
    exclude_mm: list = field(default_factory=list)  # [(x, y, w, h)] mm, 左上原点

    def px(self, mm: float) -> float:
        return mm / 25.4 * self.dpi


STRICT = dict(max_shift_mm=0.0, edge_tolerance=0.0)   # 同一エンジンでの回帰テスト向け


# --------------------------------------------------------------------------
# 比較コア
# --------------------------------------------------------------------------


def to_lab(bgr_u8: np.ndarray) -> np.ndarray:
    """BGR 8bit -> Lab float32 (L:0..100, a,b:約-127..127)。8bit Lab は使わない。"""
    return cv2.cvtColor(bgr_u8.astype(np.float32) / 255.0, cv2.COLOR_BGR2Lab)


def _contrast(lab: np.ndarray, size: int) -> np.ndarray:
    """近傍 size x size 内の (max - min)。チャンネル別。エッジ付近で大きく、平坦部で 0。"""
    k = np.ones((size, size), np.uint8)
    return cv2.dilate(lab, k) - cv2.erode(lab, k)


def _shift(img: np.ndarray, dx: int, dy: int) -> np.ndarray:
    """整数 px の平行移動。端は複製。"""
    if dx == 0 and dy == 0:
        return img
    h, w = img.shape[:2]
    m = np.float32([[1, 0, dx], [0, 1, dy]])
    return cv2.warpAffine(img, m, (w, h), flags=cv2.INTER_NEAREST, borderMode=cv2.BORDER_REPLICATE)


def _ink(lab: np.ndarray, p: Params) -> np.ndarray:
    """局所背景より L がしきい値を超えて暗い画素。既定は半径 1.5mm、差 25。"""
    m = max(1, int(round(p.px(p.ink_background_radius_mm))))
    bg = cv2.dilate(lab[:, :, 0], np.ones((2 * m + 1, 2 * m + 1), np.uint8))
    return (bg - lab[:, :, 0]) > p.ink_contrast_threshold


def tolerant_diff(a_bgr: np.ndarray, b_bgr: np.ndarray, p: Params, stats: dict | None = None) -> np.ndarray:
    """許容つき差分。戻り値は bool マスク (True = 差分)。SPEC 5.2〜5.3。

    一段目 (候補の抽出):
        3x3 平均どうしを比べ、しきい値を局所コントラスト (5x5) に比例して広げる。
            |blur3(A) - blur3(B)|_c  >  color_threshold + edge_tolerance * max(contrast5(A), contrast5(B))_c
        がどれかのチャンネル c で成り立つ画素が候補。0.5px 以下のずれ (斜め含む) はここで消える。
    二段目 (ずれで説明できる候補の吸収):
        候補を「つながったインク」単位のグループに分け、B を (dx, dy) だけ動かして一段目をやり直す。
        グループ内の候補が 0 になるずれがあれば、そのグループは位置ずれとして吸収する。
        残る場合は、最も候補が少なくなるずれでの残りを差分とする。
        画素ごとにずれ方向を選ばせない (グループで 1 つ) ので、両端が伸びた線や太くなった線、
        未/末 のような字形差は吸収されない。
    """
    assert a_bgr.shape == b_bgr.shape
    h, w = a_bgr.shape[:2]
    if stats is None:
        stats = {}
    stats.update(absorbed_groups=0, max_shift_px=0)
    if not np.any(cv2.absdiff(a_bgr, b_bgr)):            # 高速パス: 完全一致
        return np.zeros((h, w), dtype=bool)

    la, lb = to_lab(a_bgr), to_lab(b_bgr)
    k = p.edge_tolerance
    if k == 0:                                           # 厳密比較: 画素そのものを比べる
        blur_a, blur_b = la, lb
        con_a = con_b = np.zeros_like(la)
    else:
        blur_a, blur_b = cv2.blur(la, (3, 3)), cv2.blur(lb, (3, 3))
        con_a, con_b = _contrast(la, 5), _contrast(lb, 5)

    def candidates(dx: int, dy: int) -> np.ndarray:
        bb, cb = _shift(blur_b, dx, dy), _shift(con_b, dx, dy)
        return (np.abs(blur_a - bb) > p.color_threshold + k * np.maximum(con_a, cb)).any(axis=2)

    cand0 = candidates(0, 0)
    s = int(round(p.px(p.max_shift_mm)))
    if s <= 0 or not cand0.any():
        return cand0

    # グループ = 候補の近傍 (5x5) ∪ 候補に触れているインク連結成分
    region = cv2.dilate(cand0.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
    ink = (_ink(la, p) | _ink(lb, p)).astype(np.uint8)
    n_ink, ink_labels = cv2.connectedComponents(ink, connectivity=8)
    touched = np.zeros(n_ink, dtype=bool)
    touched[np.unique(ink_labels[cand0])] = True
    touched[0] = False
    region |= touched[ink_labels]
    region = cv2.dilate(region.astype(np.uint8), np.ones((2 * s + 1, 2 * s + 1), np.uint8))
    n, labels = cv2.connectedComponents(region, connectivity=8)

    # ずれ候補は |dx|+|dy| の小さい順。同数なら小さいずれを採用する
    shifts = sorted(((dx, dy) for dx in range(-s, s + 1) for dy in range(-s, s + 1)), key=lambda t: (abs(t[0]) + abs(t[1]), t))
    masks, counts = [], []
    for (dx, dy) in shifts:
        c = cand0 if (dx, dy) == (0, 0) else candidates(dx, dy)
        masks.append(c)
        counts.append(np.bincount(labels[c], minlength=n))
    counts = np.array(counts)                # (ずれの数, グループ数)
    best = counts.argmin(axis=0)

    raw = np.zeros((h, w), dtype=bool)
    for g in range(1, n):
        if counts[0, g] == 0:
            continue
        bi = int(best[g])
        if counts[bi, g] == 0:
            stats["absorbed_groups"] += 1
            stats["max_shift_px"] = max(stats["max_shift_px"], max(abs(shifts[bi][0]), abs(shifts[bi][1])))
            continue
        raw |= masks[bi] & (labels == g)
    return raw


@dataclass
class Cluster:
    id: int
    x: int
    y: int
    w: int
    h: int
    pixels: int
    fill_ratio: float


@dataclass
class PageResult:
    status: str                  # "same" | "different" | "too_different"
    clusters: list
    raw_pixels: int
    noise_dropped: int
    absorbed_groups: int
    max_shift_px: int
    raw_mask: np.ndarray
    label_mask: np.ndarray       # 採用クラスタの結合後領域 (輪郭描画用)


def _half(gap_px: float) -> int:
    return int(math.ceil(gap_px / 2.0))


def compare_page(a_bgr: np.ndarray, b_bgr: np.ndarray, p: Params) -> PageResult:
    h, w = a_bgr.shape[:2]
    st: dict = {}
    raw = tolerant_diff(a_bgr, b_bgr, p, st)
    ab, ms = st.get('absorbed_groups', 0), st.get('max_shift_px', 0)

    # 除外領域
    for (ex, ey, ew, eh) in p.exclude_mm:
        x0, y0 = int(math.floor(p.px(ex))), int(math.floor(p.px(ey)))
        x1, y1 = int(math.ceil(p.px(ex + ew))), int(math.ceil(p.px(ey + eh)))
        raw[max(0, y0):min(h, y1), max(0, x0):min(w, x1)] = False

    raw_pixels = int(raw.sum())
    empty = np.zeros((h, w), np.uint8)
    if raw_pixels == 0:
        return PageResult("same", [], 0, 0, ab, ms, raw, empty)
    if raw_pixels / float(h * w) > p.max_diff_ratio:
        return PageResult("too_different", [], raw_pixels, 0, ab, ms, raw, empty)

    # 結合: 間隔 gap 以下の差分画素がつながるように膨張
    hx, hy = _half(p.px(p.merge_x_mm)), _half(p.px(p.merge_y_mm))
    merged = cv2.dilate(raw.astype(np.uint8), np.ones((2 * hy + 1, 2 * hx + 1), np.uint8))
    n, labels = cv2.connectedComponents(merged, connectivity=8)

    counts = np.bincount(labels[raw], minlength=n)  # クラスタごとの「生の」差分画素数
    clusters, dropped = [], 0
    keep = np.zeros(n, dtype=bool)
    for i in range(1, n):
        if counts[i] < p.min_pixels:
            dropped += 1
            continue
        keep[i] = True
        ys, xs = np.nonzero(raw & (labels == i))   # 矩形は生の差分画素にぴったり合わせる
        x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
        bw, bh = int(x1 - x0 + 1), int(y1 - y0 + 1)
        clusters.append(Cluster(0, int(x0), int(y0), bw, bh, int(counts[i]), counts[i] / float(bw * bh)))

    # 読み順 (既定5mmの帯で上から、帯の中は左から) に並べて採番
    band = max(1, int(round(p.px(p.reading_band_mm))))
    clusters.sort(key=lambda c: (c.y // band, c.x))
    for idx, c in enumerate(clusters, 1):
        c.id = idx

    label_mask = (keep[labels]).astype(np.uint8) * 255
    status = "different" if clusters else "same"
    return PageResult(status, clusters, raw_pixels, dropped, ab, ms, raw, label_mask)


def draw_overlay(b_bgr: np.ndarray, res: PageResult) -> np.ndarray:
    out = b_bgr.copy()
    out[res.raw_mask] = (0, 0, 255)
    contours, _ = cv2.findContours(res.label_mask, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)  # EXTERNAL だと枠の内側のクラスタが描かれない
    cv2.drawContours(out, contours, -1, (0, 0, 255), 1)
    for c in res.clusters:
        cv2.putText(out, str(c.id), (c.x, max(12, c.y - 4)), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1, cv2.LINE_AA)
    return out


# --------------------------------------------------------------------------
# 合成画像の生成（フォント非依存。OpenCV 内蔵の Hershey フォントのみ使用）
# 4 倍で描いて INTER_AREA で縮小 -> アンチエイリアスとサブピクセルずれを再現
# --------------------------------------------------------------------------

SS = 4                 # スーパーサンプリング倍率
DPI = 300
W, H = 1350, 960       # 1 倍でのキャンバス (300dpi で約 114 x 81 mm)


def mm(v: float) -> int:
    """mm -> 4 倍キャンバス上の px"""
    return int(round(v / 25.4 * DPI * SS))


class Scene:
    """描画命令を溜め、オブジェクト群ごとのオフセット付きで描く。"""

    def __init__(self):
        self.ops = []

    def add(self, fn, group: str = "g0"):
        self.ops.append((group, fn))
        return self

    def render(self, shift_px=(0.0, 0.0), group_shift_px=None) -> np.ndarray:
        """shift_px: 全体ずれ (1 倍 px, 0.25 刻み)。group_shift_px: {group: (dx, dy)}"""
        group_shift_px = group_shift_px or {}
        img = np.full((H * SS, W * SS, 3), 255, np.uint8)
        for group, fn in self.ops:
            gx, gy = group_shift_px.get(group, (0.0, 0.0))
            ox = int(round((shift_px[0] + gx) * SS))
            oy = int(round((shift_px[1] + gy) * SS))
            fn(img, ox, oy)
        return cv2.resize(img, (W, H), interpolation=cv2.INTER_AREA)


def op_line(x0, y0, x1, y1, color=(0, 0, 0), width_px=1.0):
    t = max(1, int(round(width_px * SS)))
    return lambda img, ox, oy: cv2.line(img, (mm(x0) + ox, mm(y0) + oy), (mm(x1) + ox, mm(y1) + oy), color, t)


def op_rect(x, y, w, h, color=(0, 0, 0), width_px=1.0, fill=None):
    t = max(1, int(round(width_px * SS)))

    def fn(img, ox, oy):
        p0, p1 = (mm(x) + ox, mm(y) + oy), (mm(x + w) + ox, mm(y + h) + oy)
        if fill is not None:
            cv2.rectangle(img, p0, p1, fill, -1)
        if width_px > 0:
            cv2.rectangle(img, p0, p1, color, t)
    return fn


def op_text(x, y, text, size_mm=3.2, color=(0, 0, 0)):
    """x, y はベースライン左端 (mm)。size_mm はおよその文字高さ。"""
    scale = mm(size_mm) / 22.0          # Hershey SIMPLEX は scale=1 で高さ約 22px
    thick = max(1, int(round(scale * 1.6)))
    return lambda img, ox, oy: cv2.putText(img, text, (mm(x) + ox, mm(y) + oy), cv2.FONT_HERSHEY_SIMPLEX, scale, color, thick, cv2.LINE_8)


def op_dot(x, y, d_mm=0.45, color=(0, 0, 0)):
    return lambda img, ox, oy: cv2.circle(img, (mm(x) + ox, mm(y) + oy), max(1, mm(d_mm) // 2), color, -1)


def op_dashed(x0, y, x1, dash_mm=2.0, gap_mm=1.0, color=(0, 0, 0), width_px=1.0):
    t = max(1, int(round(width_px * SS)))

    def fn(img, ox, oy):
        x = x0
        while x < x1:
            xe = min(x + dash_mm, x1)
            cv2.line(img, (mm(x) + ox, mm(y) + oy), (mm(xe) + ox, mm(y) + oy), color, t)
            x += dash_mm + gap_mm
    return fn


def op_triangle(cx, cy, size_mm=2.0, color=(0, 0, 0)):
    def fn(img, ox, oy):
        s = size_mm / 2
        pts = np.array([[mm(cx) + ox, mm(cy - s) + oy], [mm(cx - s) + ox, mm(cy + s) + oy], [mm(cx + s) + ox, mm(cy + s) + oy]], np.int32)
        cv2.fillPoly(img, [pts], color)
    return fn


def base_scene(amount="120", dot=True, minus=True, bar_end=70.0, bar_color=(160, 60, 0), dashed=False,
               cell_fill=None, marker=False, frame_px=1.0, second="ABC", pair="XY") -> Scene:
    """表 + 工程線を持つ小さな帳票。引数を変えると 1 箇所だけ違う画像が作れる。"""
    s = Scene()
    # 表の外枠と罫線
    s.add(op_rect(8, 8, 96, 24, width_px=frame_px), "table")
    for yy in (16, 24):
        s.add(op_line(8, yy, 104, yy), "table")
    for xx in (40, 72):
        s.add(op_line(xx, 8, xx, 32), "table")
    # セルの塗り
    if cell_fill is not None:
        s.add(op_rect(73.0, 17.0, 30.0, 6.0, width_px=0, fill=cell_fill), "table")
    # 文字
    s.add(op_text(10, 14, "QTY"), "t1")
    s.add(op_text(42, 14, amount), "t2")
    s.add(op_text(10, 22, "RATE"), "t3")
    s.add(op_text(42, 22, "1"), "t4")
    if dot:
        s.add(op_dot(45.6, 21.8), "t4")
    s.add(op_text(46.4, 22, "25"), "t4")
    s.add(op_text(10, 30, "BAL"), "t5")
    if minus:
        s.add(op_line(42.0, 28.6, 43.6, 28.6, width_px=1.4), "t6")
    s.add(op_text(44.2, 30, "300"), "t6")
    s.add(op_text(74, 14, second), "t7")
    s.add(op_text(74, 30, pair), "t8")
    # 工程線
    s.add(op_text(8, 44, "TASK A"), "g1")
    if dashed:
        s.add(op_dashed(30, 43, bar_end, color=bar_color, width_px=1.0), "bar")
    else:
        s.add(op_line(30, 43, bar_end, 43, color=bar_color, width_px=1.0), "bar")
    s.add(op_text(8, 54, "TASK B"), "g2")
    s.add(op_line(30, 53, 90, 53, color=(0, 0, 0), width_px=3.0), "bar2")
    if marker:
        s.add(op_triangle(60, 62, 2.0), "mk")
    return s


# --------------------------------------------------------------------------
# ゴールデンケース
# --------------------------------------------------------------------------


@dataclass
class Case:
    id: str
    title: str
    a: np.ndarray
    b: np.ndarray
    expect: str                     # "ignore" | "detect" | "limit" (結果を表示するだけ)
    n_clusters: int | None = None   # detect のとき、期待クラスタ数 (None は 1 以上)
    must_hit_mm: list = field(default_factory=list)  # [(x, y, w, h)] 各矩形に重なるクラスタが 1 つ以上あること
    params: Params | None = None


def build_cases() -> list:
    cases = []
    base = base_scene()
    A = base.render()

    # ---- 無視必須 ----
    cases.append(Case("I01", "完全一致", A, base.render(), "ignore"))
    for i, sh in enumerate([(0.25, 0), (0.5, 0), (0.75, 0), (0, 0.5), (0.5, 0.5)]):
        cases.append(Case(f"I02-{i+1}", f"全体サブピクセルずれ {sh}", A, base.render(shift_px=sh), "ignore"))
    for i, sh in enumerate([(1, 0), (0, 1), (1, 1), (-1, 1)]):
        cases.append(Case(f"I03-{i+1}", f"全体 1px ずれ {sh}", A, base.render(shift_px=sh), "ignore"))
    cases.append(Case("I03-5", "全体 1.5px ずれ", A, base.render(shift_px=(1.5, 0)), "ignore"))
    cases.append(Case("L02", "[限界] 全体 3px ずれ (max_shift を超える)", A, base.render(shift_px=(3, 0)), "limit"))
    gs = {"t1": (0.5, 0), "t2": (0.25, 0.25), "t3": (-0.5, 0), "t4": (0.75, 0), "t6": (0, 0.5), "bar": (0, 0.5), "bar2": (0.5, 0.25), "table": (0.25, 0)}
    cases.append(Case("I04", "オブジェクトごとに異なるサブピクセルずれ", A, base.render(group_shift_px=gs), "ignore"))
    p_ex = Params(exclude_mm=[(40.5, 8.5, 31.0, 7.0)])
    cases.append(Case("I05", "除外領域内だけの変更", A, base_scene(amount="999").render(), "ignore", params=p_ex))

    # ---- 検出必須 ----
    cases.append(Case("D01", "小数点の有無", A, base_scene(dot=False).render(), "detect", 1, [(45.0, 21.0, 1.2, 1.4)]))
    cases.append(Case("D02", "マイナス記号の有無", A, base_scene(minus=False).render(), "detect", 1, [(41.8, 28.0, 2.0, 1.2)]))
    cases.append(Case("D03", "数字 1 文字の変更 120 -> 125", A, base_scene(amount="125").render(), "detect", 1, [(46.0, 10.5, 3.5, 4.0)]))
    cases.append(Case("D06a", "1px 線の 2mm 延長", A, base_scene(bar_end=72.0).render(), "detect", 1, [(70.0, 42.5, 2.0, 1.0)]))
    cases.append(Case("D07", "線色のみ変更 (青 -> 赤)", A, base_scene(bar_color=(0, 60, 160)).render(), "detect", 1, [(30, 42.5, 40, 1.0)]))
    cases.append(Case("D08", "実線 -> 破線", A, base_scene(dashed=True).render(), "detect", None, [(30, 42.5, 40, 1.0)]))
    cases.append(Case("D09", "セル塗り 白 -> #F2F2F2", A, base_scene(cell_fill=(242, 242, 242)).render(), "detect", 1, [(73, 17, 30, 6)]))
    cases.append(Case("D10", "2mm マーカーの追加", A, base_scene(marker=True).render(), "detect", 1, [(59, 61, 2, 2)]))
    cases.append(Case("D11", "外枠の太さ変更 + 枠内の数値変更", A, base_scene(frame_px=5.0, amount="125").render(), "detect", None, [(46.0, 10.5, 3.5, 4.0)]))
    cases.append(Case("D12a", "離れた 2 箇所の変更", A, base_scene(amount="125", pair="XZ").render(), "detect", 2, [(46.0, 10.5, 3.5, 4.0), (76.5, 26.5, 3.5, 4.0)]))
    cases.append(Case("D12b", "同一行の隣接 2 文字の変更", A, base_scene(second="AXY").render(), "detect", 1, [(76.5, 10.5, 6.5, 4.0)]))
    cases.append(Case("D13", "数値変更 + 全体 0.5px ずれ", A, base_scene(amount="125").render(shift_px=(0.5, 0.5)), "detect", 1, [(46.0, 10.5, 3.5, 4.0)]))
    cases.append(Case("D14", "3px 線の色を薄く (同系色)", A, base_scene_bar2((110, 110, 110)).render(), "detect", 1, [(30, 52.3, 60, 1.4)]))

    # ---- 厳密モード (max_shift_mm = 0, edge_tolerance = 0)。同一エンジンでの回帰テスト向け ----
    strict = Params(**STRICT)
    cases.append(Case("S01", "[厳密] 外枠 1px -> 3px", A, base_scene(frame_px=3.0).render(), "detect", None, [(8, 8, 96, 24)], params=strict))
    cases.append(Case("S02", "[厳密] 完全一致", A, base.render(), "ignore", params=strict))

    # ---- 既知の限界 (既定の許容設定では検出できない。仕様として記録) ----
    cases.append(Case("D15", "線幅 1px -> 3px (各辺 1px 増)", A, base_scene(frame_px=3.0).render(), "detect", None, [(8, 8, 96, 24)]))
    cases.append(Case("L01", "[限界] 1px 線を同系色で薄く (ΔL 約 20)", A, base_scene(bar_color=(190, 115, 70)).render(), "limit"))
    return cases


def base_scene_bar2(color) -> Scene:
    s = base_scene()
    s.ops = [(g, f) for (g, f) in s.ops if g != "bar2"]
    s.add(op_line(30, 53, 90, 53, color=color, width_px=3.0), "bar2")
    return s


# --------------------------------------------------------------------------
# 日本語グリフのケース（フォント依存。生成済み PNG を reference/golden に同梱）
# --------------------------------------------------------------------------

JP_FONT_CANDIDATES = [
    "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
    "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc",
    "C:/Windows/Fonts/msgothic.ttc",
]


def _jp_render(lines, shift_px=(0.0, 0.0)):
    from PIL import Image, ImageDraw, ImageFont
    font_path = next((f for f in JP_FONT_CANDIDATES if os.path.exists(f)), None)
    if font_path is None:
        return None
    font = ImageFont.truetype(font_path, size=int(round(9.0 / 72.0 * DPI)) * SS, index=0)   # 9pt
    img = Image.new("RGB", (W * SS, H * SS), "white")
    d = ImageDraw.Draw(img)
    for (x_mm, y_mm, text) in lines:
        d.text((mm(x_mm) + int(round(shift_px[0] * SS)), mm(y_mm) + int(round(shift_px[1] * SS))), text, fill="black", font=font)
    arr = cv2.cvtColor(np.asarray(img), cv2.COLOR_RGB2BGR)
    return cv2.resize(arr, (W, H), interpolation=cv2.INTER_AREA)


def build_jp_cases() -> list:
    base = [(10, 10, "工程表 ばら積み データ集計"), (10, 20, "未払金 1,250 円"), (10, 30, "備考: 変更なし")]
    a = _jp_render(base)
    if a is None:
        return []

    def v(i, text):
        lines = list(base)
        lines[i] = (lines[i][0], lines[i][1], text)
        return _jp_render(lines)

    return [
        Case("J01", "濁点 -> 半濁点 (ば -> ぱ)", a, v(0, "工程表 ぱら積み データ集計"), "detect", 1),
        Case("J02", "長音 -> 漢数字 (ー -> 一)", a, v(0, "工程表 ばら積み デ一タ集計"), "detect", 1),
        Case("J03", "似た漢字 (未 -> 末)", a, v(1, "末払金 1,250 円"), "detect", 1),
        Case("J04", "桁区切りの有無 (1,250 -> 1250)", a, v(1, "未払金 1250 円"), "detect", 1),
        Case("J05", "日本語文字列の 0.5px ずれ", a, _jp_render(base, (0.5, 0.5)), "ignore"),
        Case("J06", "日本語文字列の 1px ずれ", a, _jp_render(base, (1, 0)), "ignore"),
    ]


def _overlaps(c: Cluster, rect_mm, p: Params) -> bool:
    x, y, w, h = [p.px(v) for v in rect_mm]
    return not (c.x + c.w < x or x + w < c.x or c.y + c.h < y or y + h < c.y)


def run(dump_dir: str | None, only: set | None = None, overlay: bool = False) -> int:
    failures = 0
    print(f"{'ID':6} {'結果':4} {'状態':14} {'クラスタ':>6} {'生px':>7} {'除去':>4} {'吸収':>4}  内容")
    expected = []
    for case in build_cases() + build_jp_cases():
        if only and case.id not in only:
            continue
        p = case.params or Params()
        res = compare_page(case.a, case.b, p)
        ok = True
        why = ""
        if case.expect == "limit":
            expected.append({"id": case.id, "title": case.title, "expect": "limit", "params": {k: v for k, v in vars(p).items()},
                             "status": res.status, "raw_pixels": res.raw_pixels, "noise_dropped": res.noise_dropped, "absorbed_groups": res.absorbed_groups, "max_shift_px": res.max_shift_px, "clusters": [vars(c) for c in res.clusters]})
            if dump_dir:
                os.makedirs(dump_dir, exist_ok=True)
                cv2.imwrite(os.path.join(dump_dir, f"{case.id}_a.png"), case.a)
                cv2.imwrite(os.path.join(dump_dir, f"{case.id}_b.png"), case.b)
            print(f"{case.id:6} {'--':4} {res.status:14} {len(res.clusters):6d} {res.raw_pixels:7d} {res.noise_dropped:4d} {res.absorbed_groups:4d}  {case.title}", flush=True)
            continue
        if case.expect == "ignore":
            ok = res.status == "same"
            if not ok:
                why = "差分なしのはずが検出された"
        else:
            if res.status != "different":
                ok, why = False, f"検出されるはずが status={res.status}"
            elif case.n_clusters is not None and len(res.clusters) != case.n_clusters:
                ok, why = False, f"クラスタ数 {len(res.clusters)} != 期待 {case.n_clusters}"
            else:
                for rect in case.must_hit_mm:
                    if not any(_overlaps(c, rect, p) for c in res.clusters):
                        ok, why = False, f"期待位置 {rect} mm に重なるクラスタがない"
            if ok and case.id == "D11":
                # 枠内の数値変更が、表全体を覆う枠クラスタとは別の小さなクラスタとして出ること
                small = [c for c in res.clusters if _overlaps(c, case.must_hit_mm[0], p) and c.w < p.px(12) and c.h < p.px(8)]
                if not small:
                    ok, why = False, "数値変更が枠のクラスタに埋もれた"
        failures += 0 if ok else 1
        print(f"{case.id:6} {'OK' if ok else 'NG':4} {res.status:14} {len(res.clusters):6d} {res.raw_pixels:7d} {res.noise_dropped:4d} {res.absorbed_groups:4d}  {case.title}  {why}", flush=True)
        expected.append({
            "id": case.id, "title": case.title, "expect": case.expect,
            "params": {k: v for k, v in vars(p).items()},
            "status": res.status, "raw_pixels": res.raw_pixels, "noise_dropped": res.noise_dropped,
            "absorbed_groups": res.absorbed_groups, "max_shift_px": res.max_shift_px,
            "clusters": [vars(c) for c in res.clusters],
        })
        if dump_dir:
            os.makedirs(dump_dir, exist_ok=True)
            cv2.imwrite(os.path.join(dump_dir, f"{case.id}_a.png"), case.a)
            cv2.imwrite(os.path.join(dump_dir, f"{case.id}_b.png"), case.b)
            if overlay:
                cv2.imwrite(os.path.join(dump_dir, f"{case.id}_overlay.png"), draw_overlay(case.b, res))
    if dump_dir:
        import json
        name = "expected.partial.json" if only else "expected.json"   # --only のときは正本を上書きしない
        with open(os.path.join(dump_dir, name), "w", encoding="utf-8") as f:
            json.dump({"opencv": cv2.__version__, "cases": expected}, f, ensure_ascii=False, indent=1)
    print(f"\n{'全ケース成功' if failures == 0 else f'{failures} 件失敗'}")
    return 1 if failures else 0


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", default=None, help="入力画像と重ね描き画像の保存先")
    ap.add_argument("--only", default=None, help="実行するケース ID (カンマ区切り)")
    ap.add_argument("--overlay", action="store_true", help="--dump のとき重ね描き画像も保存する")
    args = ap.parse_args()
    sys.exit(run(args.dump, set(args.only.split(",")) if args.only else None, args.overlay))
