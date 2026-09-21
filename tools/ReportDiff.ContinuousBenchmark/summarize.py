"""生の計測から、注釈本文や帳票画像を含まないページ別の公開用データを作る。"""
import argparse
import json
from pathlib import Path
import statistics


def distribution(values):
    return dict(min=min(values), median=statistics.median(values), max=max(values))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='measure.pyのsummary.json')
    parser.add_argument('destination', type=Path, help='新規の公開用JSON')
    args = parser.parse_args()
    if args.destination.exists():
        parser.error('出力は未使用のパスにしてください。')
    raw = json.loads(args.source.read_text())
    records = []
    for run in raw['records']:
        record = {key: value for key, value in run.items() if key != 'profile'}
        if run['kind'] == 'probe':
            stages = run['profile']['stages']
            points = []
            for page in range(1, run['count'] + 1):
                part = [s for s in stages if s['page'] == page]
                point = next(s.copy() for s in part if s['stage'] == 'page-complete')
                del point['stage']
                del point['stage_ms']
                point['observed_peak_rss_bytes'] = max(s['rss_bytes'] for s in part)
                point['images_disposed_rss_bytes'] = next(s['rss_bytes'] for s in part if s['stage'] == 'images-disposed')
                point['stage_ms'] = {s['stage']: s['stage_ms'] for s in part}
                points.append(point)
            record['page_points'] = points
            record['endpoints'] = [s for s in stages if s['page'] == 0]
            record['blocks'] = []
            for first, last in [(1, 1), (2, 10), (11, 20), (21, 40), (41, 60), (61, 80), (81, 100)]:
                selected = [p for p in points if first <= p['page'] <= last]
                record['blocks'].append(dict(first=first, last=last,
                    **{key: distribution([p[key] for p in selected])
                       for key in ['page_ms', 'rss_bytes', 'managed_estimate_bytes', 'last_gc_heap_bytes']}))
            record['comparison_stage_ms'] = {
                key: distribution([p['timings'][key] for p in run['profile']['pages'] if p['page'] > 1])
                for key in run['profile']['pages'][0]['timings'] if key.endswith('Ms')}
        records.append(record)
    result = dict(schema_version=1, summary=raw['summary'], inputs=raw['inputs'], binaries=raw['binaries'],
                  validation=dict(runs=len(records), pages=sum(r['count'] for r in records),
                                  pngs=sum(r['png_count'] for r in records),
                                  json_equal=all(r['json_equal'] for r in records),
                                  png_sha256_equal=all(r['png_sha256_equal'] for r in records)),
                  records=records)
    args.destination.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n')
    for row in result['summary']:
        print(row['dpi'], row['scenario'], row['count'], row['kind'],
              f'{row["elapsed_ms"]["median"] / 1000:.3f}s',
              f'{row["peak_rss_bytes"]["median"] / 1e9:.3f}GB')
    print(result['validation'])


if __name__ == '__main__':
    main()
