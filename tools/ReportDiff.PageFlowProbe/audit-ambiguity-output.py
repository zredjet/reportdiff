#!/usr/bin/env python3
"""保存した元画像からC/Dと元帯を独立再構成し、新しい境界証明のO座標を固定本文と照合する。"""
import json, sys
from pathlib import Path
import numpy as np
from PIL import Image

root = Path(__file__).resolve().parents[2]; out = Path(sys.argv[1]).resolve()
cases = {r['id']: r for r in json.loads((root/'tests/ReportDiff.Tests/Fixtures/page-flow-ambiguity/cases.json').read_text())}
diagnosis = {(r['id'], r['reverse']): r for r in json.loads((root/'out/t3-1c-shared-diagnosis/complete/diagnosis.json').read_text())}
records = []; metrics = dict(runs=0, pages=0, surface_images=0, structures=0, band_images=0, boundary_rows=0)
def read(directory, path): return np.array(Image.open(directory/path).convert('RGB'))
def render(raw, segments, height, side, shift=0, start='canvas_start'):
    result = np.full((height, raw.shape[1], 3), 255, dtype=np.uint8)
    for seg in segments:
        if seg[side+'_start'] is None: continue
        src = seg[side+'_start'] - shift; dst = seg[start]; n = seg['length']; lo = max(0, -src); hi = min(n, len(raw)-src)
        if hi > lo: result[dst+lo:dst+hi] = raw[src+lo:src+hi]
    return result
for record in json.loads((out/'combinations/verification.json').read_text()):
    if not record['applied']: continue
    name = record['run']; directory = out/'combinations'/(name+'-True')
    report = json.loads((directory/'result.json').read_text()); flow = report['page_flow']
    case = next(c for key, c in cases.items() if name.startswith(key+'-'))
    reverse = '-ba-' in name; saved = diagnosis[case['base'], reverse]
    source_rows = {(('a' if r['page']['side'] == 0 else 'b'), r['text']): r for r in saved['rows']}
    for page in report['pages']:
        alignment = page['row_alignment']; metrics['pages'] += 1
        for side in ['a', 'b']:
            raw = read(directory, page['raw_evidence'][side]['image']); dy = (page['global_shift_px'] or {}).get('dy', 0) if side == 'b' else 0
            for mode, segments, height, start, path in [
                ('D', alignment['segments'], page['size_px']['h'], 'canvas_start', page['images'][side]),
                ('C', alignment['content_canvas']['pieces'], alignment['content_canvas']['size_px']['h'], 'content_start', page['images']['content_'+side])]:
                actual = render(raw, segments, height, side, dy, start)
                assert np.array_equal(actual, read(directory, path)), (name, page['page'], mode, side)
                if mode == 'D': assert np.count_nonzero(np.any(raw != 255, axis=2)) == np.count_nonzero(np.any(actual != 255, axis=2))
                metrics['surface_images'] += 1
        for change in alignment['structural_changes']:
            metrics['structures'] += 1
            for side in ['a', 'b']:
                source = change['source_'+side]
                if source is None: continue
                dy = (page['global_shift_px'] or {}).get('dy', 0) if side == 'b' else 0
                top = change['bbox_px']['y']; bottom = top + change['bbox_px']['h']; parts = []
                for seg in alignment['segments']:
                    lo = max(top, seg['canvas_start']); hi = min(bottom, seg['canvas_start']+seg['length'])
                    if hi > lo and seg[side+'_start'] is not None: parts.append((seg[side+'_start']+lo-seg['canvas_start']-dy, hi-lo))
                assert [(p['y'], p['h']) for p in source['parts_px']] == parts
    for link in flow['links']:
        bands = []
        for endpoint in [link['source'], link['target']]:
            if endpoint is None: continue
            page = next(p for p in report['pages'] if p['page'] == endpoint['page'])
            raw = read(directory, page['raw_evidence'][endpoint['side']]['image']); box = endpoint['bounds_px']
            band = raw[box['y']:box['y']+box['h'], box['x']:box['x']+box['w']]; bands.append(band)
            assert np.array_equal(band, read(directory, endpoint['image'])); metrics['band_images'] += 1
        if link['image_status'] == 'verified': assert np.array_equal(*bands)
        proof = link.get('ambiguity')
        if not proof: continue
        rows = proof['crossing_rows'] + [r for alt in proof['alternatives'] for r in alt['source_rows']+alt['target_rows']]
        for row in rows:
            for endpoint in [row['row'], row['counterpart']]:
                saved_row = source_rows[endpoint['side'], row['text']]
                shift = 0
                if case['global_shifts']:
                    shift = 512 + (case['global_shifts'][endpoint['page']-1] if (endpoint['side']=='a') == reverse else 0)
                assert endpoint['page'] == saved_row['page']['page'] and endpoint['bounds_px']['y'] == saved_row['top'] + shift
                assert endpoint['bounds_px']['h'] == saved_row['height']
            metrics['boundary_rows'] += 1
    metrics['runs'] += 1; records.append(name)
(out/'combinations/audit.json').write_text(json.dumps(dict(metrics=metrics, records=records), indent=2)+'\n')
print(metrics)
