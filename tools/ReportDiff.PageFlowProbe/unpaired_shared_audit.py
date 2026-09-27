"""末尾1枚の共有原因に対する編集列の別検証と破損監査。製品からは使用しない。"""
import copy
import difflib
import random
from shared_cause_model import evaluate, refkey, band


def oracle(inp, result):
    rows=[sorted((r for r in inp['rows'] if r['side']==s), key=lambda r:(r['page'],r['start'])) for s in (0,1)]
    lookup=[{r['text']:r for r in rr} for rr in rows]
    edits=[x for x in difflib.SequenceMatcher(a=[r['text'] for r in rows[0]],b=[r['text'] for r in rows[1]],autojunk=False).get_opcodes() if x[0]!='equal']
    assert len(result['components'])==1
    c=result['components'][0]
    assert len(edits)==len(c['causes']) and {e[0] for e in edits} in ({'insert'},{'delete'})
    refs=[]
    for e,cause in zip(edits,c['causes']):
        tag,i,j,k,l=e
        actual=rows[1][k:l] if tag=='insert' else rows[0][i:j]
        assert cause['rows']==[r['text'] for r in actual] and cause['band']==band(actual)
        structures=[s for p in inp['pages'] for s in p['structures'] if refkey(s)==tuple(cause['reference'])]
        assert len(structures)==1 and structures[0]['b' if tag=='insert' else 'a']==band(actual)
        refs.append(cause['reference'])
    indices=[{r['text']:i for i,r in enumerate(rr)} for rr in rows]
    for movement in c['movements']:
        for text in movement['rows']:
            a,b=lookup[0][text],lookup[1][text]
            assert a['page']==b['page'] and b['start']-a['start']==movement['dy']
            contributors=[ident for op,ident in zip(edits,refs) if (op[4]<=indices[1][text] if op[0]=='insert' else op[2]<=indices[0][text])]
            assert movement['causes']==contributors
    common=set(lookup[0])&set(lookup[1])
    for flow in c['flows']:
        crossing=[t for t in common if {lookup[0][t]['page'],lookup[1][t]['page']}=={flow['boundary'],flow['boundary']+1}]
        assert set(flow['rows'])==set(crossing) and flow['height']==sum(lookup[0][t]['length'] for t in crossing)
    for b in c['balance']:
        n=b['page'];delta=sum((op[4]-op[3]) if op[0]=='insert' else -(op[2]-op[1]) for op,cause in zip(edits,c['causes']) if cause['band']['page']['page']==n)
        incoming=sum((1 if lookup[1][t]['page']>lookup[0][t]['page'] else -1) for t in common if max(lookup[0][t]['page'],lookup[1][t]['page'])==n and lookup[0][t]['page']!=lookup[1][t]['page'])
        outgoing=sum((1 if lookup[1][t]['page']>lookup[0][t]['page'] else -1) for t in common if min(lookup[0][t]['page'],lookup[1][t]['page'])==n and lookup[0][t]['page']!=lookup[1][t]['page'])
        assert (delta,incoming,outgoing)==(b['cause_delta'],b['incoming'],b['outgoing'])
        assert b['rows_b']-b['rows_a']==delta+incoming-outgoing
    auxiliary=c['auxiliary_bands'];assert len(auxiliary)==1
    assert auxiliary[0]==band([r for r in inp['rows'] if r['page']==len(inp['pages'])])
    assert result['aggregated_difference_count']==len(edits)+sum(p['clusters'] for p in inp['pages'])


