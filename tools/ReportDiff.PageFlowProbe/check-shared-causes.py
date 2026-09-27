#!/usr/bin/env python3
"""実PDFから保存したC/D・編集列・共有構造を、製品とは独立に監査する。"""
import copy,difflib,hashlib,importlib.util,json,random,sys
from pathlib import Path
import cv2,numpy as np
from shared_cause_model import evaluate,refkey
root=Path(__file__).resolve().parents[2];folder=Path(sys.argv[1]).resolve()
spec=importlib.util.spec_from_file_location('shared_reference',root/'reference/prototype.py')
ref=importlib.util.module_from_spec(spec);sys.modules[spec.name]=ref;spec.loader.exec_module(ref)
records=json.loads((folder/'shared-causes.json').read_text());manifest=json.loads((folder/'fixtures.json').read_text())
metrics=dict(runs=0,grouped=0,unmet_positive_hypotheses=0,unexpected_negative_acceptances=0,reference_pages=0,surface_images=0,raw_pngs=0,structures=0,shared_movements=0,permutations=0,rejections=0,completeness=0,content_preserved=0)

def oracle(inp,decision):
    # 候補リンクやprefix走査を使わず、共通行のページ移動と編集列から原因を算出する。
    aa=sorted([r for r in inp['rows'] if r['side']==0],key=lambda r:(r['page'],r['start']))
    bb=sorted([r for r in inp['rows'] if r['side']==1],key=lambda r:(r['page'],r['start']))
    ax={r['text']:r for r in aa};bx={r['text']:r for r in bb}
    edges={p['number']:set() for p in inp['pages']}
    for text in ax.keys()&bx.keys():
        x,y=ax[text]['page'],bx[text]['page'];edges[x].add(y);edges[y].add(x)
    remaining=set(edges);blocks=[]
    while remaining:
        todo=[min(remaining)];block=set()
        while todo:
            n=todo.pop()
            if n not in block: block.add(n);todo+=list(edges[n]-block)
        remaining-=block
        if len(block)>1: blocks.append(block)
    assert [sorted(g) for g in blocks]==[c['pages'] for c in decision['components']]
    for block,c in zip(blocks,decision['components']):
        a=[r for r in aa if r['page'] in block];b=[r for r in bb if r['page'] in block]
        edits=[op for op in difflib.SequenceMatcher(a=[r['text'] for r in a],b=[r['text'] for r in b],autojunk=False).get_opcodes() if op[0]!='equal']
        assert len(edits)==len(c['causes'])
        ids=[]
        for (tag,i,j,k,l),cause in zip(edits,c['causes']):
            assert tag in ('insert','delete')
            rr=b[k:l] if tag=='insert' else a[i:j]
            assert cause['rows']==[r['text'] for r in rr]
            ss=[s for p in inp['pages'] for s in p['structures'] if refkey(s)==tuple(cause['reference'])]
            assert len(ss)==1 and ss[0]['kind']==('inserted' if tag=='insert' else 'deleted')
            ids.append(cause['reference'])
        ai={r['text']:i for i,r in enumerate(a)};bi={r['text']:i for i,r in enumerate(b)}
        for m in c['movements']:
            expected=[]
            for text in m['rows']:
                pitch=ax[text]['length'];n=ax[text]['page']
                offset=sum(r['page']<n for r in b)-sum(r['page']<n for r in a)
                assert m['dy']==(bi[text]-ai[text]-offset)*pitch==bx[text]['start']-ax[text]['start']
                contributors=[ident for op,ident in zip(edits,ids) if (op[4]<=bi[text] if op[0]=='insert' else op[2]<=ai[text])]
                expected.append(contributors)
            assert all(v==m['causes'] for v in expected)
        for f in c['flows']:
            crossing=[text for text in ax.keys()&bx.keys() if {ax[text]['page'],bx[text]['page']}=={f['boundary'],f['boundary']+1}]
            assert set(f['rows'])==set(crossing)
            assert f['height']==sum(ax[text]['length'] for text in crossing)
    assert decision['aggregated_difference_count']==sum(p['clusters'] for p in inp['pages'])+sum(len(c['causes']) for c in decision['components'])

