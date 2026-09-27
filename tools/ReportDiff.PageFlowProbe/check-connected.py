#!/usr/bin/env python3
"""固定76実行を製品CLIへ渡し、独立結果・基準経路・raw evidenceとの一致を確認する。"""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
output = Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=False)
base = root / 'out/t3-1c-probe'
cli = root / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
sets = [('fixed', 'type3-final', 'gate-fixed', 'aggregate-fixed'),
        ('additional', 'candidate-fixtures-final', 'gate-additional', 'aggregate-additional'),
        ('skia', 'skia-fixtures', 'skia-candidates', 'aggregate-skia'),
        ('skia-local', 'skia-local-fixtures', 'skia-local-candidates', 'aggregate-skia-local'),
        ('causal', 'causal-fixtures-final', 'causal-candidates', 'aggregate-causal')]
configs = {}
for enabled in [False, True]:
    config = output / f'carry-{str(enabled).lower()}.yaml'
    config.write_text(f'rows: {{enabled: true, carry_enabled: {str(enabled).lower()}}}\nreport: {{raw_overlay: true}}\n')
    configs[enabled] = config
hashfile = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
records = []
for label, inputs, candidates, aggregation in sets:
    expected_aggregation = {r['run']: r for r in json.loads((base/aggregation/'aggregation.json').read_text())['records']}
    for saved in json.loads((base/candidates/'candidates.json').read_text()):
        name, id = saved['folder'], saved['id']
        expected = saved['result']
        paths = [base/inputs/id/f'{side}.pdf' for side in (['b', 'a'] if saved['reverse'] else ['a', 'b'])]
        reports = {}
        for enabled in [False, True]:
            directory = output / f'{label}-{name}-{str(enabled).lower()}'
            args = ['dotnet', str(cli), 'compare', *map(str, paths), '--out', str(directory), '--config', str(configs[enabled]), '--quiet']
            if 'selected' in saved: args += ['--pages', ','.join(map(str, saved['selected']))]
            result = subprocess.run(args, cwd=root, capture_output=True, text=True)
            assert result.returncode == 1, (name, enabled, result.returncode, result.stdout, result.stderr)
            report = json.loads((directory/'result.json').read_text()); reports[enabled] = (directory, report)
            assert report['summary']['status'] == 'different'
            if not enabled:
                assert report.get('page_flow') is None and report['summary']['aggregated_difference_count'] is None
                for page in report['pages']:
                    old = next(p for p in expected['pages'] if p['page'] == page['page'])
                    if 'baseline' not in old: continue
                    assert (page['raw_pixels'], page['difference_count'], len(page['clusters']), page['status']) == (
                        old['baseline']['raw_pixels'], old['baseline']['difference_count'], old['baseline']['clusters'], old['baseline']['status']), (name, 'baseline', page['page'])
        old_dir, old = reports[False]; new_dir, new = reports[True]
        flow = new['page_flow']; oracle = expected_aggregation[name]['decision']
        # 成立済み固定入力の採用期待を下げない。新しい採用条件で失敗したら停止して理由を調べる。
        assert (flow['status'] == 'applied') == expected['gate']['ready'], (name, flow['status'], flow['reasons'], flow['pages'])
        for key in ['difference_count', 'aggregated_difference_count', 'aggregated_difference_count_complete']:
            assert new['summary'][key] == oracle[key], (name, key, new['summary'][key], oracle[key])
        assert flow['aggregation']['status'] == oracle['status'], (name, 'grouping')
        assert len(flow['links']) == len(expected['links'])
        for page in new['pages']:
            before = next(p for p in old['pages'] if p['page'] == page['page'])
            for source in ['a', 'b']:
                assert hashfile(new_dir/page['raw_evidence'][source]['image']) == hashfile(old_dir/before['raw_evidence'][source]['image']), (name, source, 'raw')
            assert hashfile(new_dir/page['raw_evidence']['overlay']) == hashfile(old_dir/before['raw_evidence']['overlay']), (name, 'overlay')
            if flow['status'] != 'applied':
                assert page == before, (name, page['page'], 'fallback_page_changed')
        for link in flow['links']:
            for endpoint in [link['source'], link['target']]:
                if endpoint is None: continue
                assert endpoint['coordinate_system'] == 'original_top_left'
                assert (new_dir/endpoint['image']).is_file()
                for reference in endpoint['structures']:
                    page = next(p for p in new['pages'] if p['page'] == reference['page'])
                    assert any(s['id'] == reference['structural_change_id'] for s in page['row_alignment']['structural_changes'])
        html = (new_dir/'report.html').read_text()
        assert 'ページ送りと文書集約' in html and 'ページ別内訳' in html
        records.append(dict(dataset=label, run=name, status=flow['status'], grouping=flow['aggregation']['status'],
                            difference_count=new['summary']['difference_count'], aggregated=new['summary']['aggregated_difference_count'],
                            complete=new['summary']['aggregated_difference_count_complete'], links=len(flow['links']), raw_evidence_equal=True,
                            result_sha256=hashfile(new_dir/'result.json'), html_sha256=hashfile(new_dir/'report.html')))
        print(f'{label}/{name}: {flow["status"]} {new["summary"]["difference_count"]}→{new["summary"]["aggregated_difference_count"]}', flush=True)
(output/'connected.json').write_text(json.dumps(records, ensure_ascii=False, indent=2)+'\n')
print(f'{len(records)}実行のCLI・集約・基準経路・raw evidenceが一致', flush=True)