def audit(inp, expected):
    counts=dict(rejected=0,permutations=0,content_preserved=0,incomplete=0)
    def reject(value):
        d=evaluate(value,terminal=True)
        assert d['status']=='skipped' and not d['components'] and d['difference_count']==d['aggregated_difference_count'],(value,d)
        counts['rejected']+=1
    for seed in range(5):
        v=copy.deepcopy(inp);rng=random.Random(seed)
        for field in ['rows','pages','links']:rng.shuffle(v[field])
        for p in v['pages']:rng.shuffle(p['structures'])
        assert evaluate(v,terminal=True)==expected
        counts['permutations']+=1
    for field,value in [('gate_ready',False),('selection_limited',True)]:
        v=copy.deepcopy(inp);v[field]=value;reject(v)
    for i,row in enumerate(inp['rows']):
        for kind in ['remove','duplicate','start','length','text']:
            v=copy.deepcopy(inp)
            if kind=='remove':del v['rows'][i]
            elif kind=='duplicate':v['rows'].append(copy.deepcopy(row))
            elif kind=='text':v['rows'][i]['text']=next(r['text'] for r in inp['rows'] if r['side']==row['side'] and r['text']!=row['text'])
            else:v['rows'][i][kind]+=1
            reject(v)
    for i,link in enumerate(inp['links']):
        for kind in ['remove','duplicate','status','reason','text','target','height','side']:
            v=copy.deepcopy(inp)
            if kind=='remove':del v['links'][i]
            elif kind=='duplicate':v['links'].append(copy.deepcopy(link))
            elif kind=='status':v['links'][i]['status']='skipped'
            elif kind=='reason':v['links'][i]['reason']='broken'
            elif kind=='text':v['links'][i]['text']=['BROKEN ROW']
            elif kind=='target':v['links'][i]['target']['top']+=1
            elif kind=='height':v['links'][i]['source']['height']+=1
            else:v['links'][i]['target']['page']['side']=v['links'][i]['source']['page']['side']
            reject(v)
    for i,page in enumerate(inp['pages']):
        for kind in ['remove','duplicate','count','paired']:
            v=copy.deepcopy(inp)
            if kind=='remove':del v['pages'][i]
            elif kind=='duplicate':v['pages'].append(copy.deepcopy(page))
            elif kind=='count':v['pages'][i]['difference_count']+=1
            else:v['pages'][i]['paired']=not page['paired']
            reject(v)
        if not page['paired']:
            v=copy.deepcopy(inp);v['pages'][i]['unpaired_covered']=False;reject(v)
            v=copy.deepcopy(inp);v['pages'][i]['clusters']=v['pages'][i]['difference_count']=1;reject(v)
        else:
            v=copy.deepcopy(inp);v['pages'][i]['clusters']+=1;v['pages'][i]['difference_count']+=1
            actual=evaluate(v,terminal=True)
            assert actual['status']=='grouped' and actual['aggregated_difference_count']==expected['aggregated_difference_count']+1
            counts['content_preserved']+=1
        for j,structure in enumerate(page['structures']):
            for kind in ['remove','duplicate','excluded','id','kind','band']+(['dy'] if structure['kind']=='block_moved' else []):
                v=copy.deepcopy(inp);p=v['pages'][i];s=p['structures'][j]
                if kind=='remove':del p['structures'][j];p['difference_count']-=1
                elif kind=='duplicate':p['structures'].append(copy.deepcopy(structure));p['difference_count']+=1
                elif kind=='excluded':s['excluded']=True
                elif kind=='id':s['reference']['page']=len(inp['pages'])
                elif kind=='kind':s['kind']='unknown'
                elif kind=='dy':s['dy']+=1
                else:
                    b=s['a'] or s['b'];b['top']+=1;b['bottom']+=1
                reject(v)
    v=copy.deepcopy(inp)
    for p in v['pages']:p['complete']=True
    actual=evaluate(v,terminal=True)
    assert actual['status']=='grouped' and not actual['difference_count_complete'] and not actual['aggregated_difference_count_complete']
    counts['incomplete']+=1
    return counts


