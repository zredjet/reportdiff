#!/usr/bin/env python3
"""固定PDFの命令・元PNG・C/Dをpypdf/Pillow/NumPyで独立に照合する。PDFは変更しない。"""
from decimal import Decimal
import hashlib
import json
from pathlib import Path
import sys

import numpy as np
from PIL import Image
from pypdf import PdfReader

base, output = map(lambda p: Path(p).resolve(), sys.argv[1:3])
data = json.loads((output / 'diagnostics.json').read_text())
prior = json.loads((base / 'audit.json').read_text())
hashfile = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
load = lambda p: np.array(Image.open(p))
records = []


def body(page, skia):
    text = [' '.join(s.split()) for s in page.extract_text().splitlines() if s.strip()][2:-2]
    operations = page.get_contents().operations
    blocks = []
    if skia:
        block = None
        for operands, op in operations:
            if op == b'q':
                assert block is None
                block = []
            if block is not None:
                values = [str(v) for v in operands]
                if op == b'cm':
                    assert values[:5] == ['1', '0', '0', '1', '0']
                    values[5] = '0'  # 各行の配置だけを正規化する。
                block.append([op.decode(), values])
            if op == b'Q':
                blocks.append(block)
                block = None
    else:
        for operands, op in operations:
            if op == b'BT':
                blocks.append([])
            if blocks:
                blocks[-1].append([op.decode(), [str(v) for v in operands]])
        blocks = blocks[2:-2]
        height = Decimal(str(page.mediabox.height))
        for i, block in enumerate(blocks):
            top = Decimal(96 + 24 * i)
            for op, v in block:
                if op in ['Tm', 're']:
                    index = 5 if op == 'Tm' else 1
                    v[index] = str((Decimal(v[index]) + top - height).normalize())
    assert len(blocks) == len(text)
    return list(zip(text, blocks))


def font_hash(page, skia):
    if skia:
        hashes = []
        for key in sorted(page['/Resources']['/Font']):
            font = page['/Resources']['/Font'][key]
            descendant = font['/DescendantFonts'][0]
            assert descendant['/CIDToGIDMap'] == '/Identity' and font['/Encoding'] == '/Identity-H'
            embedded = descendant['/FontDescriptor']['/FontFile2'].get_data()
            metrics = repr([descendant.get('/DW'), descendant.get('/W')]).encode()
            # ToUnicodeは追加文字Wの有無で異なる。描画は同じCID、同じ埋込字形・幅で検証する。
            hashes.append(key + hashlib.sha256(embedded + metrics).hexdigest())
        return hashlib.sha256(''.join(hashes).encode()).hexdigest()
    font = page['/Resources']['/Font']['/F1']
    streams = b''.join(font['/CharProcs'][k].get_data() for k in sorted(font['/CharProcs']))
    geometry = repr([font[k] for k in ['/Widths', '/FontMatrix', '/FontBBox', '/Encoding']]).encode()
    return hashlib.sha256(streams + geometry).hexdigest()


def difference(a, b):
    different = np.any(a != b, axis=2)
    y, x = np.where(different)
    return dict(pixels=int(different.sum()), bounds=None if len(x) == 0 else
                [int(x.min()), int(y.min()), int(x.max()) + 1, int(y.max()) + 1])


