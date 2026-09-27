#!/usr/bin/env python3
"""元ページを保持する内容面の独立入力作成・画素参照照合。製品の採用判定はしない。"""
import copy
import hashlib
import importlib.util
import json
import sys
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]


def read(path, gray=False):
    return cv2.imdecode(np.frombuffer(Path(path).read_bytes(), np.uint8), 0 if gray else 1)


def save(path, value):
    Path(path).write_bytes(cv2.imencode('.png', value)[1].tobytes())


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def origin(side, page, top):
    return dict(key='AB'[side]+str(page), top=top)


def build(record, folder):
    rows = record['rows']
    source = int(record['reverse'])
    target = 1-source
    before = [r for r in rows if r['page']['side'] == source]
    after = [r for r in rows if r['page']['side'] == target]
    lookup = {r['text']: r for r in after}
    result = dict(name=record['run'], originals={s+str(p): str(folder/f'p{p}-O-{s}.png')
        for s in 'AB' for p in (1, 2)}, canvases=[], omitted=[], max_shift_mm=.15, expected_rejection=None)
    for p in (1, 2):
        source_rows = sorted([r for r in before if r['page']['page'] == p], key=lambda r:r['top'])
        target_rows = [r for r in after if r['page']['page'] == p]
        start = source_rows[0]['top']
        source_end = max(r['top']+r['height'] for r in source_rows)
        target_end = max(r['top']+r['height'] for r in target_rows)
        tail = max(source_end, target_end)
        height = read(result['originals']['A'+str(p)]).shape[0]
        pieces = [dict(top=0,length=start,a=origin(0,p,0),b=origin(1,p,0))]
        for r in source_rows:
            other = lookup[r['text']]
            pair = [None, None]
            pair[source] = origin(source,p,r['top'])
            pair[target] = origin(target,other['page']['page'],other['top'])
            pieces.append(dict(top=r['top'],length=r['height'],a=pair[0],b=pair[1]))
        if tail > source_end:
            pair = [None, None]; pair[source] = origin(source,p,source_end)
            pieces.append(dict(top=source_end,length=tail-source_end,a=pair[0],b=pair[1]))
        pieces.append(dict(top=tail,length=height-tail,a=origin(0,p,tail),b=origin(1,p,tail)))
        result['canvases'].append(dict(page=p,anchor='AB'[source]+str(p),pieces=pieces))
    for r in after:
        if r['text'] not in {r['text'] for r in before}:
            result['omitted'].append(dict(key='AB'[target]+str(r['page']['page']),top=r['top'],length=r['height']))
    proof = record['evidence']['evidence']['candidate']
    result['carry'] = dict(source=origin(source,1,proof['source']['top']),
        target=origin(target,2,proof['target']['top']), length=proof['source']['height'])
    return result


