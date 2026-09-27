#!/usr/bin/env python3
"""固定した末尾ページの32方向と旧CLI互換を実プロセスで確認する。ブラウザー受け入れは行わない。"""
import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import shutil
import subprocess
import sys
from urllib.parse import unquote, urlsplit

import cv2
import numpy as np

root = Path(__file__).resolve().parents[2]
folder = Path(sys.argv[1]).resolve()
mode = sys.argv[2] if len(sys.argv) > 2 else 'fixed'
current = root / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
baseline = folder / 'baseline-cli/reportdiff.dll'
diagnosis = root / 'out/t3-1c-unpaired-components/diagnosis'
fixtures = root / 'tests/ReportDiff.Tests/Fixtures'
output = folder / mode
assert not output.exists(), ('出力は新規ディレクトリ', output)
output.mkdir(parents=True)
metrics = dict(processes=0, identical_files=0, raw_png_pairs=0, surface_images=0, static_links=0)
records = []


def compare(binary, a, b, destination, enabled=True, yaml='', args=(), command='compare'):
    config = destination.with_suffix('.yaml')
    config.write_text('rows: {enabled: true, carry_enabled: ' + str(enabled).lower() + '}\nreport: {raw_overlay: true}\n' + yaml)
    completed = subprocess.run(['dotnet', str(binary), command, str(a), str(b), '--out', str(destination),
                                '--config', str(config), '--quiet', *args], cwd=root, capture_output=True, text=True, timeout=180)
    destination.with_suffix('.log').write_text(completed.stdout + completed.stderr)
    assert completed.returncode == 1 and not completed.stderr, (destination, completed.returncode, completed.stderr)
    metrics['processes'] += 1
    return json.loads((destination / ('result.json' if command == 'compare' else 'index.json')).read_text())


def normalized(path):
    data = path.read_bytes()
    if path.suffix == '.json':
        value = json.loads(data)
        value.pop('generated_at', None)
        return json.dumps(value, sort_keys=True).encode()
    if path.name.endswith('.html'):
        # 生成日時の1項目だけを置換し、他の文言・空白・リンクはそのまま比較する。
        import re
        return re.sub(rb'\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(?:\.\d+)? ?(?:Z|[+-]\d\d:\d\d)', b'TIMESTAMP', data)
    return data


def same_directory(old, new):
    old_files = {p.relative_to(old) for p in old.rglob('*') if p.is_file()}
    new_files = {p.relative_to(new) for p in new.rglob('*') if p.is_file()}
    assert old_files == new_files, (old, 'ファイル集合', old_files ^ new_files)
    for relative in sorted(old_files):
        assert normalized(old / relative) == normalized(new / relative), (old, relative, '日時以外の互換')
    metrics['identical_files'] += len(old_files)


def same_raw(old_dir, old, new_dir, new):
    for before, after in zip(old['pages'], new['pages']):
        for a, b in [(before['raw_evidence'][s]['image'], after['raw_evidence'][s]['image']) for s in 'ab'] + [
                     (before['raw_evidence']['overlay'], after['raw_evidence']['overlay'])]:
            assert (old_dir / a).read_bytes() == (new_dir / b).read_bytes(), (new_dir, 'raw evidence')
            metrics['raw_png_pairs'] += 1


class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = []
        self.links = []

    def handle_starttag(self, tag, attrs):
        for key, value in attrs:
            if key == 'id':
                self.ids.append(value)
            if key in ('src', 'href'):
                self.links.append(value)


def html(path):
    parser = Links()
    parser.feed(path.read_text())
    assert len(parser.ids) == len(set(parser.ids)), (path, 'ID重複')
    for link in parser.links:
        url = urlsplit(link)
        assert not url.scheme and not url.netloc, (path, '外部リソース', link)
        target = path.parent / unquote(url.path) if url.path else path
        assert target.exists(), (path, link)
        if url.fragment:
            if target == path:
                ids = parser.ids
            else:
                other = Links()
                other.feed(target.read_text())
                ids = other.ids
            assert unquote(url.fragment) in ids, (path, link)
        metrics['static_links'] += 1


def image(path):
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), cv2.IMREAD_COLOR)


def save():
    (output / 'verification.json').write_text(json.dumps(dict(metrics=metrics, records=records,
        actual_browser=False, javascript_disabled=False, narrow_viewport=False), ensure_ascii=False, indent=2) + '\n')


