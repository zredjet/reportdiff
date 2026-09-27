#!/usr/bin/env python3
"""複数ページへの表示投影、採用の支持、A設定座標を独立検証する。製品は変更しない。"""
import copy
import importlib.util
import json
import math
import sys
from pathlib import Path
import cv2
import numpy as np
from shared_cause_model import evaluate

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('content_probe',Path(__file__).with_name('anchored-content.py'))
content=importlib.util.module_from_spec(spec);spec.loader.exec_module(content)
read,save,digest=content.read,content.save,content.digest


def prepare(prior,output):
    output.mkdir(parents=True,exist_ok=False)
    records=json.loads((prior/'cross-page-support.json').read_text());cases=[]
    hashes={str(prior/'cross-page-support.json'):digest(prior/'cross-page-support.json')}
    for r in records:
        if r['evidence']['evidence'] is None:continue
        c=content.build(r,prior/r['run'])
        settings=dict(pages=[{},{}],selection_limited=False,global_alignment=False,numeric_rows=False,complete_pages=True)
        case=dict(name=r['run'],source_run=r['run'],reverse=r['reverse'],content=c,settings=settings,minimum_improvement=.05,
            expected=r['variant'] in ('original','content-tone'))
        cases.append(case)
        for path in c['originals'].values():hashes[path]=digest(path)
        if r['variant']=='content-tone':
            new=copy.deepcopy(case);new['name']='low-improvement'+('-ba' if r['reverse'] else '-ab');new['minimum_improvement']=1;new['expected']=False;cases.append(new)
        if r['variant']!='original':continue
        new=copy.deepcopy(case);new['name']='cross-page-context'+('-ba' if r['reverse'] else '-ab')
        folder=output/new['name'];folder.mkdir();key=('A' if r['reverse'] else 'B')+'1'
        image=read(c['originals'][key]);image[798:800,580:820]=0
        file=folder/(key+'.png');save(file,image);new['content']['originals'][key]=str(file);cases.append(new)
        for fault in ('counterpart-swap','cause-redirect','foreign-page'):
            new=copy.deepcopy(case);new['name']=fault+('-ba' if r['reverse'] else '-ab');new['expected']=False
            pieces=new['content']['canvases'][0]['pieces'];side='a' if r['reverse'] else 'b'
            if fault=='counterpart-swap':pieces[1][side],pieces[2][side]=pieces[2][side],pieces[1][side]
            elif fault=='cause-redirect':new['content']['omitted'][0]['top']+=100
            else:pieces[1][side]['key']=pieces[1][side]['key'][0]+'2'
            cases.append(new)
        for policy in ('selection','alignment','numeric','exclude','regions','page-settings','page-scope'):
            new=copy.deepcopy(case);new['name']=policy+('-ba' if r['reverse'] else '-ab');new['expected']=False
            if policy=='selection':new['settings']['selection_limited']=True
            elif policy=='alignment':new['settings']['global_alignment']=True
            elif policy=='numeric':new['settings']['numeric_rows']=True
            elif policy=='page-scope':new['settings']['complete_pages']=False
            elif policy=='page-settings':new['settings']['pages'][1]={'diff':{'max_shift_mm':.3}}
            elif policy=='exclude':new['settings']['pages'][1]={'exclude':[dict(x=0,y=0,w=1,h=1)]}
            else:new['settings']['pages'][1]={'regions':[dict(index=0,name='対照',bounds=dict(x=0,y=0,w=1,h=1),mode='compare',diff={'max_shift_mm':0})]}
            cases.append(new)
    assert len(cases)==36
    for case in cases:
        for path in case['content']['originals'].values():hashes[path]=digest(path)
    fixture=ROOT/'tests/ReportDiff.Tests/Fixtures/page-flow-same-page-support'
    for name,value in json.loads((fixture/'sha256.json').read_text()).items():assert digest(fixture/name)==value;hashes[str(fixture/name)]=value
    (output/'manifest.json').write_text(json.dumps(cases,ensure_ascii=False,indent=2)+'\n')
    (output/'hashes.json').write_text(json.dumps(hashes,indent=2)+'\n')
    print('Prepared 36 cases: 12 evidence candidates, 2 context changes, 2 improvement failures, 14 settings rejections, 6 evidence-map corruptions')