def boundaries(records):
    checked=0;candidate_count=0
    for run in records:
        inp=run['input'];inferred=run['inferred']
        if inferred['status']!='inferred':continue
        rr=[sorted((r for r in inp['rows'] if r['side']==s),key=lambda r:(r['page'],r['start'])) for s in (0,1)]
        lookup=[{r['text']:r for r in side} for side in rr]
        assert all(len(v)==len(r) for v,r in zip(lookup,rr))
        assert [r['text'] for r in rr[0] if r['text'] in lookup[1]]==[r['text'] for r in rr[1] if r['text'] in lookup[0]]
        for bd in inferred['boundaries']:
            side,n=bd['side'],bd['page'];other=lookup[1-side]
            source=[r for r in rr[side] if r['page']==n]
            crossing=[r for r in source if r['text'] in other and other[r['text']]['page']==n+1]
            assert [r['text'] for r in crossing]==[r['text'] for r in bd['crossing']]
            shifts={}
            for r in source:
                if r['text'] in other and other[r['text']]['page']==n:
                    dy=other[r['text']]['start']-r['start'];shifts.setdefault(dy,[]).append(r['text'])
            assert [(x['dy'],x['text'],x['eligible']) for x in bd['shifts']]==[(dy,text,0<dy<=236 and len(text)>=2) for dy,text in sorted(shifts.items())]
            if bd['candidate']:
                c=bd['candidate'];counterparts=[other[r['text']] for r in crossing]
                assert c['source']==band(crossing) and c['target']==band(counterparts) and c['text']==[r['text'] for r in crossing]
                assert c['source']['bottom']==max(r['start']+r['length'] for r in source)
                assert c['target']['top']==min(r['start'] for r in rr[1-side] if r['page']==n+1)
                assert c['support']==len(shifts.get(c['source']['height'],[]))
                candidate_count+=1
            checked+=1
    return dict(boundaries=checked,candidates=candidate_count)


if __name__=='__main__':
    import hashlib
    import importlib.util
    import json
    from pathlib import Path
    import sys
    import cv2
    import numpy as np
    folder=Path(sys.argv[1]).resolve();records=json.loads((folder/'diagnosis.json').read_text())
    result=boundaries(records);controls=[]
    def image(path):return cv2.imdecode(np.frombuffer(path.read_bytes(),np.uint8),cv2.IMREAD_COLOR)
    for reverse in [False,True]:
        base=next(r for r in records if r['id']=='before8' and r['reverse']==reverse)
        proof=base['coverage'][0];key=proof['key'];side='AB'[key['side']];name=f"p{key['page']}-O-{side}.png"
        original=image(folder/base['run']/name);allowed=np.zeros(original.shape[:2],bool)
        allowed[:proof['header_end']]=True;allowed[proof['footer_start']:]=True
        for b in proof['bands']:allowed[b['top']:b['bottom']]=True
        assert not (np.any(original!=255,axis=2)&~allowed).any()
        for scenario,expected in [('residual-tone',3570),('residual-faint',30)]:
            record=next(r for r in records if r['id']==scenario and r['reverse']==reverse)
            value=image(folder/record['run']/name)
            assert np.array_equal(value[allowed],original[allowed])
            residual=np.any(value!=255,axis=2)&~allowed
            assert int(residual.sum())==expected
            controls.append(dict(run=record['run'],nonwhite_residual_pixels=expected,
                geometry_from='separate before8 control; not a product proof',pixels_sha256=hashlib.sha256(value.tobytes()).hexdigest()))
    result['separate_residual_controls']=controls
    # 既存モードの出力は追加診断を有効にしない限り不変。
    previous=folder.parent/'shared_cause_model_before.py'
    spec=importlib.util.spec_from_file_location('previous_shared_model',previous);old=importlib.util.module_from_spec(spec);spec.loader.exec_module(old)
    root=Path(__file__).resolve().parents[2];datasets=[]
    for name in ['page-flow-shared/expected.json','page-flow-multiple/aggregation.json','page-flow-unpaired/expectations.json']:
        datasets.extend(json.loads((root/'tests/ReportDiff.Tests/Fixtures'/name).read_text()))
    datasets.extend(records)
    assert all(evaluate(r['input'])==old.evaluate(r['input']) for r in datasets)
    result['default_model_equal']=len(datasets)
    (folder/'boundary-audit.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result))
