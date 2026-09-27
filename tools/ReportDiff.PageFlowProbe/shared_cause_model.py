"""同符号の複数原因を、実構造IDを分割せず送り成分全体で検証する独立モデル。"""

class Rejected(ValueError):
    pass

def require(condition, reason):
    if not condition:
        raise Rejected(reason)

def refkey(s):
    r = s['reference'] if 'reference' in s else s
    return (r['page'], r['structural_change_id'])

def band(rows):
    r = rows[0]
    return dict(page=dict(side=r['side'], page=r['page']), top=r['start'],
                height=sum(x['length'] for x in rows), bottom=rows[-1]['start']+rows[-1]['length'])

def inside(b, r):
    return b and (r['side'], r['page']) == (b['page']['side'], b['page']['page']) and b['top'] <= r['start'] and r['start']+r['length'] <= b['bottom']

def tiled(b, rows):
    return bool(rows) and band(rows) == b and all(x['start']+x['length']==y['start'] for x,y in zip(rows,rows[1:]))

def evaluate(inp, terminal=False):
    total = sum(p['difference_count'] for p in inp['pages'])
    complete = not inp['selection_limited'] and all(p['paired'] and p['complete'] for p in inp['pages'])
    try:
        components = prove(inp, terminal)
        assigned = [tuple(r) for c in components for r in c['structures']]
        actual = [refkey(s) for p in inp['pages'] for s in p['structures']]
        require(len(assigned)==len(set(assigned)) and set(assigned)==set(actual), 'membership_not_unique')
        count = sum(len(c['causes']) for c in components)
        require(count > 0, 'missing_causes')
        return dict(status='grouped', reason=None, difference_count=total,
                    aggregated_difference_count=total-len(assigned)+count,
                    difference_count_complete=complete, aggregated_difference_count_complete=complete,
                    components=components)
    except Rejected as e:
        return dict(status='skipped', reason=str(e), difference_count=total,
                    aggregated_difference_count=total, difference_count_complete=complete,
                    aggregated_difference_count_complete=complete, components=[])

