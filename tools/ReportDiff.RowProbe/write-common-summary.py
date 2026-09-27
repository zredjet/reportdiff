"""共同比較面の全照合と採用候補の境界を、再現可能な記録として保存する。"""
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parents[2]
folder = root / 'out/t3-1b-probe/results/common-surface'
def read(name):
    return json.loads((folder / (name + '.json')).read_text())

jobs, python, csharp = read('jobs'), read('python'), read('csharp')
assert len(jobs) == len(python) == len(csharp) == 306
assert {x['id'] for x in jobs} == {x['id'] for x in python} == {x['id'] for x in csharp}
assert read('parity') == dict(comparisons=306, equal=True)
assert read('swap') == dict(conditions=306, common_inputs_and_maps_equal=True)
assert all(x['originals_unchanged'] and x['swap_map_equal'] for x in csharp)
retained = [x for x in python if x['retain_white']]
compacted = [x for x in python if not x['retain_white']]
assert len(retained) == 174 and all(x['baseline_mask_equal'] and x['baseline_inputs_equal'] for x in retained)
assert not any(x['detection_lost'] for x in retained)
assert len(compacted) == 132 and sum(x['detection_lost'] for x in compacted) == 12
assert len(read('guard')['accepted']) == 174
assert read('guard')['close_opposed_seams']['rejected']
assert read('rejection')['rejected']
assert read('direction')['pairs'] == 66 and read('direction')['existing_core_directional_mask_pairs'] == 6
cs = {x['id']: x for x in csharp}
cases = []
for x in python:
    candidate = x['candidate']
    cases.append(dict(id=x['id'], retain_white=x['retain_white'], baseline=x['baseline'], candidate=candidate,
                      baseline_inputs_equal=x['baseline_inputs_equal'], baseline_mask_equal=x['baseline_mask_equal'],
                      detection_lost=x['detection_lost'], csharp_equal=True,
                      source_a_sha256=cs[x['id']]['source_a_sha256'], source_b_sha256=cs[x['id']]['source_b_sha256'],
                      raw_sha256=hashlib.sha256((folder / (x['id'] + '-raw.png')).read_bytes()).hexdigest()))
sources = [root / 'tools/ReportDiff.RowProbe' / name for name in
           ('common_surface.py', 'CommonSurfaceProbe.cs', 'Program.cs', 'mapped_features.py', 'write-common-summary.py', 'ReportDiff.RowProbe.csproj')]
sources += [root / name for name in ('reference/prototype.py', 'reference/golden/expected.json',
            'src/ReportDiff.Core/PageComparer.cs', 'src/ReportDiff.Core/PageMap.cs', 'src/ReportDiff.Core/TolerantDifference.cs',
            'src/ReportDiff.Core/ComparisonFeatures.cs', 'src/ReportDiff.Report/RawOverlay.cs')]
data = dict(date='2026-09-22', baseline_commit='f77ac1c72a0754373041273f9f36b49e312379b7', task='T3-1b',
            decision='propose_common_surface_preserving_white_space_pending_spec_approval',
            product_changed=False, reference_changed=False, golden_expectations_changed=False, formal_acceptance_complete=False,
            release_build=dict(warnings=0, errors=0), full_1330_tests_rerun=False,
            retained_white_conditions=174, retained_white_baseline_input_and_mask_equal=174,
            compact_all_conditions=132, compact_all_detection_loss_conditions=12,
            csharp_python_comparisons=306, swap_map_conditions_per_implementation=306,
            case_families=dict(previous_pdf=78, fixed_footer_images=24, mixed_insert_delete_images=12,
                               double_insertion_images=12, crossing_cluster_images=6, identity_golden=42),
            guard=read('guard'), replacement_rejection=read('rejection'), direction=read('direction'),
            crossing_cluster=next(x for x in retained if x['id'] == 'crossing-normal-forward-retain'),
            cases=cases,
            source_sha256={str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
            output_json_sha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.glob('*.json'))})
destination = root / 'docs/verification/t3-1b-common-surface.json'
destination.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n')
print(destination.relative_to(root))