def audit(inp,expected):
    def reject(v):
        result=evaluate(v)
        assert result['status']=='skipped' and result['components']==[] and result['aggregated_difference_count']==result['difference_count'],('unsafe mutation',v,result)
        metrics['rejections']+=1
    for seed in range(5):
        v=copy.deepcopy(inp);rng=random.Random(seed)
        for field in ['rows','links','pages']: rng.shuffle(v[field])
        for p in v['pages']: rng.shuffle(p['structures'])
        assert evaluate(v)==expected;metrics['permutations']+=1
    for field,value in [('gate_ready',False),('selection_limited',True)]:
        v=copy.deepcopy(inp);v[field]=value;reject(v)
    for i,r in enumerate(inp['rows']):
        for kind in ['remove','duplicate','position','height','text']:
            v=copy.deepcopy(inp)
            if kind=='remove': del v['rows'][i]
            elif kind=='duplicate': v['rows'].append(copy.deepcopy(r))
            elif kind=='position': v['rows'][i]['start']+=1
            elif kind=='height': v['rows'][i]['length']+=1
            else: v['rows'][i]['text']=next(x['text'] for x in inp['rows'] if x['side']==r['side'] and x['text']!=r['text'])
            reject(v)
    for i,l in enumerate(inp['links']):
        for kind in ['remove','duplicate','status','reason','source','target','text','direction']:
            v=copy.deepcopy(inp)
            if kind=='remove': del v['links'][i]
            elif kind=='duplicate': v['links'].append(copy.deepcopy(l))
            elif kind=='status': v['links'][i]['status']='candidate'
            elif kind=='reason': v['links'][i]['reason']='failed'
            elif kind=='source': v['links'][i]['source']=None
            elif kind=='target': v['links'][i]['target']['top']+=1
            elif kind=='text': v['links'][i]['text']=['ALTERED']
            else: v['links'][i]['source']['page']['side']=1-l['source']['page']['side']
            reject(v)
    for i,p in enumerate(inp['pages']):
        for kind in ['remove','duplicate','count','unpaired']:
            v=copy.deepcopy(inp)
            if kind=='remove': del v['pages'][i]
            elif kind=='duplicate': v['pages'].append(copy.deepcopy(p))
            elif kind=='count': v['pages'][i]['difference_count']+=1
            else: v['pages'][i]['paired']=False
            reject(v)
        v=copy.deepcopy(inp);v['pages'][i]['complete']=False;d=evaluate(v)
        assert d['status']=='grouped' and d['aggregated_difference_count']==expected['aggregated_difference_count'] and not d['aggregated_difference_count_complete'];metrics['completeness']+=1
        v=copy.deepcopy(inp);v['pages'][i]['clusters']+=1;v['pages'][i]['difference_count']+=1;d=evaluate(v)
        assert d['status']=='grouped' and d['aggregated_difference_count']==expected['aggregated_difference_count']+1;metrics['content_preserved']+=1
        for j,s in enumerate(p['structures']):
            kinds=['remove','duplicate','excluded','reference','side','band','kind']+(['dy'] if s['kind']=='block_moved' else [])
            for kind in kinds:
                v=copy.deepcopy(inp);page=v['pages'][i];ss=page['structures'][j]
                if kind=='remove': del page['structures'][j];page['difference_count']-=1
                elif kind=='duplicate': page['structures'].append(copy.deepcopy(s));page['difference_count']+=1
                elif kind=='excluded': ss['excluded']=True
                elif kind=='reference': ss['reference']['page']=len(inp['pages'])+1
                elif kind=='dy': ss['dy']+=1
                elif kind=='kind': ss['kind']='unknown'
                else:
                    endpoint=ss['a'] or ss['b']
                    if kind=='side': endpoint['page']['side']=1-endpoint['page']['side']
                    else: endpoint['top']+=1;endpoint['bottom']+=1
                reject(v)

