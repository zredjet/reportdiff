#!/usr/bin/env python3
"""V-02: 保存済みSkia 34条件を現在の実CLIで再実行し、元期待・旧出力・raw画像を照合する。"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / 'out/t3-1c-probe'
PREVIOUS = ROOT / 'out/t3-1c-cli/matrix-final'
CLI = ROOT / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'
SETS = [('skia', 'skia-fixtures', 'skia-candidates', 'aggregate-skia'),
        ('skia-local', 'skia-local-fixtures', 'skia-local-candidates', 'aggregate-skia-local')]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(path):
    return json.loads(path.read_text())


def check_hashes(hashes):
    for path, expected in hashes.items():
        assert (ROOT / path).is_file() and digest(ROOT / path) == expected, ('保存根拠の変更・欠落', path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path, help='未使用の出力ディレクトリ')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    old_record = load(ROOT / 'docs/verification/t3-1c-cli-integration.json')
    source = load(ROOT / 'out/t3-1c-unpaired-shared-integration/source-tested.json')
    check_hashes(source)
    hashes = {p: h for p, h in old_record['inputs']['pdf_sha256'].items() if '/skia-' in p}
    assert len(hashes) == 30
    pdf_hashes = dict(hashes)
    hashes.update({p: h for p, h in old_record['inputs']['independent_results_sha256'].items()
                   if '/skia-' in p or '/aggregate-skia' in p})
    assert len(hashes) == 36
    check_hashes(hashes)
    binary = {str(p.relative_to(ROOT)): digest(p) for p in sorted(CLI.parent.rglob('*')) if p.is_file()}
    spec = importlib.util.spec_from_file_location('skia_compat', ROOT / 'tools/verify-pagemap-compatibility.py')
    compat = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(compat)
    historical = {(r['dataset'], r['run']): r for r in old_record['cli']['records']}
    plans = []
    baseline = {}
    for label, inputs, candidates, aggregation in SETS:
        oracles = {r['run']: r['decision'] for r in load(BASE / aggregation / 'aggregation.json')['records']}
        for saved in load(BASE / candidates / 'candidates.json'):
            name = f"{label}-{saved['folder']}"
            paths = [BASE / inputs / saved['id'] / f'{s}.pdf' for s in ('ba' if saved['reverse'] else 'ab')]
            for path in paths:
                assert str(path.relative_to(ROOT)) in pdf_hashes
            for enabled in (False, True):
                key = name + '-' + str(enabled).lower()
                directory = PREVIOUS / key
                actual_files = {str(p.relative_to(directory)) for p in directory.rglob('*') if p.is_file()}
                snapshot = compat.snapshot(directory)
                assert snapshot and set(snapshot) == actual_files, ('旧出力のファイル集合', key)
                if enabled:
                    old = historical[label, saved['folder']]
                    assert digest(directory / 'result.json') == old['result_sha256']
                    assert digest(directory / 'report.html') == old['html_sha256']
                baseline[key] = snapshot
            plans.append((label, name, paths, saved, oracles[saved['folder']]))
    assert len(plans) == 34 and len(baseline) == 68
    (output / 'preflight.json').write_text(json.dumps(dict(source_sha256=source, input_and_oracle_sha256=hashes,
        cli_files_sha256=binary, normalized_old_output_sha256=baseline), indent=2) + '\n')
    configs = {}
    for enabled in (False, True):
        configs[enabled] = output / f'carry-{str(enabled).lower()}.yaml'
        configs[enabled].write_text('rows: {enabled: true, carry_enabled: ' + str(enabled).lower()
                                   + '}\nreport: {raw_overlay: true}\n')
    metrics = dict(conditions=0, processes=0, pdfs=30, applied=0, skipped=0, grouped=0,
                   identical_output_files=0, raw_png_pairs=0, fallback_page_dtos=0, selected_conditions=0)
    records = []
    result = dict(scope='V-02_skia_existing_corpus', metrics=metrics, records=records,
                  timestamp_only_normalization=True, full_tests_rerun=False,
                  browser_executed=False, windows_executed=False, completed=False)
    for label, name, paths, saved, oracle in plans:
        reports = {}
        snapshots = {}
        run_seconds = {}
        for enabled in (False, True):
            key = name + '-' + str(enabled).lower()
            destination = output / key
            command = ['dotnet', str(CLI), 'compare', *map(str, paths), '--out', str(destination),
                       '--config', str(configs[enabled]), '--quiet']
            if 'selected' in saved:
                command += ['--pages', ','.join(map(str, saved['selected']))]
            start = time.monotonic()
            process = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=180)
            run_seconds[str(enabled).lower()] = time.monotonic() - start
            (output / f'{key}.log').write_text(process.stdout + process.stderr)
            assert process.returncode == 1 and not process.stderr, (key, process.returncode, process.stdout, process.stderr)
            metrics['processes'] += 1
            actual = compat.snapshot(destination)
            assert set(actual) == {str(p.relative_to(destination)) for p in destination.rglob('*') if p.is_file()}
            differences = sorted(p for p in set(actual) | set(baseline[key]) if actual.get(p) != baseline[key].get(p))
            if differences:
                (output / 'mismatch.json').write_text(json.dumps(dict(condition=key, files=differences), indent=2) + '\n')
            assert not differences, (key, '日時以外の出力変更', differences)
            metrics['identical_output_files'] += len(actual)
            snapshots[key] = actual
            reports[enabled] = (destination, load(destination / 'result.json'))
        off_dir, off = reports[False]
        on_dir, on = reports[True]
        flow = on['page_flow']
        assert off.get('page_flow') is None and off['summary']['aggregated_difference_count'] is None
        assert (flow['status'] == 'applied') == saved['result']['gate']['ready'], name
        for key in ('difference_count', 'aggregated_difference_count', 'aggregated_difference_count_complete'):
            assert on['summary'][key] == oracle[key], (name, key)
        # 既存summaryは比較対象ページ内の網羅性、独立集約は選択外も含めた文書の網羅性。
        # ページ選択時に同じ値になるとは仮定せず、それぞれの契約を確認する。
        assert on['summary']['difference_count_complete'] == all(p['difference_count_complete'] for p in on['pages'])
        assert flow['aggregation']['difference_count_complete'] == oracle['difference_count_complete']
        assert flow['selection_limited'] == ('selected' in saved)
        assert flow['aggregation']['status'] == oracle['status']
        assert len(flow['links']) == len(saved['result']['links'])
        for before, after in zip(off['pages'], on['pages'], strict=True):
            assert before['page'] == after['page']
            for old_image, new_image in [(before['raw_evidence'][s]['image'], after['raw_evidence'][s]['image']) for s in 'ab'] + [
                    (before['raw_evidence']['overlay'], after['raw_evidence']['overlay'])]:
                assert (off_dir / old_image).read_bytes() == (on_dir / new_image).read_bytes(), (name, 'raw画像')
                metrics['raw_png_pairs'] += 1
            if flow['status'] != 'applied':
                assert before == after, (name, '見送り時のページ結果')
                metrics['fallback_page_dtos'] += 1
        for link in flow['links']:
            for endpoint in (link['source'], link['target']):
                if endpoint is None:
                    continue
                assert endpoint['coordinate_system'] == 'original_top_left'
                assert (on_dir / endpoint['image']).is_file()
                for reference in endpoint['structures']:
                    page = next(p for p in on['pages'] if p['page'] == reference['page'])
                    assert any(s['id'] == reference['structural_change_id'] for s in page['row_alignment']['structural_changes'])
        metrics['conditions'] += 1
        metrics['applied' if flow['status'] == 'applied' else 'skipped'] += 1
        metrics['grouped'] += int(flow['aggregation']['status'] == 'grouped')
        metrics['selected_conditions'] += int('selected' in saved)
        records.append(dict(dataset=label, run=saved['folder'], selected=saved.get('selected'), status=flow['status'],
                            reasons=flow['reasons'], summary=on['summary'], process_seconds=run_seconds,
                            normalized_output_sha256=snapshots))
        (output / 'verification.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
        print(f"{metrics['conditions']}/34 {name}: {flow['status']} "
              f"{on['summary']['difference_count']}→{on['summary']['aggregated_difference_count']}", flush=True)
    check_hashes(source)
    check_hashes(hashes)
    check_hashes(binary)
    assert metrics['processes'] == 68 and metrics['conditions'] == 34
    assert (metrics['applied'], metrics['skipped'], metrics['grouped'], metrics['selected_conditions']) == (6, 28, 6, 4)
    result['completed'] = True
    result['source_files_unchanged'] = len(source)
    result['input_and_oracle_files_unchanged'] = len(hashes)
    result['cli_files_unchanged'] = len(binary)
    (output / 'verification.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(metrics, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    main()