def display_reference(c,p,raw):
    # 元ケースの物理座標から直接C→Dの表を定める。入力のD写像を転写に使わない。
    result={1:np.zeros((1450,999),np.uint8),2:np.zeros((1450,999),np.uint8)}
    if p==1:
        bands=[(0,500,0),(500,700,100),(700,900,200 if c['reverse'] else 100),(900,1250,200)]
        for lo,hi,delta in bands:result[1][lo+delta:hi+delta]=raw[lo:hi]
        result[2][300:500]=raw[700:900]
    else:
        result[2][:300]=raw[:300];result[2][500:1450]=raw[300:1250]
    return result


def check_display_images(c,p,folder):
    source='B' if c['reverse'] else 'A';target='A' if c['reverse'] else 'B'
    if p==1:
        table=[(0,500,0,0),(500,100,None,500),(600,200,500,600),
            (900 if c['reverse'] else 800,200,700,None),(800 if c['reverse'] else 1000,100,None,800),(1100,350,900,900)]
    else:table=[(0,300,0,0),(300,200,None,300),(500,400,300,500),(900,200,700,None),(1100,350,900,900)]
    for side,slot in [(source,2),(target,3)]:
        original=read(c['content']['originals'][side+str(p)]);expected=np.full((1450,999,3),255,np.uint8);coverage=np.zeros(1250,np.int32)
        for row in table:
            y,length,start=row[0],row[1],row[slot]
            if start is None:continue
            expected[y:y+length]=original[start:start+length];coverage[start:start+length]+=1
        assert np.all(coverage==1)
        assert np.array_equal(expected,read(folder/f'D{p}-{side}.png')),(c['name'],p,side,'display_image')


def supports(c,page):
    a=cv2.cvtColor(read(c['content']['originals']['A'+str(page['page'])]),cv2.COLOR_BGR2GRAY).astype(np.int64)
    b=cv2.cvtColor(read(c['content']['originals']['B'+str(page['page'])]),cv2.COLOR_BGR2GRAY).astype(np.int64)
    ad=page['adoption'];la,lb=ad['lines_a'],ad['lines_b'];groups=[]
    for m in ad['matches']:
        dy=la[m['a']]['baseline']-lb[m['b']]['baseline']
        if not groups or abs(dy-groups[-1][0])>8:groups.append([dy,[]])
        groups[-1][1].append(m)
    rows=lambda line:np.arange(math.floor(line['bounds']['top']),math.ceil(line['bounds']['bottom']))
    evidence=[];reason='candidate';mass_total=0;difference_total=0
    for _,matches in groups:
        if len(matches)<2:reason='insufficient_support';break
        center=round(float(np.median([la[m['a']]['baseline']-lb[m['b']]['baseline'] for m in matches])))
        ya=[rows(la[m['a']]) for m in matches];yb=[rows(lb[m['b']]) for m in matches]
        scores=[]
        for dy in range(center-4,center+5):
            distance=mass=0
            for source,target,bands,offset in [(a,b,ya,-dy),(b,a,yb,dy)]:
                yy=np.unique(np.concatenate(bands));aa=255-source[yy];bb=255-target[yy+offset]
                distance+=int(np.abs(aa-bb).sum());mass+=int((aa+bb).sum())
            scores.append((1-distance/mass if mass else 0,dy,distance,mass))
        scores.sort(key=lambda item:(-item[0],abs(item[1]-center),abs(item[1])))
        best=scores[0];gap=best[0]-scores[1][0]
        ink=sum(min(int((255-a[x]).sum()),int((255-b[y]).sum()))>=255*(300/25.4)**2 for x,y in zip(ya,yb))
        evidence.append(dict(dy=best[1],score=best[0],gap=gap,support_bands=ink))
        if gap<.02:reason='ambiguous';break
        if ink<2:reason='insufficient_support';break
        mass_total+=best[3];difference_total+=best[2]
    assert ad['validation']['reason']==reason,(c['name'],page['page'],ad['validation'],evidence)
    if reason=='candidate':
        for x,y in zip(evidence,ad['validation']['groups'],strict=True):
            assert all(abs(x[k]-y[k])<1e-12 for k in ('dy','score','gap','support_bands'))
        assert ad['score'] is not None
        assert abs(ad['score'][0]['score']-(1-difference_total/mass_total))<1e-12
    return evidence