def image(path,gray=False):
    return cv2.imdecode(np.frombuffer(Path(path).read_bytes(),np.uint8),cv2.IMREAD_GRAYSCALE if gray else cv2.IMREAD_COLOR)

def render(original,segments,side):
    dst=np.full((sum(s['length'] for s in segments),original.shape[1],3),255,np.uint8)
    for s in segments:
        start=s[side+'_start']
        if start is not None: dst[s['canvas_start']:s['canvas_start']+s['length']]=original[start:start+s['length']]
    return dst

results=[]
for f in manifest:
    assert hashlib.sha256((folder/f['id']/(f['side']+'.pdf')).read_bytes()).hexdigest()==f['sha256']
for run in records:
    inp=run['input'];d=evaluate(inp);metrics['runs']+=1
    if d['status']=='grouped':
        metrics['grouped']+=1;oracle(inp,d);audit(inp,d)
        metrics['shared_movements']+=sum(len(m['causes'])>1 for c in d['components'] for m in c['movements'])
    matches=(d['status']=='grouped')==run['hypothesis']['grouped'] and (d['status']!='grouped' or d['aggregated_difference_count']==run['hypothesis']['aggregate'])
    if not matches:
        if run['hypothesis']['grouped']:metrics['unmet_positive_hypotheses']+=1
        else:metrics['unexpected_negative_acceptances']+=1
    directory=folder/run['run'];report=json.loads((directory/'cli/result.json').read_text())
    for p in report['pages']:
        original=[image(directory/f"p{p['page']}-O-{side}.png") for side in ['A','B']]
        for side,im in zip(['a','b'],original):
            assert np.array_equal(im,image(directory/'cli'/p['raw_evidence'][side]['image']));metrics['raw_pngs']+=1
        # 補正・送り・マスクに依存しない、元画像からの共通色合成を独立に再計算する。
        a,b=[((im[:,:,0].astype(np.int32)*114+im[:,:,1].astype(np.int32)*587+im[:,:,2].astype(np.int32)*299+500)//1000) for im in original]
        common=np.minimum(255-a,255-b);tint=(common*204+127)//255
        raw=np.stack([a+tint,np.minimum(a,b)+tint,b+tint],axis=2).astype(np.uint8)
        assert np.array_equal(raw,image(directory/'cli'/p['raw_evidence']['overlay']));metrics['raw_pngs']+=1
    if inp['gate_ready']:
        for side in (0,1):
            expected=next(f['expected_rows'] for f in manifest if f['id']==run['id'] and f['side']==('ab'[1-side] if run['reverse'] else 'ab'[side]))
            actual=sorted([r for r in inp['rows'] if r['side']==side],key=lambda r:(r['page'],r['start']))
            assert [[r['text'] for r in actual if r['page']==n+1] for n in range(len(expected))]==expected
    for proj in run['projections']:
        n=proj['page'];ca=image(directory/f'p{n}-C-A.png');cb=image(directory/f'p{n}-C-B.png');mask=image(directory/f'p{n}-C-raw.png',True)
        compared=ref.compare_page(ca,cb,ref.Params())
        assert np.array_equal(compared.raw_mask,mask>0) and compared.raw_pixels==proj['raw_pixels'] and len(compared.clusters)==len(proj['content_clusters'])
        metrics['reference_pages']+=1
        display=image(directory/f'p{n}-D-raw.png',True);projected=np.zeros_like(display)
        for piece in proj['pieces']: projected[piece['display_start']:piece['display_start']+piece['length']]=mask[piece['content_start']:piece['content_start']+piece['length']]
        assert np.array_equal(display,projected) and np.count_nonzero(display)==np.count_nonzero(mask)
        for side in ['a','b']:
            original=image(directory/f'p{n}-O-{side.upper()}.png')
            for mode in ['C','D']:
                im=render(original,proj['content_map' if mode=='C' else 'display_map'],side)
                assert np.array_equal(im,image(directory/f'p{n}-{mode}-{side.upper()}.png'));metrics['surface_images']+=1
                if mode=='D': assert np.count_nonzero(np.any(im!=255,axis=2))==np.count_nonzero(np.any(original!=255,axis=2))
        actual=next(p for p in inp['pages'] if p['number']==n)
        assert [s['id'] for s in proj['structures']]==[s['reference']['structural_change_id'] for s in actual['structures']]
        metrics['structures']+=len(proj['structures'])
    results.append(dict(run=run['run'],hypothesis=run['hypothesis'],hypothesis_met=matches,decision=d,product_summary=run['cli']['summary'],gate=run['gate'],adoptions=run['adoptions']))
    print(run['run'],d['status'],d['aggregated_difference_count'],d['reason'],flush=True)
(folder/'shared-check.json').write_text(json.dumps(dict(metrics=metrics,records=results),indent=2)+'\n')
print(json.dumps(metrics,indent=2))
# 既存の単一原因が成立した場合はその経路を優先し、共有原因の候補だけを追加する案の再生。
def convert(old):
    inp=copy.deepcopy(old)
    def b(v):
        if v is None:return None
        return dict(page=dict(side=0 if v['side']=='a' else 1,page=v['page']),top=v['start'],height=v['length'],bottom=v['start']+v['length'])
    for r in inp['rows']: r['side']=0 if r['side']=='a' else 1
    for l in inp['links']: l['source']=b(l['source']);l['target']=b(l['target'])
    for p in inp['pages']:
        for s in p['structures']: s['a']=b(s['a']);s['b']=b(s['b'])
    return inp

legacy_records=[];source_hashes={};legacy_metrics=dict(runs=0,preserved=0,additional_grouped=0,shared_movements=0)
legacy_root=Path(sys.argv[2]) if len(sys.argv)>2 else root/'out/t3-1c-probe'
datasets=[]
for part in ['fixed','additional','skia','skia-local','causal']:
    p=legacy_root/('aggregate-'+part)/'aggregation.json';source_hashes[str(p)]=hashlib.sha256(p.read_bytes()).hexdigest()
    datasets.extend((part,r['run'],convert(r['input']),r['decision']) for r in json.loads(p.read_text())['records'])
p=root/'tests/ReportDiff.Tests/Fixtures/page-flow-multiple/aggregation.json'
source_hashes[str(p)]=hashlib.sha256(p.read_bytes()).hexdigest()
datasets.extend(('multiple',r['run'],r['input'],r['expected']) for r in json.loads(p.read_text()))
allowed={('causal',name+direction):count for name,count in [('two-inserts',2),('two-inserts-tone',3)] for direction in ['-ab','-ba']}
allowed.update({('multiple',name+direction):count for name,count in [('shared-two-inserts',2),('one-independent-one-shared',3)] for direction in ['-ab','-ba']})
for dataset,name,inp,previous in datasets:
    candidate=evaluate(inp)
    if previous['status']=='grouped':
        # 旧単一原因・片側ページ等の成功経路は候補で置換しない。
        changed=False;count=previous['aggregated_difference_count']
    else:
        changed=candidate['status']=='grouped';count=candidate['aggregated_difference_count']
        assert changed==((dataset,name) in allowed),(dataset,name,candidate)
        if changed:
            assert count==allowed[(dataset,name)]
            oracle(inp,candidate);audit(inp,candidate)
            legacy_metrics['shared_movements']+=sum(len(m['causes'])>1 for c in candidate['components'] for m in c['movements'])
        else: assert count==previous['aggregated_difference_count']
    legacy_metrics['runs']+=1;legacy_metrics['additional_grouped' if changed else 'preserved']+=1
    legacy_records.append(dict(dataset=dataset,run=name,changed=changed,before=previous['aggregated_difference_count'],after=count,candidate=candidate if changed else None))
assert metrics['unexpected_negative_acceptances']==0, metrics
(folder/'legacy-shared-check.json').write_text(json.dumps(dict(metrics=legacy_metrics,source_sha256=source_hashes,records=legacy_records),indent=2)+'\n')
(folder/'shared-check.json').write_text(json.dumps(dict(metrics=metrics,records=results),indent=2)+'\n')
print('再生結果',json.dumps(legacy_metrics));print('再生監査を含む総計',json.dumps(metrics))
