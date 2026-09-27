"""全試行の完了を確かめ、検討結果と再現ファイルのハッシュを記録する。"""
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parents[2]
output = root / 'out/t3-1b-probe/results/mapped-features'
def read(name):
    return json.loads((output / (name + '.json')).read_text())

previous, pdf, parity = read('pdf'), read('new-pdf-csharp'), read('csharp')
images, context = read('tone'), read('context')
identity_cs, identity_py = read('identity-csharp'), read('identity')
assert len(previous) == len(identity_cs) == len(identity_py) == len(images) == 42
assert len(pdf) == len(parity) == 36 and len(context) == 180
assert all(c['baseline_mask_equal'] for c in previous)
assert all(c['product_equal'] for c in identity_cs)
assert all(c['reference_equal'] for c in identity_py)
assert all(c['csharp_equal'] for c in parity)
assert not any(c['false_detection'] for c in context)
# これは反例が再現したという検査。検出必須の期待値を「見逃してよい」へ変更しない。
counterexample = next(c for c in pdf if c['id'] == 'gray-tone-normal-forward')
assert (counterexample['baseline']['RawPixels'], len(counterexample['baseline']['clusters'])) == (4, 1)
assert (counterexample['candidate']['RawPixels'], len(counterexample['candidate']['clusters'])) == (2, 0)
source_files = sorted(p for p in (root / 'tools/ReportDiff.RowProbe').iterdir() if p.suffix in ('.cs', '.py', '.csproj'))
source_files += [root / p for p in ('reference/prototype.py', 'reference/golden/expected.json',
                                   'src/ReportDiff.Core/TolerantDifference.cs', 'src/ReportDiff.Core/ComparisonFeatures.cs',
                                   'src/ReportDiff.Core/PageComparer.cs', 'src/ReportDiff.Core/PageMap.cs')]
data = dict(date='2026-09-22', baseline_commit='f77ac1c72a0754373041273f9f36b49e312379b7', task='T3-1b',
            candidate='map_original_features_then_search_on_display_canvas',
            decision='rejected_detectable_tone_change_lost_and_swap_results_differ',
            product_code_changed=False, reference_formula_changed=False, golden_expectations_changed=False,
            formal_acceptance_complete=False, release_build=dict(warnings=0, errors=0),
            full_product_test_suite_rerun=False, python_opencv='4.13.0',
            previous_pdf_conditions=42, new_pdf_conditions=36, csharp_python_pdf_comparisons=72,
            identity_csharp_product_conditions=42, identity_python_reference_conditions=42,
            tone_image_conditions=42, white_boundary_image_conditions=180,
            new_pdf_detection_loss_conditions=sum(c['detection_lost'] for c in pdf),
            tone_image_detection_loss_conditions=sum(c['detection_lost'] for c in images),
            originals_display_raw_evidence_unchanged_in_pdf_probe=True,
            previous_pdf_results=previous, new_pdf_results=pdf,
            csharp_python_equal_ids=[c['id'] for c in parity],
            identity_csharp_results=identity_cs, identity_python_equal_ids=[c['id'] for c in identity_py],
            tone_image_results=images, white_boundary_false_detections=sum(c['false_detection'] for c in context),
            causes=read('causes'),
            source_sha256={str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in source_files},
            output_sha256={str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.glob('*.json'))})
path = root / 'docs/verification/t3-1b-mapped-features.json'
path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n')
print(path.relative_to(root))
