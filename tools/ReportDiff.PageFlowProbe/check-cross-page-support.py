#!/usr/bin/env python3
"""跨ページ支持の証拠、元画素の支持評価、共通面の拒否と既存結果を別々に照合する。"""
import collections, difflib, importlib.util, json, math, sys
from pathlib import Path
import cv2
import numpy as np
from shared_cause_model import evaluate

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
prior = root/'out/t3-1c-same-page-support/complete'
old = {r['run']: r for r in json.loads((prior/'shared-causes.json').read_text())}
spec = importlib.util.spec_from_file_location('cross_reference', root/'reference/prototype.py')
reference = importlib.util.module_from_spec(spec); sys.modules[spec.name] = reference; spec.loader.exec_module(reference)
metrics = dict(runs=0, grouped=0, negative_rejections=0, unmet_positive=0, fixed_unchanged=0,
    proposed=0, independent_support_passed=0, independent_support_rejected=0,
    verified_bands=0, rejected_bands=0, unanchored_joins=0, attempted_maps=0,
    reference_pages=0, surface_images=0, structures=0, raw_images=0, permutations=0, metadata_rejections=0)
records = []
rk = lambda r: [r['page'], r['structural_change_id']]

def image(path, gray=False):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), cv2.IMREAD_GRAYSCALE if gray else cv2.IMREAD_COLOR)

def render(original, segments, side):
    result = np.full((sum(s['length'] for s in segments), original.shape[1], 3), 255, np.uint8)
    for s in segments:
        start = s[side+'_start']
        if start is not None: result[s['canvas_start']:s['canvas_start']+s['length']] = original[start:start+s['length']]
    return result

def support_pixels(run, directory):
    support = run['support']; options = run['options']; result = support['result']
    a = image(directory/'p2-O-A.png', True).astype(np.int64)
    b = image(directory/'p2-O-B.png', True).astype(np.int64)
    la, lb = support['lines_a'], support['lines_b']; width = a.shape[1]
    assert len(la) == len(lb) == 4
    deltas = sorted(x['baseline']-y['baseline'] for x, y in zip(la, lb, strict=True))
    center = round((deltas[1]+deltas[2])/2)
    refine = round(options['refine_mm']/25.4*300); maximum = round(options['max_shift_mm']/25.4*300)
    ybands = lambda lines: [np.arange(math.floor(l['bounds']['top']), math.ceil(l['bounds']['bottom'])) for l in lines]
    ya, yb = ybands(la), ybands(lb)
    score = []
    for dy in range(max(-maximum,center-refine), min(maximum,center+refine)+1):
        difference = mass = 0
        for src, dst, bands, offset in [(a,b,ya,-dy),(b,a,yb,dy)]:
            yy = np.unique(np.concatenate(bands)); x = 255-src[yy]; y = 255-dst[yy+offset]
            difference += int(np.abs(x-y).sum()); mass += int((x+y).sum())
        score.append(dict(dy=dy,score=0 if mass == 0 else 1-difference/mass))
    score.sort(key=lambda x:(-x['score'],abs(x['dy']-center),abs(x['dy'])))
    gap = score[0]['score']-score[1]['score']
    required = options['min_support_ink_mm2']*(300/25.4)**2*255
    masses = [(int((255-a[x]).sum()), int((255-b[y]).sum())) for x,y in zip(ya,yb,strict=True)]
    count = sum(x>=required and y>=required for x,y in masses)
    expected_reason = 'ambiguous' if gap < options['min_score_gap'] else 'insufficient_support' if count < options['min_support_bands'] else 'candidate'
    assert result['reason'] == expected_reason, (run['run'], result, gap, count)
    if expected_reason == 'candidate':
        group, = result['groups']; assert group['dy'] == score[0]['dy'] and group['support_bands'] == count
        assert abs(group['score']-score[0]['score']) < 1e-12 and abs(group['gap']-gap) < 1e-12
        metrics['independent_support_passed'] += 1
    else:
        assert result['groups'] is None
        assert result['detail'] == ('pixel_refinement' if expected_reason == 'ambiguous' else 'support_ink')
        metrics['independent_support_rejected'] += 1
    return dict(center=center,best=score[0],gap=gap,eligible_ink_rows=count,required_mass=required,masses=masses)