def prove(inp, terminal=False):
    require(inp['gate_ready'], 'document_gate_not_ready')
    require(not inp['selection_limited'], 'selection_limited')
    pages = sorted(inp['pages'], key=lambda p:p['number'])
    rows = sorted(inp['rows'], key=lambda r:(r['side'],r['page'],r['start']))
    require(len(pages)<=128 and len(rows)<=32768 and len(inp['links'])<=128 and sum(len(r['text']) for r in rows)<=2097152, 'resource_limit')
    require([p['number'] for p in pages]==list(range(1,len(pages)+1)), 'page_sequence')
    if terminal:
        require(len(pages)>=3 and all(p['paired'] for p in pages[:-1]) and not pages[-1]['paired'], 'terminal_page_shape')
        require(pages[-1]['unpaired_covered'] and not pages[-1]['structures'] and pages[-1]['clusters']==pages[-1]['difference_count']==0, 'unpaired_residual')
    else:
        require(all(p['paired'] for p in pages), 'unpaired_page')
    require(all(r['side'] in (0,1) and 1<=r['page']<=len(pages) and r['start']>=0 and r['length']>0 and r['start']+r['length']<=2147483647 for r in rows), 'row_bounds')
    ordered = [[r for r in rows if r['side']==side] for side in (0,1)]
    require(all(v and len({r['text'] for r in v})==len(v) for v in ordered), 'ambiguous_text')
    lookup = [{r['text']:r for r in v} for v in ordered]
    require([r['text'] for r in ordered[0] if r['text'] in lookup[1]]==[r['text'] for r in ordered[1] if r['text'] in lookup[0]], 'reordered_text')
    for side in (0,1):
        for p in pages:
            rr=[r for r in ordered[side] if r['page']==p['number']]
            require(all(x['start']+x['length']<=y['start'] for x,y in zip(rr,rr[1:])), 'overlapping_rows')
    structures = [s for p in pages for s in p['structures']]
    require(len({refkey(s) for s in structures})==len(structures), 'duplicate_structure')
    for p in pages:
        require(p['clusters']>=0 and p['difference_count']==p['clusters']+len(p['structures']), 'invalid_count')
        for s in p['structures']:
            require(not s['excluded'], 'excluded_structure')
            require(s['reference']['page']==p['number'] and s['reference']['structural_change_id']>0, 'structure_reference')
            require(s['kind'] in ('inserted','deleted','block_moved'), 'unsupported_structure')
            require((s['a'] is None)==(s['kind']=='inserted') and (s['b'] is None)==(s['kind']=='deleted'), 'structure_endpoints')
            for side,b in enumerate([s['a'],s['b']]):
                if b:
                    require(b['page']==dict(side=side,page=p['number']) and b['height']>0 and b['top']>=0 and b['bottom']==b['top']+b['height'], 'structure_bounds')
    links = inp['links']
    require(links, 'missing_links')
    for l in links:
        require(l['status']=='band_verified' and l['reason'] is None and l['source'] and l['target'], 'unverified_link')
        x,y=l['source'],l['target']
        require(x['page']['side'] in (0,1) and y['page']['side']==1-x['page']['side'] and 1<=x['page']['page']<len(pages) and y['page']['page']==x['page']['page']+1 and x['height']==y['height'], 'link_direction')
    require(len({l['source']['page']['page'] for l in links})==len(links), 'duplicate_boundary')
    boundary={l['source']['page']['page']:l for l in links}
    blocks=[]
    for p in pages:
        if not blocks or p['number']-1 not in boundary: blocks.append([])
        blocks[-1].append(p['number'])
    component={n:i for i,block in enumerate(blocks) for n in block}
    require(all(component[r['page']]==component[lookup[1][r['text']]['page']] for r in ordered[0] if r['text'] in lookup[1]), 'crosses_missing_link')
    if terminal: require(len(blocks)==1, 'not_one_chain')
    result=[]
    for numbers in blocks:
        local=[[r for r in v if r['page'] in numbers] for v in ordered]
        ss=sorted([s for s in structures if s['reference']['page'] in numbers],key=refkey)
        ll=sorted([l for l in links if l['source']['page']['page'] in numbers],key=lambda l:l['source']['page']['page'])
        if not ll:
            require(not ss and [(r['page'],r['start'],r['length'],r['text']) for r in local[0]]==[(r['page'],r['start'],r['length'],r['text']) for r in local[1]], 'nonneutral_page')
            continue
        lengths={r['length'] for v in local for r in v}
        require(len(lengths)==1, 'nonuniform_height')
        pitch=next(iter(lengths)); starts=[]
        for n in numbers:
            vv=[[r for r in v if r['page']==n] for v in local]
            if terminal and n==len(pages):
                require(sum(bool(v) for v in vv)==1, 'terminal_body_shape')
                require(all(all(y['start']-x['start']==pitch for x,y in zip(v,v[1:])) for v in vv), 'nontiled_body')
                starts.extend(v[0]['start'] for v in vv if v)
                continue
            require(all(v for v in vv), 'missing_body')
            require(all(all(y['start']-x['start']==pitch for x,y in zip(v,v[1:])) for v in vv), 'nontiled_body')
            require(vv[0][0]['start']==vv[1][0]['start'], 'body_origin')
            starts.extend(v[0]['start'] for v in vv)
            if n!=numbers[-1]: require(len(vv[0])==len(vv[1]), 'intermediate_capacity')
        require(len(set(starts))==1, 'nonuniform_body_origin')
        extras=[[r for r in v if r['text'] not in lookup[1-side]] for side,v in enumerate(local)]
        require(bool(extras[0])!=bool(extras[1]), 'mixed_or_missing_causes')
        sign=1 if extras[1] else -1; expanded_side=1 if sign==1 else 0
        expanded=local[expanded_side]; base=local[1-expanded_side]
        if terminal:
            require(not any(r['page']==len(pages) for r in base) and any(r['page']==len(pages) for r in expanded), 'terminal_side')
        require(all(l['source']['page']['side']==1-expanded_side for l in ll), 'mixed_flow_direction')
        # 共通行を区切りとして原因帯を列挙。実構造IDに一致しない帯を分割しない。
        runs=[]
        for i,r in enumerate(expanded):
            if r['text'] in lookup[1-expanded_side]: continue
            if not runs or runs[-1][-1]+1!=i: runs.append([])
            runs[-1].append(i)
        causes=[]; active={}; seen=[]
        for indices in runs:
            rr=[expanded[i] for i in indices]
            require(len({r['page'] for r in rr})==1, 'cause_crosses_page')
            b=band(rr)
            found=[s for s in ss if s['kind']==('inserted' if sign==1 else 'deleted') and s['b' if sign==1 else 'a']==b]
            require(len(found)==1, 'cause_structure_not_unique')
            causes.append(dict(reference=list(refkey(found[0])), band=b, rows=[r['text'] for r in rr], delta=sign*b['height'], first_index=indices[0], last_index=indices[-1]))
        for i,r in enumerate(expanded):
            seen.extend(c for c in causes if c['last_index']==i)
            if r['text'] in lookup[1-expanded_side]: active[r['text']]=list(seen)
        def signature(rr):
            values=[tuple(tuple(c['reference']) for c in active[r['text']]) for r in rr]
            require(values and len(set(values))==1, 'nonuniform_contributors')
            return [list(v) for v in values[0]]
        def within(b): return sorted([r for r in rows if inside(b,r)],key=lambda r:r['start'])
        assigned={tuple(c['reference']) for c in causes}; flows=[]; crossed=set(); auxiliaries=[]
        for l in ll:
            source,target=l['source'],l['target']; a,b=within(source),within(target)
            require(tiled(source,a) and tiled(target,b) and [r['text'] for r in a]==[r['text'] for r in b]==l['text'], 'flow_text_or_tiling')
            n=source['page']['page']; incoming=[c for c in causes if c['band']['page']['page']<=n]
            require(source['height']==sum(c['band']['height'] for c in incoming) and len(a)*pitch==source['height'], 'cumulative_flow_height')
            require(source['bottom']==max(r['start']+pitch for r in base if r['page']==n) and target['top']==min(r['start'] for r in expanded if r['page']==n+1), 'flow_endpoints')
            require(signature(a)==[c['reference'] for c in incoming], 'flow_contributors')
            require(not crossed.intersection(r['text'] for r in a), 'duplicate_crossing')
            crossed.update(r['text'] for r in a)
            refs=[]; flow_auxiliary=[]
            for endpoint in (source,target):
                if terminal and endpoint['page']['page']==len(pages):
                    rr=[r for r in expanded if r['page']==len(pages)]
                    require(endpoint==target and tiled(endpoint,rr) and endpoint['page']['side']==expanded_side, 'terminal_endpoint')
                    require(not auxiliaries, 'duplicate_terminal_endpoint')
                    auxiliaries.append(endpoint);flow_auxiliary.append(endpoint)
                    continue
                found=[s for s in ss if s['kind']==('deleted' if endpoint['page']['side']==0 else 'inserted') and s['a' if endpoint['page']['side']==0 else 'b']==endpoint]
                require(len(found)==1 and refkey(found[0]) not in assigned, 'endpoint_membership')
                assigned.add(refkey(found[0]));refs.append(list(refkey(found[0])))
            flow=dict(boundary=n, structures=refs, causes=[c['reference'] for c in incoming], rows=[r['text'] for r in a], height=source['height'])
            if terminal: flow['auxiliary_bands']=flow_auxiliary
            flows.append(flow)
        expected_crossed={r['text'] for r in base if r['page']!=lookup[expanded_side][r['text']]['page']}
        require(crossed==expected_crossed, 'crossed_membership')
        moved=set(); movements=[]
        for s in ss:
            if refkey(s) in assigned: continue
            require(s['kind']=='block_moved', 'unexplained_structure')
            a,b=within(s['a']),within(s['b'])
            require(tiled(s['a'],a) and tiled(s['b'],b) and [r['text'] for r in a]==[r['text'] for r in b], 'movement_tiling')
            require(all(r['text'] in active for r in a), 'movement_unknown_row')
            contributors=signature(a); delta=sum(c['delta'] for c in causes if c['reference'] in contributors)
            require(delta!=0 and s['dy']==delta==s['b']['top']-s['a']['top'], 'cumulative_movement')
            require(not moved.intersection(r['text'] for r in a), 'duplicate_movement')
            moved.update(r['text'] for r in a);assigned.add(refkey(s))
            movements.append(dict(structure=list(refkey(s)), causes=contributors, dy=delta, rows=[r['text'] for r in a]))
        expected_moved={r['text'] for r in base if r['page']==lookup[expanded_side][r['text']]['page'] and r['start']!=lookup[expanded_side][r['text']]['start']}
        require(moved==expected_moved, 'moved_membership')
        require(assigned=={refkey(s) for s in ss}, 'uncovered_structure')
        balance=[]
        for n in numbers:
            na=sum(r['page']==n for r in local[0]);nb=sum(r['page']==n for r in local[1])
            cause=sign*sum(len(c['rows']) for c in causes if c['band']['page']['page']==n)
            inc=sign*sum(len(f['rows']) for f in flows if f['boundary']+1==n)
            out=sign*sum(len(f['rows']) for f in flows if f['boundary']==n)
            require(nb-na==cause+inc-out, 'page_balance')
            balance.append(dict(page=n,rows_a=na,rows_b=nb,cause_delta=cause,incoming=inc,outgoing=out))
        value=dict(pages=numbers,causes=causes,structures=[list(r) for r in sorted(assigned)],movements=movements,flows=flows,balance=balance)
        if terminal:
            require(len(auxiliaries)==1, 'missing_terminal_endpoint')
            value['auxiliary_bands']=auxiliaries
        result.append(value)
    return result