for record in data['records']:
    name = record['id']
    case = 'skia-a4-dense-2' if name == 'dense' else name + ('-a4-sparse-2' if name == 'sparse' else '-a4-2')
    directory = output / name
    measurement = base / 'measurements' / (case + '-true-product-0')
    report = json.loads((measurement / 'result.json').read_text())
    skia = name in ['skia', 'dense', 'inset']
    pitch = 50 if name == 'dense' else 100
    pdfs = [PdfReader(p) for p in record['files']]
    originals = {}
    shapes = []
    fonts = set()
    bodies = {}
    for side, pdf, path, digest in zip(['A', 'B'], pdfs, record['files'], record['sha256']):
        assert hashfile(path) == digest == next(p['sha256'] for p in prior['pdf_inputs']
                                              if p['case'] == case and p['side'] == side.lower())
        for page, p in enumerate(pdf.pages, 1):
            fonts.add(font_hash(p, skia))
            bodies[side, page] = body(p, skia)
            original = load(directory / f'{side}-{page}-original.png')
            originals[side, page] = original
            previous = report['pages'][page - 1]['raw_evidence'][side.lower()]['image']
            assert hashfile(directory / f'{side}-{page}-original.png') == hashfile(measurement / previous)
            ceil = load(directory / f'{side}-{page}-ceil.png')
            assert np.array_equal(original, ceil[:original.shape[0], :original.shape[1]])
            rows = np.where(np.any(original[300:3000] < 255, axis=(1, 2)))[0] + 300
            shapes.append(dict(side=side, page=page, size=[original.shape[1], original.shape[0]],
                               nonwhite_body=[int(rows[0]), int(rows[-1]) + 1], ceil_crop_equal=True))
    assert len(fonts) == 1, (name, 'rendering_font_changed')
    target = {text: (page, slot, block) for (side, page), rows in bodies.items() if side == 'B'
              for slot, (text, block) in enumerate(rows)}
    assert len(target) == sum(len(rows) for (side, page), rows in bodies.items() if side == 'B')
    vector_differences = []
    pairs = []
    for (side, page), rows in bodies.items():
        if side != 'A':
            continue
        for slot, (text, block) in enumerate(rows):
            bp, bs, other = target[text]
            if block != other:
                vector_differences.append(dict(text=text, a_page=page, a_slot=slot, b_page=bp, b_slot=bs,
                                               commands_a=block, commands_b=other))
            modes = {}
            for mode in ['original', 'ceil', 'floor', 'no-path-aa']:
                # 1回に一組だけ読み込む。対照の寸法・画素は製品の採用根拠へ渡さない。
                aa = originals['A', page] if mode == 'original' else load(directory / f'A-{page}-{mode}.png')
                bb = originals['B', bp] if mode == 'original' else load(directory / f'B-{bp}-{mode}.png')
                modes[mode] = difference(aa[400 + pitch * slot:400 + pitch * (slot + 1)],
                                         bb[400 + pitch * bs:400 + pitch * (bs + 1)])
            pairs.append(dict(text=text, a_page=page, a_slot=slot, b_page=bp, b_slot=bs,
                              vector_commands_equal=block == other, modes=modes))
    surfaces = []
    for page in record['pages']:
        if page['pieces'] is None:
            continue
        n = page['number']
        ca, cb = [load(directory / f'C-{n}-{s}.png') for s in ['A', 'B']]
        raw, display = [load(directory / f'{space}-{n}-raw.png') for space in ['C', 'D']]
        reconstructed_display = np.zeros_like(display)
        joins = []
        for side, content in [('A', ca), ('B', cb)]:
            reconstructed = np.full_like(content, 255)
            prior_end = 0
            for piece in page['pieces']:
                c, h, start = piece['content_start'], piece['length'], piece[side.lower() + '_start']
                if start != prior_end:
                    joins.append(c)
                if start is not None:
                    reconstructed[c:c+h] = originals[side, n][start:start+h]
                prior_end = None if start is None else start + h
            assert np.array_equal(content, reconstructed), (name, n, side, 'C_copy')
        for piece in page['pieces']:
            c, d, h = piece['content_start'], piece['display_start'], piece['length']
            reconstructed_display[d:d+h] = raw[c:c+h]
        assert np.array_equal(display, reconstructed_display), (name, n, 'D_projection')
        exact = difference(ca, cb)
        raw_y = np.where(np.any(raw != 0, axis=1))[0]
        distance = int(np.min(np.abs(raw_y[:, None] - np.array(joins)))) if len(raw_y) and joins else None
        assert distance is None or distance > page['neighborhood_radius']
        assert int(np.count_nonzero(raw)) == page['comparison']['candidate_raw'] == int(np.count_nonzero(display))
        surfaces.append(dict(page=n, reconstructed_c_equal=True, projected_d_equal=True, exact=exact,
                             raw_pixels=int(np.count_nonzero(raw)), raw_rows=raw_y.tolist(),
                             discontinuous_joins=sorted(set(joins)), minimum_distance_to_join=distance,
                             neighborhood_radius=page['neighborhood_radius'], adoption=page['comparison']['adoption']))
    records.append(dict(case=case, rendering_font_equal=True, original_images_equal_previous=True,
                        shapes=shapes, vector_differences=vector_differences, pairs=pairs, surfaces=surfaces))
    print(case, '対応行', len(pairs), '命令不一致', len(vector_differences), 'C/D', len(surfaces), flush=True)

result = dict(records=records, source_diagnostics_sha256=hashfile(output / 'diagnostics.json'))
(output / 'audit.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
