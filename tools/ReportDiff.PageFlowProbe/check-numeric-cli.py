#!/usr/bin/env python3
"""数値行の固定独立証拠を製品CLIへ照合する。未採用候補のCの件数は製品件数と区別する。"""
import hashlib, importlib.util, json, subprocess, sys
from pathlib import Path
import numpy as np
from PIL import Image

root = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1]).resolve(); out.mkdir(parents=True, exist_ok=False)
baseline = Path(sys.argv[2]).resolve()
cli = root / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
evidence = root / 'out/t3-1c-numeric/final-verified'
fixtures = root / 'tests/ReportDiff.Tests/Fixtures/page-flow-numeric'
spec = importlib.util.spec_from_file_location('compat', root / 'tools/verify-pagemap-compatibility.py')
compat = importlib.util.module_from_spec(spec); spec.loader.exec_module(compat)
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
read = lambda p: np.array(Image.open(p).convert('RGB'))
metrics = dict(runs=0, processes=0, adopted=0, cd_images=0, raw_png_pairs=0, compatible_files=0, numeric_proofs=0, content_clusters=0)
records = []
for path, sha in json.loads((fixtures/'sha256.json').read_text()).items(): assert digest(fixtures/path) == sha
for r in json.loads((evidence/'numeric.json').read_text()):
    name = r['run']; results = {}
    paths = [fixtures/r['id']/(s+'.pdf') for s in (['b','a'] if r['reverse'] else ['a','b'])]
    for label, exe, enabled in [('before-off',baseline,False),('off',cli,False),('on',cli,True)]:
        config = out/(label+'.yaml'); config.write_text(f'rows: {{enabled: true, carry_enabled: {str(enabled).lower()}}}\nreport: {{raw_overlay: true}}\n')
        folder = out/(name+'-'+label)
        result = subprocess.run(['dotnet',str(exe),'compare',*map(str,paths),'--config',str(config),'--out',str(folder),'--quiet'], capture_output=True, text=True)
        assert result.returncode == 1, (name,label,result.stdout,result.stderr)
        results[label] = (folder,json.loads((folder/'result.json').read_text())); metrics['processes'] += 1
    old_dir, old = results['off']; new_dir, new = results['on']; flow = new['page_flow']; adopted = r['input']['gate_ready']
    snapshots = [compat.snapshot(results[label][0]) for label in ['before-off','off']]
    assert snapshots[0] == snapshots[1], (name,'無効時の全出力'); metrics['compatible_files'] += len(snapshots[0])
    assert (flow['status'] == 'applied') == adopted and flow['range_ready'] == r['mapping_gate']['ready'], (name,flow)
    assert new['summary']['difference_count'] == (r['candidate']['difference_count'] if adopted else old['summary']['difference_count'])
    assert new['summary']['aggregated_difference_count'] == (r['candidate']['aggregated_difference_count'] if adopted else old['summary']['difference_count'])
    assert (flow['aggregation']['status'] == 'grouped') == adopted
    proofs = flow.get('numeric_matches',[])
    assert len(proofs) == len(r['numeric']['pairs'])
    for proof, pair in zip(proofs,r['numeric']['pairs'],strict=True):
        assert proof['status'] == ('applied' if adopted else 'not_applied')
        assert not proof['used_as_exact_support'] and not proof['pixel_equality_proven']
        for actual, expected in [(proof,pair)] + list(zip(proof['anchors'],pair['anchors'],strict=True)):
            for side in ['a','b']:
                row = actual[side]; expected_row = expected[side]; endpoint = row['original']
                assert row['text'] == expected_row['text'] and endpoint['side'] == side and endpoint['page'] == expected_row['page']
                assert endpoint['coordinate_system'] == 'original_top_left'
                assert endpoint['bounds_px']['y'] == expected_row['top'] and endpoint['bounds_px']['h'] == expected_row['height']
        assert proof['changed_token_indices'] == [i for i,(a,b) in enumerate(zip(pair['a']['text'].split(),pair['b']['text'].split(),strict=True)) if a != b]
        metrics['numeric_proofs'] += 1
    if r['mapping_gate']['ready']:
        assert [p['adoption'] for p in flow['pages']] == r['adoptions'], (name,'元本文だけの支持')
    if r['id'] == 'weak-actual-support':
        assert not adopted and flow['pages'][1]['adoption']['detail'] == 'support_ink' and not flow['aggregation']['groups']
    if adopted:
        assert len(flow['aggregation']['groups']) == len(r['candidate']['groups'])
        for actual, expected in zip(flow['aggregation']['groups'], r['candidate']['groups'], strict=True):
            for k in ['cause','structures','balance']: assert actual[k] == expected[k]
            assert all(flow['links'][i-1]['status'] == 'carried' for i in actual['links'])
    for page, before in zip(new['pages'],old['pages'],strict=True):
        for x,y in [(page['raw_evidence'][s]['image'],before['raw_evidence'][s]['image']) for s in ['a','b']] + [(page['raw_evidence']['overlay'],before['raw_evidence']['overlay'])]:
            assert digest(new_dir/x) == digest(old_dir/y); metrics['raw_png_pairs'] += 1
        if not adopted:
            assert page == before, (name,'見送り時の全ページ結果'); continue
        projection = next(p for p in r['projections'] if p['page'] == page['page'])
        assert page['raw_pixels'] == projection['raw_pixels'] and len(page['clusters']) == len(projection['content_clusters'])
        metrics['content_clusters'] += len(page['clusters'])
        for mode, keys in [('C',['content_a','content_b']),('D',['a','b'])]:
            for side,key in zip(['A','B'],keys):
                assert np.array_equal(read(new_dir/page['images'][key]),read(evidence/name/f"p{page['page']}-{mode}-{side}.png")), (name,mode,side)
                metrics['cd_images'] += 1
        for actual, expected in zip(page['row_alignment']['structural_changes'],projection['structures'],strict=True):
            assert actual['id'] == expected['id'] and actual['kind'] == expected['kind']
    html = (new_dir/'report.html').read_text()
    if proofs: assert '数値変更を含む行対応' in html and '変更行を完全一致の支持には数えていません' in html
    metrics['runs'] += 1; metrics['adopted'] += adopted
    records.append(dict(run=name, status=flow['status'], summary=new['summary'], numeric_matches=proofs))
    print(name,flow['status'],new['summary']['difference_count'],new['summary']['aggregated_difference_count'],flush=True)
    (out/'verification.json').write_text(json.dumps(dict(metrics=metrics,records=records),ensure_ascii=False,indent=2)+'\n')
