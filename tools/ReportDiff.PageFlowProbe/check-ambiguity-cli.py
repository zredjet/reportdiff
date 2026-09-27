#!/usr/bin/env python3
"""境界再検証の追加4方向だけを許可し、固定26方向の旧新CLIと独立C/D証拠を照合する。"""
import hashlib, importlib.util, json, subprocess, sys
from pathlib import Path
import numpy as np
from PIL import Image

root = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1]).resolve(); out.mkdir(parents=True, exist_ok=False)
before = Path(sys.argv[2]).resolve(); current = root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
fixtures = root/'tests/ReportDiff.Tests/Fixtures/page-flow-shared'
diagnosis = root/'out/t3-1c-shared-diagnosis/complete'
experiment = {r['run']: r for r in json.loads((diagnosis/'diagnosis.json').read_text())}
spec = importlib.util.spec_from_file_location('compat', root/'tools/verify-pagemap-compatibility.py')
compat = importlib.util.module_from_spec(spec); spec.loader.exec_module(compat)
metrics = dict(runs=0, processes=0, added=0, unchanged=0, compatible_files=0, cd_images=0, raw_png_pairs=0, structures=0, alternatives=0, same_page_rows=0)
records = []
for path, sha in json.loads((fixtures/'sha256.json').read_text()).items():
    assert hashlib.sha256((fixtures/path).read_bytes()).hexdigest() == sha
for saved in json.loads((fixtures/'expected.json').read_text()):
    name = saved['run']; added = saved['id'] in ['shared-chain', 'shared-and-independent']; results = {}
    paths = [fixtures/saved['id']/(s+'.pdf') for s in (['b', 'a'] if saved['reverse'] else ['a', 'b'])]
    for label, cli, carry in [('before-on', before, True), ('on', current, True), ('before-off', before, False), ('off', current, False)]:
        config = out/(label+'.yaml'); config.write_text(f'rows: {{enabled: true, carry_enabled: {str(carry).lower()}}}\nreport: {{raw_overlay: true}}\n')
        directory = out/(name+'-'+label)
        p = subprocess.run(['dotnet', str(cli), 'compare', *map(str, paths), '--out', str(directory), '--config', str(config), '--quiet'], capture_output=True, text=True)
        assert p.returncode == 1, (name, label, p.stdout, p.stderr)
        results[label] = directory, json.loads((directory/'result.json').read_text()); metrics['processes'] += 1
    olddir, old = results['before-on']; directory, new = results['on']; flow = new['page_flow']; exp = experiment[name]['experiment']
    expected = exp['aggregate'] if added else saved['expected']
    for key in ['difference_count', 'aggregated_difference_count', 'difference_count_complete', 'aggregated_difference_count_complete']:
        assert new['summary'][key] == expected[key], (name, key)
    assert flow['aggregation']['status'] == expected['status']
    if not added:
        a, b = compat.snapshot(olddir), compat.snapshot(directory); assert a == b, (name, '対象外の変更')
        metrics['unchanged'] += 1; metrics['compatible_files'] += len(a)
    else:
        assert flow['status'] == 'applied' and old['page_flow']['status'] == 'skipped'
        assert [p['adoption'] for p in flow['pages']] == exp['adoptions']
        assert [l['id'] for l in flow['links']] == [l['id'] for l in old['page_flow']['links']]
        changed = [l for l in flow['links'] if 'ambiguity' in l]; assert len(changed) == 1
        link = changed[0]; proof = link['ambiguity']; assert link['id'] == 2 and proof['boundary_page'] == 2
        assert proof['coordinate_system'] == 'globally_aligned' and not proof['pixel_equality_proven']
        assert [s['dy'] for s in proof['shifts']] == [100, 200] and all(len(s['support_text']) == 2 for s in proof['shifts'])
        if saved['id'] == 'shared-chain':
            assert proof['selected_dy'] == 200 and len(proof['crossing_rows']) == 2 and not proof['alternatives']
            assert link['image_status'] == 'verified' and link['original_comparisons'] > 0
        else:
            assert proof['selected_dy'] is None and len(proof['alternatives']) == 2 and not proof['crossing_rows']
            assert link['source'] is None and link['target'] is None and link['image_status'] == 'not_performed' and link['original_comparisons'] == 0
            for alt in proof['alternatives']:
                assert alt['source']['image'] is None and alt['target']['image'] is None
                for row in alt['source_rows'] + alt['target_rows']:
                    assert row['row']['page'] == row['counterpart']['page']; metrics['same_page_rows'] += 1
                metrics['alternatives'] += 1
        actual = flow['aggregation']['shared_components']; wanted = exp['aggregate']['shared_components']
        assert len(actual) == len(wanted)
        for a, b in zip(actual, wanted, strict=True):
            for key in ['pages', 'structures', 'movements', 'balance']: assert a[key] == b[key], (name, key)
            assert a['causes'] == [dict(reference=c['reference'], rows=c['rows'], delta_px=c['delta']) for c in b['causes']]
            for l, r in zip(a['links'], b['links'], strict=True):
                for key in ['structures', 'causes', 'rows']: assert l[key] == r[key]
                assert flow['links'][l['link']-1]['source']['page'] == r['link']['source']['page']['page']
        metrics['added'] += 1
    html = (directory/'report.html').read_text()
    refs = [s for g in flow['aggregation']['groups'] for s in g['structures']] + [s for c in flow['aggregation'].get('shared_components', []) for s in c['structures']]
    assert len(refs) == len({(r['page'], r['structural_change_id']) for r in refs})
    for r in refs:
        anchor = f'page-{r["page"]}-structure-{r["structural_change_id"]}'
        assert f'id="{anchor}"' in html and f'href="#{anchor}"' in html
    a, b = [compat.snapshot(results[k][0]) for k in ['before-off', 'off']]; assert a == b; metrics['compatible_files'] += len(a)
    for p, q in zip(new['pages'], results['off'][1]['pages'], strict=True):
        for x, y in [(p['raw_evidence'][s]['image'], q['raw_evidence'][s]['image']) for s in ['a', 'b']] + [(p['raw_evidence']['overlay'], q['raw_evidence']['overlay'])]:
            assert (directory/x).read_bytes() == (results['off'][0]/y).read_bytes(); metrics['raw_png_pairs'] += 1
        if not added: continue
        projection = next(x for x in exp['projections'] if x['page'] == p['page'])
        assert p['raw_pixels'] == projection['raw_pixels'] and len(p['clusters']) == len(projection['content_clusters'])
        assert [s['id'] for s in p['row_alignment']['structural_changes']] == [s['id'] for s in projection['structures']]
        metrics['structures'] += len(projection['structures'])
        for mode, keys in [('C', ['content_a', 'content_b']), ('D', ['a', 'b'])]:
            for side, key in zip(['A', 'B'], keys):
                assert np.array_equal(np.array(Image.open(directory/p['images'][key])), np.array(Image.open(diagnosis/name/f'p{p["page"]}-{mode}-{side}.png')))
                metrics['cd_images'] += 1
    metrics['runs'] += 1; records.append(dict(run=name, added=added, summary=new['summary']))
    print(name, new['summary']['difference_count'], new['summary']['aggregated_difference_count'], flush=True)
    (out/'verification.json').write_text(json.dumps(dict(metrics=metrics, records=records), indent=2)+'\n')
