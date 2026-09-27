#!/usr/bin/env python3
"""R-02の固定A4 9条件と対照4条件を再実行し、未成立の期待も保持する。"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / 'out/t3-1c-scale'
CLI = ROOT / 'src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll'


def load(path):
    return json.loads(path.read_text())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n')


def check_hashes(hashes):
    for name, expected in hashes.items():
        assert (ROOT / name).is_file() and digest(ROOT / name) == expected, name


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path, help='未使用の出力ディレクトリ')
    args = parser.parse_args()
    if not hasattr(os, 'wait4'):
        raise RuntimeError('macOS/Linuxのwait4が必要です。')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    spec = importlib.util.spec_from_file_location('compat', ROOT / 'tools/verify-pagemap-compatibility.py')
    compat = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(compat)
    source = load(ROOT / 'out/t3-1c-unpaired-shared-integration/source-tested.json')
    previous = load(ROOT / 'docs/verification/t3-1c-scale-measurement.json')
    inputs = previous['input_pdf_sha256']
    artifacts = previous['artifacts_sha256']
    for hashes in (source, inputs, artifacts):
        check_hashes(hashes)
    assert len(source) == 544 and len(inputs) == 26
    history = load(BASE / 'measurements/verification.json')
    baseline = load(BASE / 'output-hashes.json')
    target_ids = [c['case']['id'] for c in history['cases']
                  if next(v for v in c['variants'] if v['carry_enabled'])['expectation_unmet']]
    assert len(history['cases']) == 13 and len(target_ids) == 9
    binary = {str(p.relative_to(ROOT)): digest(p) for p in sorted(CLI.parent.rglob('*')) if p.is_file()}
    # 旧結果は日時以外を正規化せず、過去の記録に保存されたハッシュへ照合する。
    for key, expected in baseline.items():
        directory = BASE / 'measurements' / (key + '-product-0')
        assert compat.snapshot(directory) == expected, key
        assert set(expected) == {str(p.relative_to(directory)) for p in directory.rglob('*') if p.is_file()}
    for case in history['cases']:
        for side, name in zip('ab', case['case']['files'], strict=True):
            relative = str(Path(name).relative_to(ROOT))
            assert inputs[relative] == case['case']['expected'][side + '_sha256']
    save(output / 'preflight.json', dict(source_sha256=source, input_sha256=inputs,
         old_artifact_sha256=artifacts, cli_files_sha256=binary, target_ids=target_ids))
    configs = {}
    for enabled in (False, True):
        configs[enabled] = output / ('carry-' + str(enabled).lower() + '.yaml')
        configs[enabled].write_text('rows: {enabled: true, carry_enabled: ' + str(enabled).lower()
                                   + '}\nreport: {raw_overlay: true}\n')
    metrics = dict(conditions=0, processes=0, target_conditions=9, control_conditions=4, pdfs=26,
                   target_expectations_met=0, target_expectations_unmet=0, control_expectations_met=0,
                   raw_png_pairs=0, fallback_page_dtos=0, identical_output_files=0, changed_output_files=0)
    record = dict(scope='R-02_fixed_a4_revalidation', completed=False, platform=platform.platform(),
                  dpi=300, iterations=1, performance_acceptance=False, metrics=metrics, cases=[], runs=[])
    save(output / 'verification.json', record)
    for saved in history['cases']:
        case = saved['case']
        reports, raw_sets, variants = {}, {}, []
        for enabled in (False, True):
            key = case['id'] + '-' + str(enabled).lower()
            name = key + '-product-0'
            directory = output / name
            command = ['dotnet', str(CLI), 'compare', *case['files'], '--config', str(configs[enabled]),
                       '--out', str(directory), '--quiet']
            start = time.monotonic()
            with (output / (name + '.stdout.log')).open('w') as stdout, (output / (name + '.stderr.log')).open('w') as stderr:
                child = subprocess.Popen(command, cwd=ROOT, stdout=stdout, stderr=stderr)
                _, status, usage = os.wait4(child.pid, 0)
                child.returncode = os.waitstatus_to_exitcode(status)
            elapsed = time.monotonic() - start
            assert child.returncode == 1 and not (output / (name + '.stderr.log')).read_text(), (name, child.returncode)
            report = load(directory / 'result.json')
            old = load(BASE / 'measurements' / name / 'result.json')
            flow = report.get('page_flow')
            assert report['summary']['status'] == 'different' and report['summary']['difference_count'] > 0
            if not enabled:
                assert flow is None and report['summary']['aggregated_difference_count'] is None
            current = compat.snapshot(directory)
            assert set(current) == {str(p.relative_to(directory)) for p in directory.rglob('*') if p.is_file()}
            changed = sorted(p for p in set(baseline[key]) | set(current) if baseline[key].get(p) != current.get(p))
            raw = {str(page['page']) + '-' + side: digest(directory / page['raw_evidence'][side]['image'])
                   for page in report['pages'] for side in 'ab'}
            raw.update({str(page['page']) + '-overlay': digest(directory / page['raw_evidence']['overlay'])
                        for page in report['pages']})
            reports[enabled], raw_sets[enabled] = report, raw
            expectation = case['expected']
            unmet = bool(enabled and (flow['status'] != expectation['expected_flow'] or
                         expectation['expected_aggregate'] is not None and
                         report['summary']['aggregated_difference_count'] != expectation['expected_aggregate']))
            result = dict(id=name, case=case['id'], carry_enabled=enabled, kind='product', iteration=0,
                          seconds=elapsed, peak_rss_bytes=usage.ru_maxrss * (1 if platform.system() == 'Darwin' else 1024),
                          exit_code=child.returncode, status=flow['status'] if flow else 'disabled',
                          reasons=flow['reasons'] if flow else [], summary=report['summary'], expectation_unmet=unmet,
                          old_summary_equal=old['summary'] == report['summary'],
                          old_page_dtos_equal=old['pages'] == report['pages'],
                          old_flow_equal=old.get('page_flow') == flow,
                          changed_output_files=changed, normalized_output_sha256=current)
            record['runs'].append(result)
            variants.append({k: v for k, v in result.items() if k not in ('normalized_output_sha256', 'changed_output_files')})
            metrics['processes'] += 1
            metrics['changed_output_files'] += len(changed)
            metrics['identical_output_files'] += sum(baseline[key].get(p) == h for p, h in current.items())
            save(output / 'verification.json', record)
            print(f"{metrics['processes']}/26 {name}: {result['status']} "
                  f"{report['summary']['difference_count']}→{report['summary']['aggregated_difference_count']} "
                  f"期待未成立={unmet} 旧出力との差={len(changed)}", flush=True)
        assert raw_sets[False] == raw_sets[True], (case['id'], 'raw画像の変更')
        metrics['raw_png_pairs'] += len(raw_sets[True])
        fallback = reports[True]['page_flow']['status'] == 'skipped'
        if fallback:
            assert reports[False]['pages'] == reports[True]['pages'], (case['id'], '見送り時のページ結果')
            assert reports[True]['summary']['difference_count'] == reports[True]['summary']['aggregated_difference_count']
            metrics['fallback_page_dtos'] += len(reports[True]['pages'])
        unmet = variants[1]['expectation_unmet']
        if case['id'] in target_ids:
            metrics['target_expectations_unmet' if unmet else 'target_expectations_met'] += 1
        else:
            assert not unmet, (case['id'], '対照の期待未成立')
            metrics['control_expectations_met'] += 1
        if case['expected']['test']['pages'] == 31:
            assert 'flow_pixel_limit' in reports[True]['page_flow']['reasons']
        record['cases'].append(dict(case=case, role='target' if case['id'] in target_ids else 'control',
                                   variants=variants, raw_evidence_png_pairs=len(raw_sets[True]),
                                   fallback_pages_equal=fallback))
        metrics['conditions'] += 1
        save(output / 'verification.json', record)
    for hashes in (source, inputs, artifacts, binary):
        check_hashes(hashes)
    assert metrics['conditions'] == 13 and metrics['processes'] == 26 and metrics['control_expectations_met'] == 4
    record['completed'] = True
    save(output / 'verification.json', record)
    print(json.dumps(metrics, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    main()
