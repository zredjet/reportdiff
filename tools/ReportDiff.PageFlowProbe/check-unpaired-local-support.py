#!/usr/bin/env python3
"""前側1行の診断を固定座標表・参照比較器・前段の対照26方向へ照合する。製品採用の証明ではない。"""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
folder, previous = (Path(p).resolve() for p in sys.argv[1:3])
spec = importlib.util.spec_from_file_location('local_support_reference', ROOT / 'reference/prototype.py')
reference = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = reference
spec.loader.exec_module(reference)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path, gray=False):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), 0 if gray else 1)


def render(original, table, column):
    canvas = np.full((sum(row[1] for row in table), 999, 3), 255, np.uint8)
    coverage = np.zeros(1250, np.int32)
    for y, height, start_a, start_b in table:
        start = (start_a, start_b)[column]
        if start is not None:
            canvas[y:y + height] = original[start:start + height]
            coverage[start:start + height] += 1
    return canvas, coverage


records = json.loads((folder / 'diagnosis.json').read_text())
old = {r['run']: r for r in json.loads((previous / 'diagnosis.json').read_text())}
supports = {r['run']: r for r in json.loads((folder / 'local-support.json').read_text())}
assert len(records) == len(old) == len(supports) == 28
assert len({r['run'] for r in records}) == 28
assert json.loads((folder / 'fixtures.json').read_text()) == json.loads((previous / 'fixtures.json').read_text())
metrics = dict(runs=28, unchanged_controls=0, product_plans_unchanged=0, unchanged_control_images=0,
               pdfs=0, experimental_gaps=0, unmet_positive=0, reference_pages=0, surface_images=0,
               mask_images=0, exact_bands=0, product_test_fixture_files_unchanged=0)
for file in sorted((folder / 'inputs').rglob('*.pdf')):
    relative = file.relative_to(folder)
    assert file.read_bytes() == (previous / relative).read_bytes()
    fixed = ROOT / 'tests/ReportDiff.Tests/Fixtures/page-flow-terminal-shared' / file.parent.name / file.name
    assert file.read_bytes() == fixed.read_bytes()
    metrics['pdfs'] += 1

# 元ケースの独立表。順に C の対応本文／固定部と D の元行全被覆。
# 各行は（出力y, 高さ, 短い側の元y, 長い側の元y）。A/B反転は最後の2列だけ入れ替える。
tables = {
    1: {
        'C': [(0, 500, 0, 0), (500, 300, 500, 600), (800, 350, 900, 900)],
        'D': [(0, 500, 0, 0), (500, 100, None, 500), (600, 300, 500, 600),
              (900, 100, 800, None), (1000, 350, 900, 900)]},
    2: {
        'C': [(0, 300, 0, 0), (300, 100, 300, 400), (400, 300, 400, 600), (700, 350, 900, 900)],
        'D': [(0, 300, 0, 0), (300, 100, None, 300), (400, 100, 300, 400),
              (500, 100, None, 500), (600, 300, 400, 600), (900, 200, 700, None), (1100, 350, 900, 900)]}}
