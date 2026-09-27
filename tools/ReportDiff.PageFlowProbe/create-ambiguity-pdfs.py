#!/usr/bin/env python3
"""境界再検証と数値・色・縦補正の合成PDF。既存の自作Type3を保ち、製品依存を追加しない。"""
import hashlib, json, sys
from pathlib import Path
from pypdf import PdfWriter
from pypdf.generic import DecodedStreamObject, NameObject, RectangleObject

root = Path(__file__).resolve().parents[2]
source = root/'tests/ReportDiff.Tests/Fixtures/page-flow-shared'
out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=False)
cases = [
    ('chain-number', 'shared-chain', 'ROW ITEM PP', None, True),
    ('chain-support-number', 'shared-chain', 'ROW ITEM II', None, False),
    ('chain-carry-number', 'shared-chain', 'ROW ITEM KK', None, False),
    ('independent-number', 'shared-and-independent', 'SECOND ITEM JJ', None, True),
    ('independent-endpoint-number', 'shared-and-independent', 'FIRST ITEM JJ', None, False),
    ('independent-tone', 'shared-and-independent', None, None, True),
    ('chain-down', 'shared-chain', None, [4, 4, 4], True),
    ('chain-up', 'shared-chain', None, [-6, -6, -6], True),
    ('chain-variable', 'shared-chain', None, [4, -6, 3], None),
    ('independent-global', 'shared-and-independent', None, [4, 4, 4, 4], True),
]
hashes = {}; manifest = []
for name, base, replace, shifts, expected in cases:
    for side in ['a', 'b']:
        writer = PdfWriter(clone_from=source/base/(side+'.pdf')); changed = 0
        for i, page in enumerate(writer.pages):
            data = page.get_contents().get_data()
            if replace:
                old = ('<'+replace.encode().hex().upper()+'>').encode()
                new = ('<'+('SUM TOTAL 555' if side == 'a' else 'SUM TOTAL 556').encode().hex().upper()+'>').encode()
                changed += data.count(old); data = data.replace(old, new)
            if name == 'independent-tone' and side == 'b' and i == 1:
                # 仮の送り元の対応先にある本文だけを着色する。文字層と罫線は不変。
                old = ('<'+b'FIRST ITEM II'.hex().upper()+'>').encode()
                assert data.count(old) == 1
                data = data.replace(old, b'0.65 g '+old).replace(b'Tj ET', b'Tj ET 0 g')
            if shifts:
                pad = 512; height = 1250 + 2 * pad; scale = .24
                data = f'q 1 0 0 1 0 {pad*scale:.8f} cm\n'.encode()+data+b'\nQ\n0 g\n'
                for start in [64, height - 64 - 440]:
                    for y in range(0, 440, 9):
                        for x in range(64, 1000-72, 9):
                            if (x*37+y*17+x*y*13) % 11 < 9:
                                data += f'{x*scale:.8f} {(height-start-y-8)*scale:.8f} {8*scale:.8f} {8*scale:.8f} re f\n'.encode()
                dy = shifts[i] if side == 'b' else 0
                data = f'q 1 0 0 1 0 {-dy*scale:.8f} cm\n'.encode()+data+b'Q\n'
                page.mediabox = RectangleObject([0, 0, 240, height * scale])
            stream = DecodedStreamObject(); stream.set_data(data); page[NameObject('/Contents')] = writer._add_object(stream)
            page.compress_content_streams()
        if replace: assert changed == 1, (name, side, changed)
        directory = out/name; directory.mkdir(exist_ok=True); path = directory/(side+'.pdf')
        with path.open('wb') as f: writer.write(f)
        hashes[str(path.relative_to(out))] = hashlib.sha256(path.read_bytes()).hexdigest()
    manifest.append(dict(id=name, base=base, global_shifts=shifts, expected=expected))
(out/'sha256.json').write_text(json.dumps(hashes, indent=2)+'\n')
(out/'cases.json').write_text(json.dumps(manifest, indent=2)+'\n')
print(len(hashes), 'PDFs')
