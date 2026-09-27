#!/usr/bin/env python3
"""本文・変位・配置の支持境界を独立再計算し、固定された事前期待と現在の製品結果を区別する。"""
import collections, json, sys
from pathlib import Path

root = Path(__file__).resolve().parents[2]; folder = Path(sys.argv[1]).resolve()
geometry = {r['id']: r for r in json.loads((folder/'geometry.json').read_text())}
diagnosis = {r['run']: r for r in json.loads((folder/'support.json').read_text())}
runs = json.loads((folder/'shared-causes.json').read_text()); checked = json.loads((folder/'shared-check.json').read_text())
expected = {r['run']: r['decision'] for r in checked['records']}
metrics = dict(runs=0, boundaries=0, gaps=0, adopted=0, range_only=0, negative_rejections=0, original_unmet=0)
product_checks = dict(core_decisions=0, report_components=0, structure_references=0, html_anchors=0)
product_audit = None
if len(sys.argv) > 2:
    audit = json.loads(Path(sys.argv[2]).read_text())
    product_audit = dict(inputs=0, permutations=0, rejections=0, completeness=0, content_preserved=0)
    for got, saved in zip(audit, runs, strict=True):
        assert got['run'] == saved['run'] and got['decision'] == saved['legacy']
        assert got['audit_scope'] == 'paired_components'
        product_audit['inputs'] += 1
        for key, value in got['audit'].items(): product_audit[key] += value
records = []
rk = lambda x: [x['page'], x['structural_change_id']]

def compare_components(got, want, report=None):
    for k in ['status', 'difference_count', 'aggregated_difference_count', 'difference_count_complete', 'aggregated_difference_count_complete']:
        assert got[k] == want[k], (name, k)
    components = [c for c in want.get('components', []) if len(c['causes']) > 1]
    assert len(got.get('shared_components', [])) == len(components), name
    for c, d in zip(got.get('shared_components', []), components, strict=True):
        assert c['pages'] == d['pages'] and c['balance'] == d['balance']
        assert list(map(rk, c['structures'])) == d['structures']
        for a, b in zip(c['causes'], d['causes'], strict=True):
            assert rk(a['reference']) == b['reference'] and a['rows'] == len(b['rows'])
            assert a['delta_px' if report else 'delta'] == b['delta']
        for a, b in zip(c['movements'], d['movements'], strict=True):
            assert rk(a['structure']) == b['structure'] and list(map(rk, a['causes'])) == b['causes'] and a['dy'] == b['dy']
        for a, b in zip(c['links'], d['flows'], strict=True):
            boundary = next(l for l in report['page_flow']['links'] if l['id'] == a['link'])['source']['page'] if report else a['link']['source']['page']['page']
            assert boundary == b['boundary'] and list(map(rk, a['structures'])) == b['structures']
            assert list(map(rk, a['causes'])) == b['causes'] and a['rows'] == len(b['rows'])
        if report:
            html = (folder/name/'cli/report.html').read_text()
            for page, sid in d['structures']:
                actual_page = next(p for p in report['pages'] if p['page'] == page)
                assert any(s['id'] == sid for s in actual_page['row_alignment']['structural_changes'])
                anchor = f'page-{page}-structure-{sid}'
                assert f'id="{anchor}"' in html and f'href="#{anchor}"' in html
                product_checks['structure_references'] += 1; product_checks['html_anchors'] += 1
            product_checks['report_components'] += 1
for side in ['a', 'b']:
    old = root/'tests/ReportDiff.Tests/Fixtures/page-flow-shared/same-page-two'/(side+'.pdf')
    assert (folder/'same-page-two'/(side+'.pdf')).read_bytes() == old.read_bytes()