if mode == 'fixed':
    data = json.loads((diagnosis / 'unpaired-components.json').read_text())
    for item in data:
        name = item['run']
        native = 'ba' if item['reverse'] else 'ab'
        a, b = [diagnosis / 'inputs' / item['id'] / (s + '.pdf') for s in native]
        for side, path in zip(native, (a, b)):
            assert path.read_bytes() == (fixtures / 'page-flow-unpaired' / item['id'] / (side + '.pdf')).read_bytes()
        off_dir, on_dir = output / (name + '-off'), output / (name + '-on')
        off = compare(current, a, b, off_dir, False)
        on = compare(current, a, b, on_dir)
        expected = item['candidate']
        assert on['summary']['difference_count'] == expected['difference_count']
        assert on['summary']['aggregated_difference_count'] == expected['aggregated_difference_count']
        assert on['page_flow']['aggregation']['status'] == expected['status']
        assert not on['summary']['difference_count_complete'] and not on['summary']['aggregated_difference_count_complete']
        same_directory(diagnosis / name / 'off', off_dir)
        changed = item['expected']['grouped'] and item['id'] not in ('single-r11', 'single-chain')
        if not changed:
            same_directory(diagnosis / name / 'on', on_dir)
        else:
            assert on['page_flow']['status'] == 'applied'
            assert len(on['page_flow']['aggregation']['groups']) == 2
            nonflow = [l for l in on['page_flow']['links'] if l.get('nonflow')]
            assert [link['id'] for link in nonflow] == [proof['candidate_index'] + 1 for proof in item['nonflow']]
            for group in on['page_flow']['aggregation']['groups']:
                for reference in group['structures']:
                    page = next(p for p in on['pages'] if p['page'] == reference['page'])
                    assert any(s['id'] == reference['structural_change_id'] for s in page['row_alignment']['structural_changes'])
                for band in group['auxiliary_bands']:
                    assert not band['structures'] and band['image']
                    page = next(p for p in on['pages'] if p['page'] == band['page'])
                    assert page['status'].startswith('only_in_')
        if on['page_flow']['status'] == 'applied':
            for projection in item['projections']:
                page = next(p for p in on['pages'] if p['page'] == projection['page'])
                assert page['raw_pixels'] == projection['raw_pixels']
                assert len(page['clusters']) == len(projection['clusters'])
                for surface, key in [('C', 'content_'), ('D', '')]:
                    for side in 'ab':
                        assert np.array_equal(image(on_dir / page['images'][key + side]),
                            image(diagnosis / name / f"p{page['page']}-{surface}-{side.upper()}.png")), (name, surface, side)
                        metrics['surface_images'] += 1
        for page in on['pages']:
            if page['status'].startswith('only_in_'):
                assert not page['clusters'] and not page['row_alignment']['structural_changes'] and not page['difference_count_complete']
        same_raw(off_dir, off, on_dir, on)
        html(on_dir / 'report.html')
        records.append(dict(run=name, changed=changed, total=on['summary']['difference_count'], aggregate=on['summary']['aggregated_difference_count']))
        print(name, records[-1]['total'], records[-1]['aggregate'], flush=True)
        save()
elif mode == 'compat':
    choices = {
        'page-flow': ['R10', 'R11', 'chain3', 'flow-number-change', 'two-inserts'],
        'page-flow-multiple': ['independent-inserts', 'independent-opposite', 'independent-chain', 'neutral-between', 'nonflow-terminal-tone'],
        'page-flow-shared': ['shared-two', 'shared-tone', 'shared-chain', 'independent-and-shared', 'shared-and-independent'],
        'page-flow-numeric': ['terminal-number', 'multiple-final', 'carry-number', 'weak-actual-support'],
        'page-flow-global': ['down', 'up', 'unpaired', 'horizontal'],
        'page-flow-same-page-support': ['same-page-two', 'carry-tone', 'before-short'],
        'page-flow-anchored-acceptance': ['tone', 'context', 'second-cluster-limit'],
    }
    for family, names in choices.items():
        for name in names:
            for reverse in (False, True):
                tag = family + '-' + name + ('-ba' if reverse else '-ab')
                a, b = [fixtures / family / name / (s + '.pdf') for s in ('ba' if reverse else 'ab')]
                old_dir, new_dir = output / (tag + '-old'), output / (tag + '-new')
                yaml = 'align: {enabled: true}\n' if family == 'page-flow-global' else ''
                old = compare(baseline, a, b, old_dir, yaml=yaml)
                new = compare(current, a, b, new_dir, yaml=yaml)
                same_directory(old_dir, new_dir)
                same_raw(old_dir, old, new_dir, new)
                records.append(dict(run=tag, total=new['summary']['difference_count'], aggregate=new['summary']['aggregated_difference_count']))
                print(tag, '日時以外一致', flush=True)
                save()
elif mode == 'directory':
    for side in 'ab':
        directory = output / side
        directory.mkdir()
        for name in ['independent-terminal', 'independent-tone', 'independent-faint']:
            shutil.copyfile(fixtures / 'page-flow-unpaired' / name / (side + '.pdf'), directory / (name + '.pdf'))
    target = output / 'report'
    index = compare(current, output / 'a', output / 'b', target, command='compare-dir')
    assert index['summary']['compared'] == 3 and index['summary']['different'] == 3 and index['summary']['error'] == 0
    expected = {'independent-terminal.pdf': (11, 2, 0), 'independent-tone.pdf': (12, 3, 1), 'independent-faint.pdf': (19, 19, 19)}
    for entry in index['files']:
        child = json.loads((target / entry['json']).read_text())
        assert entry['comparison'] == child['summary']
        total, aggregate, clusters = expected[entry['relative_path']]
        assert (child['summary']['difference_count'], child['summary']['aggregated_difference_count'], child['summary']['clusters']) == (total, aggregate, clusters)
        assert not child['summary']['difference_count_complete'] and not child['summary']['aggregated_difference_count_complete']
    for page in target.rglob('*.html'):
        html(page)
    records.append(dict(index=index))
    save()
else:
    raise SystemExit('mode: fixed / compat / directory')
print(json.dumps(metrics), flush=True)
