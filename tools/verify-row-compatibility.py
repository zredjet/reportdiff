#!/usr/bin/env python3
"""記録済みv0.1.4の95条件に対し、rows無効/有効で既存値・画像・終了コードを照合する。"""
import argparse
import hashlib
import html
import importlib.util
import json
from pathlib import Path
import re
import subprocess
from datetime import datetime

COUNTS = ['structural_change_count', 'structural_change_counts', 'difference_count', 'difference_count_complete']
ROW_DEFAULTS = dict(enabled=False, max_shift_mm=20, min_word_match=0.6, refine_mm=0.3,
                    min_improvement=0.05, min_score_gap=0.02, min_support_bands=2, min_support_ink_mm2=1, max_segments=8)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value, enabled, provenance):
    if isinstance(value, list):
        return [canonical(v, enabled, provenance) for v in value]
    if not isinstance(value, dict):
        return value
    value = dict(value)
    if 'warnings' in value:
        added = [w for w in value['warnings'] if w.get('code') == 'ROW_SHIFT_SUSPECTED']
        assert enabled or not added
        value['warnings'] = [w for w in value['warnings'] if w not in added]
    if 'rows' in value and {'dpi', 'diff', 'cluster', 'report'} <= value.keys():
        assert value.pop('rows') == dict(ROW_DEFAULTS, enabled=enabled)
    if {'page', 'clusters', 'size_px', 'images'} <= value.keys():
        row = value.pop('row_alignment', None)
        if row:
            assert row['status'] != 'applied', row
        for field in COUNTS:
            value.pop(field, None)
    if {'pages_compared', 'clusters', 'absorbed_groups'} <= value.keys():
        for field in COUNTS:
            value.pop(field, None)
    if provenance and value == provenance[0]:
        value = provenance[1]
    return {k: canonical(v, enabled, provenance) for k, v in value.items()}


def snapshot(root, enabled, provenance=None):
    result, dates = {}, []
    for path in root.rglob('*.json'):
        text = path.read_text()
        dates.extend(re.findall(r'"generated_at"\s*:\s*"([^"\n]*)"', text))
        text = re.sub(r'("generated_at"\s*:\s*)"[^"\n]*"', r'\1"GENERATED_AT"', text)
        result[str(path.relative_to(root))] = digest(json.dumps(canonical(json.loads(text), enabled, provenance), sort_keys=True).encode())
    for path in root.rglob('*'):
        if path.suffix == '.png':
            result[str(path.relative_to(root))] = digest(path.read_bytes())
        elif path.suffix == '.html':
            text = path.read_text()
            for date in dates:
                value = datetime.fromisoformat(date)
                offset = value.strftime('%z')
                visible = value.strftime('%Y-%m-%d %H:%M:%S ') + offset[:3] + ':' + offset[3:]
                parts = re.fullmatch(r'(.*T\d{2}:\d{2}:\d{2})(?:\.(\d+))?([+-]\d{2}:\d{2})', date)
                roundtrip = parts[1] + '.' + (parts[2] or '').ljust(7, '0') + parts[3]
                for item in [date, roundtrip, visible]:
                    text = text.replace(item, 'GENERATED_AT').replace(html.escape(item).replace('+', '&#x2B;'), 'GENERATED_AT')
            text = re.sub(r'(<script type="application/json" id="result">)(.*?)(</script>)',
                          lambda m: m[1] + json.dumps(canonical(json.loads(m[2]), enabled, provenance), sort_keys=True) + m[3], text, flags=re.S)
            # 新しい行設定欄・見送り理由欄だけを除く。既存の表・文言・スタイル・JSは残す。
            text = re.sub(r'<section class="row-(?:settings|alignment)"[^>]*>.*?</section>', '', text, flags=re.S)
            # 行整列の見送りで追加する警告のみを除外し、既存警告は全件照合する。
            if enabled:
                text = re.sub(r'<li>[^<]* <code>ROW_SHIFT_SUSPECTED</code></li>', '', text)
                text = text.replace('<h2 id="warnings-title">警告</h2><ul></ul>', '<h2 id="warnings-title">警告</h2><p>警告はありません。</p>')
            if provenance:
                for key in ['path', 'sha256']:
                    old, new = provenance[0][key], provenance[1][key]
                    text = text.replace(old, new).replace(html.escape(old).replace('/', '&#47;'), html.escape(new).replace('/', '&#47;'))
            result[str(path.relative_to(root))] = digest(text.encode())
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline_record', type=Path)
    parser.add_argument('current_cli', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    baseline = json.loads(args.baseline_record.read_text())
    spec = importlib.util.spec_from_file_location('previous_verifier', Path(__file__).with_name('verify-pagemap-compatibility.py'))
    previous = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(previous)
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    records = []
    for case in baseline['cases']:
        before = args.baseline_record.parent / 'before' / case['id']
        assert previous.snapshot(before) == case['files_sha256'], ('baseline_changed', case['id'])
        expected = snapshot(before, False)
        for enabled in [False, True]:
            name = case['id'] + ('-rows-on' if enabled else '-rows-off')
            cli_args = list(case['args'])
            provenance = None
            if enabled:
                assert '--rules' not in cli_args, '帳票別のrows指定を使う追加ケースは別途検証すること'
                source = Path(cli_args[cli_args.index('--config') + 1]) if '--config' in cli_args else None
                text = source.read_text() if source else '{}'
                try:
                    config = json.loads(text)
                    assert 'rows' not in config
                    config['rows'] = {'enabled': True}
                    modified = json.dumps(config)
                except json.JSONDecodeError:
                    assert not re.search(r'^rows:', text, re.M)
                    modified = text.rstrip() + '\nrows: {enabled: true}\n'
                target = root / (name + '.yaml')
                target.write_text(modified)
                if source:
                    cli_args[cli_args.index('--config') + 1] = str(target)
                    provenance = ({'path': str(target), 'sha256': digest(target.read_bytes())},
                                  {'path': str(source.resolve()), 'sha256': digest(source.read_bytes())})
                else:
                    cli_args += ['--config', str(target)]
            output = root / name
            run = subprocess.run(['dotnet', str(args.current_cli.resolve()), *cli_args, '--out', str(output)], capture_output=True, text=True)
            assert run.returncode == case['exit_code'], (name, run.returncode, run.stderr)
            actual = snapshot(output, enabled, provenance)
            assert actual == expected, (name, [k for k in actual.keys() | expected.keys() if actual.get(k) != expected.get(k)])
            records.append({'id': name, 'rows_enabled': enabled, 'exit_code': run.returncode, 'files_sha256': actual})
            print(name, flush=True)
    result = {'baseline_record': str(args.baseline_record), 'baseline_record_sha256': digest(args.baseline_record.read_bytes()),
              'baseline_cli_sha256': baseline['baseline_cli_sha256'], 'current_cli_sha256': digest(args.current_cli.read_bytes()),
              'normalization': ['generated_at', 'config.rows', 'page.row_alignment', *COUNTS,
                                'new HTML row-settings and row-alignment sections', 'ROW_SHIFT_SUSPECTED warning only',
                                'temporary rows-enabled config path and SHA256'],
              'cases': records, 'file_count': sum(len(c['files_sha256']) for c in records),
              'png_count': sum(k.endswith('.png') for c in records for k in c['files_sha256'])}
    (root / 'verification.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')


if __name__ == '__main__':
    main()