def prepare(prior, output):
    output.mkdir(parents=True, exist_ok=False)
    records = json.loads((prior/'cross-page-support.json').read_text())
    hashes = {str(p.relative_to(ROOT)): digest(p) for p in [prior/'cross-page-support.json']}
    fixture = ROOT/'tests/ReportDiff.Tests/Fixtures/page-flow-same-page-support'
    for name, expected in json.loads((fixture/'sha256.json').read_text()).items():
        assert digest(fixture/name) == expected
        hashes[str((fixture/name).relative_to(ROOT))] = expected
    cases = []; expectations = {}; eligibility = []
    for r in records:
        has_proof = r['evidence']['evidence'] is not None
        eligible = has_proof and r['support']['result']['reason'] == 'candidate' and r['links'][0]['status'] == 'band_verified'
        eligibility.append(dict(run=r['run'],content_candidate=eligible,prior_aggregate=r['decision']['aggregated_difference_count']))
        if not eligible:
            continue
        assert r['id'] == 'same-page-two' and r['variant'] in ('original', 'content-tone')
        c = build(r, prior/r['run']); cases.append(c)
        for path in c['originals'].values(): hashes[str(Path(path).relative_to(ROOT))] = digest(path)
        expected = dict(kind='fixed',detect=r['variant']=='content-tone',source_run=r['run'])
        expectations[c['name']] = expected
        if r['variant'] != 'original':
            continue
        source = int(r['reverse']); target = 1-source
        originals = {k:read(v) for k,v in c['originals'].items()}
        # 比較面の正解は元の保持側ページと、その通常座標に加える変更から独立に作る。
        for variant in ('seam-tone','seam-shape','seam-rule','thin-mark','I10','I11','I12','D20','D21','carry-edge','nonwhite-space'):
            new = copy.deepcopy(c); new['name'] = variant+('-ba' if r['reverse'] else '-ab')
            directory = output/new['name']; directory.mkdir()
            altered = {k:v.copy() for k,v in originals.items()}
            a = originals['AB'[source]+'1'].copy(); b = a.copy()
            if variant == 'seam-tone': b[497:504,620:650] = (80, 100, 160)
            elif variant == 'seam-shape': b[494:507,630:633] = 0; b[504:507,620:636] = 0
            elif variant == 'seam-rule': b[499:501,580:820] = 0
            elif variant == 'thin-mark': b[499:500,610:646] = 128
            elif variant == 'carry-edge': b[699:703,610:646] = 0
            elif variant == 'nonwhite-space': altered['AB'[source]+'2'][800,700] = (254,255,255)
            else:
                for side, dest in [('a',a),('b',b)]:
                    gold = ROOT/f'reference/golden/{variant}_{side}.png'; hashes[str(gold.relative_to(ROOT))] = digest(gold)
                    image = read(gold)
                    # 既存周期帯を再描画せず、白い周辺込みで境界500へ配置する。
                    if variant == 'I11': dest[292:708,580:660] = image[208:624,208:288]
                    else: dest[468:548,100:872] = image[208:288,208:980]
                if variant in ('I12','D21'): new['max_shift_mm'] = .30
            if variant != 'nonwhite-space':
                # 元ページへの散布。変更点だけを書き、未変更の元画素と原因帯を保つ。
                canvas = c['canvases'][0]
                for side, wanted in [(source,a),(target,b)]:
                    key = 'ab'[side]
                    baseline = originals['AB'[source]+'1']
                    for piece in canvas['pieces']:
                        o = piece[key]
                        if o is None: continue
                        top=piece['top']; length=piece['length']; band=wanted[top:top+length]
                        changed=np.any(band!=baseline[top:top+length],axis=2)
                        target_band=altered[o['key']][o['top']:o['top']+length]
                        target_band[changed] = band[changed]
                save(directory/'oracle-A.png', a if source == 0 else b)
                save(directory/'oracle-B.png', b if source == 0 else a)
            for key,image in altered.items():
                path=directory/(key+'.png');save(path,image);new['originals'][key]=str(path)
            if variant == 'nonwhite-space': new['expected_rejection']='nonwhite_space'
            if variant == 'carry-edge': new['expected_rejection']='carry_pixels'
            # I11の帯の外端は白であり、700境界の送り画素を変えていないことも検査する。
            expected=dict(kind='synthetic',detect=variant in ('seam-tone','seam-shape','seam-rule','thin-mark','D20','D21'),
                ignored=variant in ('I10','I11','I12'), source_run=r['run'], variant=variant,
                oracle=str(directory),not_pdf_fixture=True)
            cases.append(new);expectations[new['name']]=expected
        # 原因の意味は別工程。ここでは写像の機械的条件を壊す対照を先に固定する。
        for defect in ('canvas-gap','short-canvas','anchor-jump','wrong-page','wrong-side','duplicate-source',
                       'source-oob','drop-cause','duplicate-cause','white-cause','null-anchor','duplicate-canvas',
                       'mixed-anchor','duplicate-number','unknown-key','empty-anchor'):
            new=copy.deepcopy(c); new['name']=defect+('-ba' if r['reverse'] else '-ab'); pieces=new['canvases'][0]['pieces']
            source_key='ab'[source];target_key='ab'[target]
            reason='source_coverage'
            if defect=='canvas-gap': pieces[1]['top']+=1;reason='canvas_coverage'
            elif defect=='short-canvas':pieces[-1]['length']-=1;reason='canvas_coverage'
            elif defect=='anchor-jump':pieces[2][source_key]['top']+=1;reason='anchor_discontinuity'
            elif defect=='wrong-page':pieces[2][source_key]['key']='AB'[source]+'2';reason='anchor_discontinuity'
            elif defect=='wrong-side':pieces[2][source_key]['key']='AB'[target]+'1';reason='source_range'
            elif defect=='duplicate-source':pieces[2][target_key]=copy.deepcopy(pieces[1][target_key])
            elif defect=='source-oob':pieces[2][target_key]['top']=1249;reason='source_range'
            elif defect=='drop-cause':new['omitted'].pop()
            elif defect=='duplicate-cause':new['omitted'].append(copy.deepcopy(new['omitted'][0]))
            elif defect=='white-cause':new['omitted'].append(dict(key='AB'[target]+'1',top=1000,length=10));reason='white_cause'
            elif defect=='null-anchor':pieces[1][source_key]=None;reason='anchor_discontinuity'
            elif defect=='duplicate-canvas':new['canvases'][1]=copy.deepcopy(new['canvases'][0]);reason='document_shape'
            elif defect=='mixed-anchor':new['canvases'][1]['anchor']='AB'[target]+'2';reason='document_shape'
            elif defect=='duplicate-number':new['canvases'][1]['page']=1;reason='document_shape'
            elif defect=='unknown-key':new['originals']['A3']=new['originals'].pop('A1');reason='document_shape'
            elif defect=='empty-anchor':new['canvases'][0]['anchor']='';reason='document_shape'
            new['expected_rejection']=reason;cases.append(new);expectations[new['name']]=dict(kind='invalid-map',source_run=r['run'])
    assert len(cases)==58 and sum(x['content_candidate'] for x in eligibility)==4
    (output/'manifest.json').write_text(json.dumps(cases,indent=2)+'\n')
    (output/'expectations.json').write_text(json.dumps(expectations,indent=2)+'\n')
    (output/'inputs.json').write_text(json.dumps(dict(hashes=hashes,eligibility=eligibility),indent=2)+'\n')
    print(f'Prepared {len(cases)} cases: 4 fixed candidates, 22 synthetic cases, 32 malformed maps')