for r in runs:
    name = r['run']; g = geometry[r['id']]; d = diagnosis[name]; rows = d['rows']
    by_side = {side: sorted([row for row in rows if row['page']['side'] == side], key=lambda row: (row['page']['page'], row['top'])) for side in [0, 1]}
    counts = {side: collections.Counter(row['text'] for row in rr) for side, rr in by_side.items()}
    lookup = {side: {row['text']: row for row in rr} for side, rr in by_side.items()}
    for boundary in d['inferred']['boundaries']:
        side = boundary['side']; page = boundary['page']; shifts = collections.defaultdict(list)
        for row in by_side[side]:
            other = lookup[1-side].get(row['text'])
            if row['page']['page'] == page and other and other['page']['page'] == page and round(other['left']-row['left']) == 0:
                assert counts[side][row['text']] == counts[1-side][row['text']] == 1
                shifts[round(other['baseline']-row['baseline'])].append(row['text'])
        assert {s['dy']: s['text'] for s in boundary['shifts']} == dict(shifts), (name, side, page)
        for s in boundary['shifts']: assert s['eligible'] == (0 < s['dy'] <= 236 and len(s['text']) >= 2)
        crossed = [row for row in by_side[side] if row['page']['page'] == page and row['text'] in lookup[1-side] and lookup[1-side][row['text']]['page']['page'] == page+1]
        assert crossed == boundary['crossing']; metrics['boundaries'] += 1
    for gap in d['gaps']:
        side = gap['page']['side']; page = gap['page']['page']
        current = [row for row in by_side[side] if row['page']['page'] == page]
        other = {row['text'] for row in by_side[1-side] if row['page']['page'] == page}
        assert gap['text'] == [row['text'] for row in current if gap['top'] <= row['top'] < gap['top']+gap['height']]
        assert gap['before'] == sum(row['text'] in other and row['top'] < gap['top'] for row in current)
        assert gap['after'] == sum(row['text'] in other and row['top'] >= gap['top']+gap['height'] for row in current)
        metrics['gaps'] += 1
    report = json.loads((folder/name/'cli/result.json').read_text()); applied = report['page_flow']['status'] == 'applied'
    compare_components(r['legacy'], expected[name]); product_checks['core_decisions'] += 1
    assert applied == (g['grouped'] and not g['existing_unmet']), (name, report['page_flow']['reasons'])
    if applied:
        compare_components(report['page_flow']['aggregation'], expected[name], report)
        assert report['summary']['difference_count'] == (8 if g['mutation'] == 'paired' else 7)
        assert report['summary']['aggregated_difference_count'] == (3 if g['mutation'] == 'paired' else 2)
        assert sum(page['raw_pixels'] for page in report['pages']) == (1614 if g['mutation'] == 'paired' else 0)
        metrics['adopted'] += 1
    elif g['existing_unmet']:
        assert r['hypothesis']['grouped'] and report['summary']['difference_count'] == 10
        metrics['original_unmet'] += 1
    else: metrics['negative_rejections'] += 1
    if not applied:
        assert not report['page_flow']['aggregation'].get('shared_components')
        assert not report['page_flow']['aggregation']['groups']
    if r['gate']['ready'] and not applied:
        assert r['id'] in ['between-short', 'faint-support']
        rejected = [a for a in r['adoptions'] if not a['accepted']]
        assert len(rejected) == 1 and rejected[0]['detail'] == ('support_bands' if r['id'] == 'between-short' else 'support_ink')
        assert report['summary']['difference_count'] == (14 if r['id'] == 'between-short' else 11)
        metrics['range_only'] += 1
    if g['mutation'] == 'none' and g['first'] >= 2 and g['second']-g['first'] >= 2:
        boundary = next(b for b in d['inferred']['boundaries'] if b['side'] == (1 if r['reverse'] else 0))
        support = next((len(s['text']) for s in boundary['shifts'] if s['dy'] == 200), 0)
        assert support == g['capacity']-g['second']-2
        last_insert = next(gap for gap in d['gaps'] if gap['text'] == [f"ROW ADDED {chr(65+g['second'])} A"])
        assert last_insert['after'] == support
    metrics['runs'] += 1
    records.append(dict(run=name,capacity=g['capacity'],first=g['first'],second=g['second'],mutation=g['mutation'],
        hypothesis_met=not g['existing_unmet'],range_ready=r['gate']['ready'],applied=applied,
        candidate_count=r['legacy']['difference_count'],product=report['summary'],adoptions=r['adoptions']))
assert metrics == dict(runs=30,boundaries=52,gaps=120,adopted=12,range_only=4,negative_rejections=16,original_unmet=2), metrics
assert checked['metrics']['unexpected_negative_acceptances'] == 0 and checked['metrics']['unmet_positive_hypotheses'] == 2
assert product_checks == dict(core_decisions=30,report_components=12,structure_references=84,html_anchors=84), product_checks
(folder/'support-check.json').write_text(json.dumps(dict(metrics=metrics,product_checks=product_checks,product_audit=product_audit,records=records),indent=2)+'\n')
print(metrics, product_checks, product_audit)