for run in json.loads((folder/'cross-page-support.json').read_text()):
    name = run['run']; directory = folder/name; inp = run['input']; got = run['decision']; model = evaluate(inp)
    for key in ['status','difference_count','aggregated_difference_count','difference_count_complete','aggregated_difference_count_complete']:
        assert model[key] == got[key], (name,key)
    components = [c for c in model['components'] if len(c['causes'])>1]
    for c,d in zip(got.get('shared_components',[]), components, strict=True):
        assert c['pages'] == d['pages'] and c['balance'] == d['balance'] and list(map(rk,c['structures'])) == d['structures']
        for x,y in zip(c['causes'], d['causes'], strict=True):
            assert rk(x['reference']) == y['reference'] and x['delta'] == y['delta'] and x['rows'] == len(y['rows'])
        for x,y in zip(c['movements'],d['movements'],strict=True):
            assert rk(x['structure']) == y['structure'] and list(map(rk,x['causes'])) == y['causes'] and x['dy'] == y['dy']
    grouped = got['status'] == 'grouped'; metrics['grouped'] += grouped
    if not run['expected']: assert not grouped; metrics['negative_rejections'] += 1
    elif not grouped:
        assert run['id'] == 'same-page-two' and run['variant'] in ['original','content-tone']
        metrics['unmet_positive'] += 1
    if run['variant'] == 'original':
        previous = old[name]; assert got == previous['legacy'], name
        assert run['product']['decision']['ready'] == previous['gate']['ready']
        metrics['fixed_unchanged'] += 1
        report = json.loads((prior/name/'cli/result.json').read_text())
        for page in report['pages']:
            n = page['page']; originals = [image(directory/f'p{n}-O-{s}.png') for s in ['A','B']]
            for s,im in zip(['a','b'], originals, strict=True):
                assert np.array_equal(im, image(prior/name/'cli'/page['raw_evidence'][s]['image'])); metrics['raw_images'] += 1
            a,b = [((im[:,:,0].astype(np.int32)*114+im[:,:,1].astype(np.int32)*587+im[:,:,2].astype(np.int32)*299+500)//1000) for im in originals]
            tint = (np.minimum(255-a,255-b)*204+127)//255
            raw = np.stack([a+tint,np.minimum(a,b)+tint,b+tint],axis=2).astype(np.uint8)
            assert np.array_equal(raw,image(prior/name/'cli'/page['raw_evidence']['overlay'])); metrics['raw_images'] += 1
    proof = run['evidence']['evidence']; pixel_support = None
    if proof:
        metrics['proposed'] += 1
        aa = sorted([r for r in run['rows'] if r['page']['side']==0],key=lambda r:(r['page']['page'],r['top']))
        bb = sorted([r for r in run['rows'] if r['page']['side']==1],key=lambda r:(r['page']['page'],r['top']))
        edits = [op for op in difflib.SequenceMatcher(a=[r['text'] for r in aa],b=[r['text'] for r in bb],autojunk=False).get_opcodes() if op[0]!='equal']
        assert len(edits)==2 and all(op[0]==('delete' if run['reverse'] else 'insert') for op in edits)
        source,target = (bb,aa) if run['reverse'] else (aa,bb); lookup = {r['text']:r for r in target}
        added = [r for r in target if all(x['text']!=r['text'] for x in source)]
        assert [r['text'] for r in added] == proof['causes'] and len(added)==2
        assert added[-1]['top']==proof['cause']['top'] and added[-1]['page']==proof['cause']['page']
        crossed = [r for r in source if r['page']['page']==1 and lookup[r['text']]['page']['page']==2]
        independent = [r for r in source if r['page']['page']==lookup[r['text']]['page']['page']==2]
        assert crossed == proof['crossing'] and independent == proof['source_support']
        assert [lookup[r['text']] for r in independent] == proof['target_support']
        assert set(r['text'] for r in crossed).isdisjoint(r['text'] for r in independent)
        candidate = proof['candidate']; height = sum(r['height'] for r in crossed)
        assert height == 200 and candidate['support']==0 and len(independent)==4
        for row in independent:
            other = lookup[row['text']]; assert abs(other['baseline']-row['baseline']-height)<1e-8
            assert other['top'] >= candidate['target']['bottom'] and row['left']==other['left']
        assert not any(round(lookup[r['text']]['baseline']-r['baseline'])==height for r in source if r['page']['page']==lookup[r['text']]['page']['page']==1)
        assert proof['before_support'] == proof['between_support'] == 2
        pixel_support = support_pixels(run,directory)
        link, = run['links']; bands=[]
        for band in [link['source'],link['target']]:
            original=image(directory/f"p{band['page']['page']}-O-{'AB'[band['page']['side']]}.png")
            bands.append(original[band['top']:band['bottom']])
        if run['variant']=='carry-tone':
            assert link['status']=='skipped' and not np.array_equal(*bands); metrics['rejected_bands'] += 1
        else:
            assert link['status']=='band_verified' and np.array_equal(*bands); metrics['verified_bands'] += 1
            assert run['original_comparisons']==12
            assert next(m for m in run['maps'] if m['number']==1)['reason']=='unanchored_join'
            assert next(m for m in run['maps'] if m['number']==2)['status']=='built'
        assert not run['gate']['ready'] and run['adoptions']==[] and run['projections']==[]
    for attempt in run['attempts']:
        removed = set(); joins=[]; next_source=[0,0]
        for band,kind in zip(attempt['segments'],attempt['kinds'],strict=True):
            for side,k in enumerate(['a_start','b_start']):
                if band[k] is not None: assert band[k]==next_source[side]; next_source[side]+=band['length']
            if kind==0:
                if removed=={0,1}: joins.append(band['canvas_start'])
                removed=set()
            elif kind==1: removed.add(0 if band['a_start'] is not None else 1)
        assert next_source==[image(directory/f"p{attempt['page']}-O-{s}.png").shape[0] for s in ['A','B']]
        assert bool(joins)==(attempt['page']==1); metrics['unanchored_joins'] += len(joins); metrics['attempted_maps'] += 1
    for proj in run['projections']:
        n=proj['page'];ca,cb=[image(directory/f'p{n}-C-{s}.png') for s in ['A','B']];mask=image(directory/f'p{n}-C-raw.png',True)
        result=reference.compare_page(ca,cb,reference.Params())
        assert np.array_equal(result.raw_mask,mask>0) and result.raw_pixels==proj['raw_pixels'] and len(result.clusters)==len(proj['content_clusters'])
        display=image(directory/f'p{n}-D-raw.png',True);projected=np.zeros_like(display)
        for piece in proj['pieces']:projected[piece['display_start']:piece['display_start']+piece['length']]=mask[piece['content_start']:piece['content_start']+piece['length']]
        assert np.array_equal(projected,display) and np.count_nonzero(display)==np.count_nonzero(mask)
        for side in ['a','b']:
            original=image(directory/f'p{n}-O-{side.upper()}.png')
            for mode in ['C','D']:
                rendered=render(original,proj['content_map' if mode=='C' else 'display_map'],side)
                assert np.array_equal(rendered,image(directory/f'p{n}-{mode}-{side.upper()}.png'));metrics['surface_images']+=1
                if mode=='D':assert np.count_nonzero(np.any(rendered!=255,axis=2))==np.count_nonzero(np.any(original!=255,axis=2))
        metrics['reference_pages']+=1;metrics['structures']+=len(proj['structures'])
    metrics['permutations']+=run['audit']['permutations'];metrics['metadata_rejections']+=sum(run['audit']['rejections'].values());metrics['runs']+=1
    records.append(dict(run=name,expected=run['expected'],grouped=grouped,evidence=run['evidence']['status'],
        support=pixel_support,maps=run['maps'],gate=run['gate'],difference_count=got['difference_count'],aggregated_difference_count=got['aggregated_difference_count']))
assert metrics == dict(runs=44,grouped=12,negative_rejections=28,unmet_positive=4,fixed_unchanged=30,
    proposed=12,independent_support_passed=6,independent_support_rejected=6,verified_bands=10,rejected_bands=2,
    unanchored_joins=10,attempted_maps=20,reference_pages=32,surface_images=128,structures=112,raw_images=180,
    permutations=60,metadata_rejections=1524), metrics
(folder/'verification.json').write_text(json.dumps(dict(metrics=metrics,records=records),indent=2)+'\n')
print(json.dumps(metrics,ensure_ascii=False,indent=2))
