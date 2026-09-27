#!/usr/bin/env python3
"""保存された実CLI正例、上限到達時の全文書fallback、compare-dirと静的リンクを検査する。"""
from html.parser import HTMLParser
import json
from pathlib import Path
import shutil
import subprocess
import sys
from urllib.parse import unquote, urlsplit

root = Path(__file__).resolve().parents[2]
evidence = Path(sys.argv[1]).resolve()
out = evidence/'output-audit'; out.mkdir(exist_ok=False)
fixtures = root/'tests/ReportDiff.Tests/Fixtures'
cli = root/'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'

class Links(HTMLParser):
    def __init__(self):
        super().__init__(); self.ids = set(); self.links = []
    def handle_starttag(self, tag, attrs):
        for name, value in attrs:
            if name == 'id':
                assert value not in self.ids, value
                self.ids.add(value)
            if name in ('href', 'src'): self.links.append(value)

def check_links(path):
    parser = Links(); parser.feed(path.read_text()); count = 0
    for link in parser.links:
        url = urlsplit(link); assert not url.scheme and not url.netloc, link
        target = path.parent/unquote(url.path) if url.path else path
        assert target.is_file(), (path, link)
        if url.fragment:
            if target == path: ids = parser.ids
            else:
                other = Links(); other.feed(target.read_text()); ids = other.ids
            assert url.fragment in ids, (path, link)
        count += 1
    return count

def run(command):
    process = subprocess.run(['dotnet', str(cli), *map(str, command), '--quiet'], capture_output=True, text=True)
    assert process.returncode == 1, process.stderr

positive = []
for variant in ['original', 'tone', 'context']:
    for direction in ['ab', 'ba']:
        name = variant+'-'+direction; folder = evidence/f'measure/{name}-on-0'
        positive.append(dict(run=name, links=check_links(folder/'report.html')))

negative = []
for variant in ['too-different', 'cluster-limit', 'second-too-different', 'second-cluster-limit']:
    for direction in ['ab', 'ba']:
        name = variant+'-'+direction; paths = []
        for mode in ['on', 'off']:
            dest = out/(name+'-'+mode); source = fixtures/'page-flow-anchored-acceptance'/variant
            run(['compare', source/(direction[0]+'.pdf'), source/(direction[1]+'.pdf'), '--out', dest,
                '--config', evidence/f'measure/{mode}.yaml'])
            paths.append(dest)
        on, off = [json.loads((p/'result.json').read_text()) for p in paths]
        audit = on['page_flow']['anchored_content']
        assert audit['status'] == 'skipped' and audit['reason'] == 'anchored_content_incomplete'
        assert not audit['omitted_original_bands'] and 'surfaces' not in audit
        assert on['pages'] == off['pages']
        aa = {p.relative_to(paths[0]): p for p in paths[0].rglob('*.png')}
        bb = {p.relative_to(paths[1]): p for p in paths[1].rglob('*.png')}
        # 有効時だけ従来経路が保存する送り候補の元帯画像は、ページ比較の画像と分ける。
        bands = {Path(link[side]['image']) for link in on['page_flow']['links'] for side in ['source', 'target']
            if link.get(side) and link[side].get('image')}
        assert aa.keys()-bb.keys() == bands and not bb.keys()-aa.keys()
        assert all(str(p).startswith('pages/flow_l') for p in bands)
        for f in bb: assert aa[f].read_bytes() == bb[f].read_bytes(), (name, str(f))
        negative.append(dict(run=name, identical_page_png_files=len(bb), prior_flow_band_images=len(bands),
            links=check_links(paths[0]/'report.html'), summary=on['summary']))
        print(name, '上限見送り・全ページ一致', flush=True)

for side in ['a', 'b']:
    folder = out/side; folder.mkdir()
    for variant in ['original', 'tone', 'context']:
        source = fixtures/('page-flow-same-page-support/same-page-two' if variant == 'original' else 'page-flow-anchored-acceptance/'+variant)
        shutil.copyfile(source/(side+'.pdf'), folder/(variant+'.pdf'))
dest = out/'directory'
run(['compare-dir', out/'a', out/'b', '--out', dest, '--config', evidence/'measure/on.yaml'])
index = json.loads((dest/'index.json').read_text())
assert index['summary']['compared'] == index['summary']['different'] == 3 and index['summary']['error'] == 0
assert sum(p['comparison']['clusters'] for p in index['files']) == 2
assert sum(p['comparison']['difference_count'] for p in index['files']) == 20
assert sum(p['comparison']['aggregated_difference_count'] for p in index['files']) == 8
for p in index['files']:
    report = json.loads((dest/p['json']).read_text()); assert report['summary'] == p['comparison']
    assert report['page_flow']['anchored_content']['status'] == 'applied'
    check_links(dest/p['html'])
result = dict(positive=positive, incomplete=negative, directory=dict(summary=index['summary'],
    clusters=2, difference_count=20, aggregate=8, links=check_links(dest/'index.html')),
    actual_browser=False, javascript_disabled=False, narrow_viewport=False)
(out/'verification.json').write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n')
print('静的リンクとcompare-dir成立', flush=True)
