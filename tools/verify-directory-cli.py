#!/usr/bin/env python3
"""合成データによる compare-dir の実 CLI 計測とブラウザ用結果生成（macOS 開発環境）。"""
import argparse
import json
import os
from pathlib import Path
import re
import statistics
import struct
import subprocess
import time
import zlib


def png(changed=False):
    def chunk(name, data):
        return struct.pack('>I', len(data)) + name + data + struct.pack('>I', zlib.crc32(name + data))
    rows = b''.join(b'\0' + b''.join(bytes([0 if changed and 20 <= x < 30 and 20 <= y < 28 else 255]) * 3
                                  for x in range(64)) for y in range(64))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>2I5B', 64, 64, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b'')


def pdf(changed=False):
    content = b'BT /F1 12 Tf 20 180 Td (ReportDiff Synthetic) Tj ET\n' + (b'0 0 0 rg 20 40 40 12 re f\n' if changed else b'')
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
               b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 216 216] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
               b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
               b'<< /Length ' + str(len(content)).encode() + b' >>\nstream\n' + content + b'endstream']
    result = bytearray(b'%PDF-1.7\n'); offsets = [0]
    for index, obj in enumerate(objects, 1):
        offsets.append(len(result)); result.extend(f'{index} 0 obj\n'.encode() + obj + b'\nendobj\n')
    xref = len(result)
    result.extend(f'xref\n0 {len(offsets)}\n0000000000 65535 f \n'.encode())
    for offset in offsets[1:]: result.extend(f'{offset:010d} 00000 n \n'.encode())
    result.extend(f'trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n'.encode())
    return bytes(result)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('cli', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    root = args.output.resolve(); root.mkdir(parents=True, exist_ok=False)
    def write(name, data):
        path = root / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(data)
    long_name = '長い帳票名 & ' + 'invoice-' * 20 + '.png'
    for side in ['A', 'B']:
        write(f'small/{side}/.hidden/同一.PNG', png())
        write(f'small/{side}/変更/{long_name}', png(side == 'B'))
        write(f'small/{side}/請求/日本語 空白.pdf', pdf(side == 'B'))
        write(f'small/{side}/破損.png', b'broken' if side == 'A' else png())
        write(f'small/{side}/曖昧.png', png())
        write(f'small/{side}/readme.txt', b'ignored')
    write('small/A/片側.pdf', b'uninspected')
    write('small/B/片側.png', png())
    write('common.yaml', b'dpi: 144\ndiff: {color_threshold: 8}\n')
    write('selected.yaml', b'diff: {max_shift_mm: 0, edge_tolerance: 0}\n')
    write('rules.yaml', "schema_version: 1\nrules:\n- {pattern: '請求/', config: selected.yaml}\n- {pattern: '曖昧', config: selected.yaml}\n- {pattern: '曖昧', config: selected.yaml}\n".encode())
    measurements = []
    def run(name, count=None):
        source = root / name; destination = root / (name + '-result')
        options = ['--config', str(root / 'common.yaml'), '--rules', str(root / 'rules.yaml')] if name == 'small' else ['--profile', 'strict', '--no-html']
        command = ['dotnet', str(args.cli.resolve()), 'compare-dir', str(source / 'A'), str(source / 'B'), '--out', str(destination), '--force', *options]
        samples = []
        for iteration in range(4):
            started = time.perf_counter()
            # macOS time -l の maximum resident set size は bytes。起動とファイル出力も含む。
            result = subprocess.run(['/usr/bin/time', '-l', *command], capture_output=True, text=True)
            elapsed = (time.perf_counter() - started) * 1000
            expected = 2 if name == 'small' else 0
            assert result.returncode == expected, (result.returncode, result.stderr)
            rss = re.search(r'(\d+)\s+maximum resident set size', result.stderr)
            assert rss, result.stderr
            if iteration: samples.append({'elapsed_ms': round(elapsed, 3), 'peak_rss_bytes': int(rss[1])})
        report = json.loads((destination / 'index.json').read_text())
        if name == 'small':
            assert report['summary'] == {'status': 'error', 'total': 7, 'compared': 3, 'same': 1, 'different': 2,
                                         'only_in_a': 1, 'only_in_b': 1, 'error': 2, 'ignored': 2}
        else: assert report['summary']['same'] == count and report['summary']['error'] == 0
        measurements.append({'name': name, 'summary': report['summary'], 'samples': samples,
                             'median_elapsed_ms': statistics.median(x['elapsed_ms'] for x in samples),
                             'median_peak_rss_bytes': statistics.median(x['peak_rss_bytes'] for x in samples)})
    run('small')
    for count in [10, 100, 300]:
        name = f'many-{count}'
        for side in ['A', 'B']:
            for index in range(count): write(f'{name}/{side}/帳票{index:04d}.png', png())
        run(name, count)
    document = {'environment': os.uname().sysname + ' ' + os.uname().machine,
                'method': 'separate CLI process; warmup 1, samples 3; macOS /usr/bin/time -l peak RSS bytes; 64x64 PNG; small includes 216pt PDF at 144dpi',
                'measurements': measurements}
    (root / 'measurement.json').write_text(json.dumps(document, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(document, ensure_ascii=False, indent=2))


if __name__ == '__main__': main()