findings = []
for record in records:
    name = record['run']
    prior, support = old[name], supports[name]
    assert record['product'] == prior['product'], (name, '製品計画')
    metrics['product_plans_unchanged'] += 1
    experimental = [r for p in support['maps'] for r in (p['removed'] or [])
                    if r['proof'] == 'experimental_single_before_cause']
    if record['id'] != 'original':
        assert not experimental
        assert record == prior, (name, '対照の診断全フィールド')
        current_images = {p.name: digest(p) for p in (folder / name).glob('*.png')}
        prior_images = {p.name: digest(p) for p in (previous / name).glob('*.png')}
        assert current_images == prior_images, (name, '対照の全画像')
        metrics['unchanged_control_images'] += len(current_images)
        metrics['unchanged_controls'] += 1
        continue
    assert record['expected']['grouped'] and not record['input']['gate_ready']
    assert not prior['gate']['ready'] and record['gate']['ready']
    for field in ('inferred', 'links', 'gaps', 'coverage', 'layouts', 'original_comparisons'):
        assert record[field] == prior[field], (name, field)
    assert len(experimental) == 1 and support['minimum_support_bands'] == 2
    target = 0 if record['reverse'] else 1
    assert experimental[0]['band'] == dict(page=dict(side=target, page=2), top=500, height=100, bottom=600)
    metrics['experimental_gaps'] += 1
    adoptions = {a['page']: a['adoption'] for a in record['adoptions']}
    assert adoptions[1]['accepted'] and not adoptions[2]['accepted']
    assert (adoptions[2]['reason'], adoptions[2]['detail']) == ('insufficient_support', 'support_bands')
    # 本文の同一ページ対応から独立再計算。送り元が別ページの FFFF はここに含めない。
    rows = record['input']['rows']
    matches = []
    for a in (r for r in rows if r['side'] == 0 and r['page'] == 2):
        for b in (r for r in rows if r['side'] == 1 and r['page'] == 2 and r['text'] == a['text']):
            matches.append((a['text'], a['start'] - b['start']))
    sign = 1 if record['reverse'] else -1
    assert matches == [('ITEM GGGG', sign * 100), ('ITEM HHHH', sign * 200),
                       ('ITEM IIII', sign * 200), ('ITEM JJJJ', sign * 200)]
    assert [(r['text'], round(r['dy'])) for r in support['same_page_body_matches'] if r['page'] == 2] == matches
    for link in record['links']:
        assert link['status'] == 'band_verified'
        bands = []
        for band in (link['source'], link['target']):
            image = read(folder / name / f"p{band['page']['page']}-O-{'AB'[band['page']['side']]}.png")
            bands.append(image[band['top']:band['bottom']])
        assert np.array_equal(*bands)
        metrics['exact_bands'] += 1
    for projection in record['projections']:
        page = projection['page']
        content = []
        for column, side in enumerate('AB'):
            original = read(folder / name / f'p{page}-O-{side}.png')
            for mode in 'CD':
                expected, coverage = render(original, tables[page][mode], 1 - column if record['reverse'] else column)
                assert np.array_equal(expected, read(folder / name / f'p{page}-{mode}-{side}.png')), (name, page, mode, side)
                if mode == 'D':
                    assert np.all(coverage == 1), (name, page, side, '元行を一度ずつ保持')
                else:
                    assert np.all(coverage <= 1)
                    content.append(expected)
                metrics['surface_images'] += 1
        compared = reference.compare_page(*content, reference.Params())
        assert compared.raw_pixels == projection['raw_pixels'] == 0 and not projection['clusters']
        assert np.array_equal(compared.raw_mask, read(folder / name / f'p{page}-C-raw.png', True) > 0)
        assert not np.any(read(folder / name / f'p{page}-D-raw.png', True))
        metrics['reference_pages'] += 1
        metrics['mask_images'] += 2
    findings.append(dict(run=name, cause_before=1, cause_after=3, segment_support_counts=[1, 3],
                         range_ready=True, page1_accepted=True, page2_adoption=adoptions[2], document_adopted=False))
    metrics['unmet_positive'] += 1

snapshot = json.loads((folder.parent / 'source-before.json').read_text())
assert len(snapshot) == 544
for path, sha in snapshot.items():
    assert digest(ROOT / path) == sha, path
    metrics['product_test_fixture_files_unchanged'] += 1
assert (metrics['unchanged_controls'], metrics['pdfs'], metrics['unmet_positive']) == (26, 28, 2)
evidence = {str(p.relative_to(ROOT)): digest(p) for p in sorted(folder.rglob('*')) if p.is_file()}
result = dict(scope='diagnostic_only_not_product_adoption', metrics=metrics, findings=findings,
              product_snapshot_sha256=digest(folder.parent / 'source-before.json'), evidence_sha256=evidence)
destination = folder.parent / 'verification.json'
assert not destination.exists(), '既存検証を上書きしない'
destination.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n')
print(json.dumps(metrics, indent=2))