def settings_audit(c):
    # A1とA2の同じyでも異なる所有ラベルを持つ。順逆で出自を辿って往復させる。
    canvases=c['content']['canvases'];width=999;height=1250
    originals={1:np.zeros((height,width),np.uint8),2:np.zeros((height,width),np.uint8)}
    originals[1][300:900,600:610]=1;originals[2][300:900,600:610]=2
    restored={p:np.zeros_like(m) for p,m in originals.items()};masks=[]
    for canvas in canvases:
        mask=np.zeros((height,width),np.uint8)
        for piece in canvas['pieces']:
            a=piece['a']
            if a is None:continue
            page=int(a['key'][1:]);top=piece['top'];length=piece['length']
            mask[top:top+length]=originals[page][a['top']:a['top']+length]
            restored[page][a['top']:a['top']+length]=mask[top:top+length]
        masks.append(mask)
    for cause in c['content']['omitted']:
        if cause['key'][0]=='A':originals[int(cause['key'][1:])][cause['top']:cause['top']+cause['length']]=0
    assert all(np.array_equal(restored[p],originals[p]) for p in originals)
    assert np.all(masks[0][700:900,600:610]==(2 if c['reverse'] else 1))
    assert np.all(masks[1][700:900,600:610]==(0 if c['reverse'] else 2))
    return dict(roundtrip=True,carry_owner=2 if c['reverse'] else 1,white_owner=0 if c['reverse'] else 2,
        omitted_setting_pixels=2000 if c['reverse'] else 0,scope='coordinate_model_only')