def check(inputs, output):
    spec=importlib.util.spec_from_file_location('anchored_reference',ROOT/'reference/prototype.py')
    ref=importlib.util.module_from_spec(spec);sys.modules[spec.name]=ref;spec.loader.exec_module(ref)
    manifests=json.loads((inputs/'manifest.json').read_text()); expect=json.loads((inputs/'expectations.json').read_text())
    result={r['name']:r for r in json.loads((output/'content.json').read_text())}
    prior_inputs=json.loads((inputs/'inputs.json').read_text())
    for path,value in prior_inputs['hashes'].items():assert digest(ROOT/path)==value,path
    metrics=dict(cases=0,rejected=0,compared=0,reference_pages=0,oracle_images=0,projected_images=0,
        detected=0,ignored=0,unmet_detection=0,absorbed_groups=0,source_rows=0,white_pixels=0)
    evidence=[]
    for c in manifests:
        name=c['name']; r=result[name]; e=expect[name]; metrics['cases']+=1
        assert r['rejection']==c['expected_rejection'] and r['adoption']==r['aggregation']=='not_evaluated'
        if r['rejection'] is not None:
            assert r['pages']==[];metrics['rejected']+=1;continue
        metrics['compared']+=1
        originals={k:read(v) for k,v in c['originals'].items()}
        source=c['canvases'][0]['anchor'][0]; target='B' if source=='A' else 'A'
        # 元ケースの手計算した物理座標。入力写像の描画処理を正解の生成に再利用しない。
        t1,t2=originals[target+'1'],originals[target+'2']
        physical_reference={1:np.concatenate((t1[:500],t1[600:800],t2[300:500],t1[900:])),
            2:np.concatenate((t2[:300],t2[500:900],np.full_like(t2[:200],255),t2[900:]))}
        coverage={k:np.zeros(im.shape[0],dtype=np.int32) for k,im in originals.items()}
        projected={k:np.zeros(im.shape[:2],dtype=np.uint8) for k,im in originals.items()}
        total=0;absorbed=0
        for canvas,page in zip(c['canvases'],r['pages'],strict=True):
            n=canvas['page'];anchor=originals[canvas['anchor']];h,w=anchor.shape[:2]
            rendered={side:np.full_like(anchor,255) for side in ('a','b')}
            for piece in canvas['pieces']:
                top=piece['top'];length=piece['length']
                for side in ('a','b'):
                    o=piece[side]
                    if o is None:
                        assert np.all(anchor[top:top+length]==255);metrics['white_pixels']+=length*w;continue
                    rendered[side][top:top+length]=originals[o['key']][o['top']:o['top']+length]
                    coverage[o['key']][o['top']:o['top']+length]+=1
            for side in ('a','b'):
                assert np.array_equal(rendered[side],read(output/name/f'p{n}-C-{side.upper()}.png'))
            assert np.array_equal(rendered[canvas['anchor'][0].lower()],anchor)
            assert np.array_equal(rendered[target.lower()],physical_reference[n]),(name,n,'physical_reference')
            if n==1 and e['kind']=='synthetic':
                for side in ('A','B'):
                    assert np.array_equal(rendered[side.lower()],read(Path(e['oracle'])/f'oracle-{side}.png')),(name,side)
                    metrics['oracle_images']+=1
            # 既存Pythonコアが通常座標で全体を処理。継ぎ目を渡さず、グループも分割しない。
            compared=ref.compare_page(rendered['a'],rendered['b'],ref.Params(max_shift_mm=c['max_shift_mm']))
            raw=read(output/name/f'p{n}-C-raw.png',True)
            assert np.array_equal(raw>0,compared.raw_mask),(name,n,'mask')
            assert (page['raw_pixels'],page['noise_dropped'],page['absorbed_groups'],page['status']) == (
                compared.raw_pixels,compared.noise_dropped,compared.absorbed_groups,compared.status),(name,n)
            assert len(page['clusters'])==len(compared.clusters),(name,n,'clusters')
            for native,model in zip(page['clusters'],compared.clusters,strict=True):
                assert native['pixels']==model.pixels
                assert [native['bounds'][k] for k in ('left','top','right','bottom')]==[model.x,model.y,model.x+model.w,model.y+model.h]
            metrics['reference_pages']+=1;total+=page['raw_pixels'];absorbed+=page['absorbed_groups']
            for piece in canvas['pieces']:
                band=raw[piece['top']:piece['top']+piece['length']]
                for side in ('a','b'):
                    o=piece[side]
                    if o is not None:projected[o['key']][o['top']:o['top']+piece['length']]=band
            assert sum(f['pixels'] for f in page['fragments'] if f['origin']['key']==canvas['anchor'])==page['raw_pixels']
        for omit in c['omitted']:coverage[omit['key']][omit['top']:omit['top']+omit['length']]+=1
        for key,cov in coverage.items():assert np.all(cov==1),(name,key);metrics['source_rows']+=len(cov)
        for key,im in projected.items():
            assert np.array_equal(im,read(output/name/(key+'-projected.png'),True))
            assert np.count_nonzero(im)==r['projected_pixels'][key];metrics['projected_images']+=1
        detected=total>0 and sum(len(p['clusters']) for p in r['pages'])>0
        if e.get('detect'):
            if detected:metrics['detected']+=1
            else:
                # 事前期待は検出のまま。通常座標の参照比較でも0の薄色1px対照を未成立として記録する。
                assert e.get('variant')=='thin-mark' and total==0,name
                metrics['unmet_detection']+=1
        elif e.get('ignored'):assert total==0 and absorbed>0,name;metrics['ignored']+=1
        else: assert total==0,name
        if e['kind']=='fixed' and e['detect']:assert total==3807 and len(r['pages'][0]['clusters'])==1
        metrics['absorbed_groups']+=absorbed
        evidence.append(dict(name=name,raw_pixels=total,clusters=sum(len(p['clusters']) for p in r['pages']),absorbed_groups=absorbed,
            expected_detection=e.get('detect',False),detection_expectation_met=not e.get('detect') or detected))
    assert metrics['cases']==58 and metrics['rejected']==36 and metrics['compared']==22
    assert metrics['detected']==12 and metrics['ignored']==6 and metrics['unmet_detection']==2
    summary=dict(metrics=metrics,runs=evidence,scope='content_surface_only',adoption='not_evaluated',aggregation='not_evaluated',
        prior_eligibility=prior_inputs['eligibility'],input_hashes=prior_inputs['hashes'])
    (output/'verification.json').write_text(json.dumps(summary,indent=2)+'\n');print(json.dumps(metrics,indent=2))


if __name__=='__main__':
    mode, first, second=sys.argv[1:]
    if mode=='prepare':prepare(Path(first).resolve(),Path(second).resolve())
    elif mode=='check':check(Path(first).resolve(),Path(second).resolve())
    else:raise ValueError(mode)