def check(inputs,output):
    cases={c['name']:c for c in json.loads((inputs/'manifest.json').read_text())}
    for path,sha in json.loads((inputs/'hashes.json').read_text()).items():assert digest(path)==sha,path
    records=json.loads((output/'projection.json').read_text())
    metrics=dict(cases=0,accepted=0,rejected=0,compared_pages=0,projected_masks=0,cross_page_clusters=0,support_groups=0,
        small_fragment_audits=0,audit_masks=0,settings_roundtrips=0,all_page_rollbacks=0,structures=0,display_images=0)
    results=[]
    spec=importlib.util.spec_from_file_location('projection_reference',ROOT/'reference/prototype.py')
    reference=importlib.util.module_from_spec(spec);sys.modules[spec.name]=reference;spec.loader.exec_module(reference)
    for r in records:
        c=cases[r['name']];metrics['cases']+=1
        assert r['accepted']==c['expected'],(r['name'],r['reason'],[(p['page'],p['adoption']['reason']) for p in r.get('pages',[])])
        metrics['accepted' if r['accepted'] else 'rejected']+=1
        assert r['published_pages']==([1,2] if r['accepted'] else [])
        for p in r.get('pages',[]):
            page=p['page'];folder=output/r['name'];raw=read(folder/f'C{page}-raw.png',True)
            check_display_images(c,page,folder);metrics['display_images']+=2
            # 内容マスクは通常座標の参照コアで再検証する。
            compared=reference.compare_page(read(folder/f'C{page}-A.png'),read(folder/f'C{page}-B.png'),reference.Params())
            assert np.array_equal(raw>0,compared.raw_mask) and compared.raw_pixels==p['raw_pixels']
            projection=p['projection'];ids=np.zeros(raw.shape,np.int32)
            for y,x,length,id in projection['runs']:assert not np.any(ids[y,x:x+length]);ids[y,x:x+length]=id
            assert np.all((ids==0)|(raw>0))
            for cluster in projection['clusters']:
                assert np.count_nonzero(ids==cluster['content_id'])==cluster['pixels']
                assert cluster['owner_page']==page
                expected=display_reference(c,page,(ids==cluster['content_id']).astype(np.uint8))
                painted={d:np.zeros_like(m) for d,m in expected.items()}
                for part in cluster['parts']:
                    x0,y0,x1,y1=part['content_bounds'];dx0,dy0,dx1,dy1=part['display_bounds']
                    piece=(ids[y0:y1,x0:x1]==cluster['content_id']).astype(np.uint8)
                    assert int(piece.sum())==part['pixels'] and dx0==x0 and dx1==x1
                    painted[part['display_page']][dy0:dy1,dx0:dx1]|=piece
                assert all(np.array_equal(painted[d],expected[d]) for d in expected),(r['name'],page,'display-fragments')
                if len({p['display_page'] for p in cluster['parts']})>1:metrics['cross_page_clusters']+=1
            expected=display_reference(c,page,raw)
            for d,image in expected.items():
                assert np.array_equal(image,read(folder/f'C{page}-D{d}-raw.png',True))
                assert np.count_nonzero(image)==projection['display_pixels'][str(d)];metrics['projected_masks']+=1
            metrics['support_groups']+=len(supports(c,p));metrics['compared_pages']+=1
            ad=p['adoption'];assert abs(ad['improvement']-(ad['baseline_pixels']-ad['candidate_pixels'])/ad['baseline_pixels'])<1e-12
            assert ad['candidate_pixels']==p['raw_pixels'];metrics['structures']+=len(p['structures'])
        if not r['accepted'] and any(p['adoption']['accepted'] for p in r.get('pages',[])):metrics['all_page_rollbacks']+=1
        if r.get('aggregate'):
            model=evaluate(r['aggregate_input']);native=r['aggregate']
            for key in ('status','difference_count','aggregated_difference_count','difference_count_complete','aggregated_difference_count_complete'):
                assert model[key]==native[key],(r['name'],key,model,native)
            assert native['status']=='grouped'
            assert native['aggregated_difference_count']==2+sum(len(p['clusters']) for p in r['pages'])
            assert r['cause_omitted_pixels']==199800
            assert sorted(a['role'] for a in r['structure_audit'])==['carry','carry','cause','cause','movement','movement']
        if r.get('small_fragment_audit'):
            audit=r['small_fragment_audit'];cluster,=audit['clusters'];assert cluster['pixels']==4
            assert min(p['pixels'] for p in cluster['parts'])==2
            assert audit['display_pixels']=={'1':5,'2':3}
            assert len({p['display_page'] for p in cluster['parts']})==2
            expected_raw=np.zeros((1250,999),np.uint8);expected_raw[699:701,620:622]=255;expected_raw[700,650]=255
            for page,mask in display_reference(c,1,expected_raw).items():
                assert np.array_equal(mask,read(output/r['name']/f'audit-D{page}-raw.png',True))
                metrics['audit_masks']+=1
            assert audit['runs']==[[699,620,2,1],[700,620,2,1]]
            metrics['small_fragment_audits']+=1
        if r['name'] in ('same-page-two-ab','same-page-two-ba'):
            setting=settings_audit(c);metrics['settings_roundtrips']+=1
        else:setting=None
        results.append(dict(name=r['name'],accepted=r['accepted'],reason=r['reason'],settings=setting,
            difference_count=r.get('aggregate',{}).get('difference_count') if r.get('aggregate') else None,
            aggregated_count=r.get('aggregate',{}).get('aggregated_difference_count') if r.get('aggregate') else None))
    assert metrics['cases']==36 and metrics['accepted']==6 and metrics['rejected']==30
    assert metrics['cross_page_clusters']==2 and metrics['small_fragment_audits']==2 and metrics['settings_roundtrips']==2
    (output/'verification.json').write_text(json.dumps(dict(metrics=metrics,runs=results,production_integrated=False),ensure_ascii=False,indent=2)+'\n')
    print(json.dumps(metrics,indent=2))


if __name__=='__main__':
    mode,first,second=sys.argv[1:]
    if mode=='prepare':prepare(Path(first).resolve(),Path(second).resolve())
    elif mode=='check':check(Path(first).resolve(),Path(second).resolve())
    else:raise ValueError(mode)
