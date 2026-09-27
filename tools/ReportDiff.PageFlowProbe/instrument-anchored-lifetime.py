#!/usr/bin/env python3
"""out内のコピーだけでC/D・元画像・完成した比較マスクの同時所有を計測する。
一時ROI、比較器内部の作業Mat、PDFium/Skia内部の確保は対象外。プロセス全体は別途RSSで測る。
"""
from pathlib import Path
import hashlib
import json
import shutil
import sys

root = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1]).resolve()
assert out.is_relative_to(root/'out') and not out.exists()
hashes = {}
for project in ['Core', 'Pdf', 'Report', 'Cli']:
    source = root/f'src/ReportDiff.{project}'
    shutil.copytree(source, out/f'src/ReportDiff.{project}', ignore=shutil.ignore_patterns('bin'))
    for p in source.glob('*.cs'): hashes[str(p.relative_to(root))] = hashlib.sha256(p.read_bytes()).hexdigest()

def replace(project, file, before, after):
    path = out/f'src/ReportDiff.{project}/{file}.cs'; text = path.read_text()
    assert text.count(before) == 1, (file, before)
    path.write_text(text.replace(before, after))

(out/'src/ReportDiff.Core/AnchoredLifetime.cs').write_text('''using OpenCvSharp;
using System.Text.Json;
namespace ReportDiff.Core;
// 診断コピーだけ。強参照を保持し、GCの最終化を明示Disposeの成功へ算入しない。
public static class AnchoredLifetime
{
    private sealed record Entry(Mat Image, string Kind, long Bytes);
    private static readonly List<Entry> alive = [];
    private static readonly Dictionary<string, int> peaks = [];
    private static int peakCount, created;
    private static long peakBytes;
    private static object? budget;
    public static bool Enabled { get; set; }
    public static Mat Track(Mat image, string kind)
    {
        if (!Enabled) return image;
        alive.RemoveAll(e => e.Image.IsDisposed);
        if (alive.Any(e => ReferenceEquals(e.Image, image))) return image;
        alive.Add(new(image, kind, checked(image.Total() * image.ElemSize()))); created++;
        peakCount = Math.Max(peakCount, alive.Count); peakBytes = Math.Max(peakBytes, alive.Sum(e => e.Bytes));
        foreach (var group in alive.GroupBy(e => e.Kind)) peaks[group.Key] = Math.Max(peaks.GetValueOrDefault(group.Key), group.Count());
        return image;
    }
    public static Mat? Nullable(Mat? image, string kind) => image is null ? null : Track(image, kind);
    public static void Budget(AnchoredContentBudget value) => budget = new { value.ExistingBytes, value.PeakBytes, value.UsedBytes };
    public static void Write(string path)
    {
        alive.RemoveAll(e => e.Image.IsDisposed);
        if (alive.Count != 0) throw new InvalidOperationException("診断対象Matが未解放です。");
        File.WriteAllText(path, JsonSerializer.Serialize(new { scope = "owned original/C/D colors and completed comparison masks; excludes temporary work/ROI and native libraries",
            created, peakCount, peakBytes, peaks, remaining = alive.Count, budget }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
''')
replace('Cli', 'AnchoredRun', 'Plan = plan; settingsDigest = Hash(settings);',
    'Plan = plan; settingsDigest = Hash(settings); AnchoredLifetime.Enabled = true;')
replace('Cli', 'AnchoredRun', 'return image.TakePixels();', 'return AnchoredLifetime.Track(image.TakePixels(), "original");')
replace('Cli', 'AnchoredRun', 'public void Dispose() { foreach (var r in reservations) r.Dispose(); Plan.Dispose(); }',
    'public void Dispose() { AnchoredLifetime.Budget(Plan.Budget); foreach (var r in reservations) r.Dispose(); Plan.Dispose(); }')
for side in ['a', 'b']:
    replace('Core', 'AnchoredContentImages', f'{side} = new(size, MatType.CV_8UC3, Scalar.White);',
        f'{side} = AnchoredLifetime.Track(new Mat(size, MatType.CV_8UC3, Scalar.White), "C_D_color");')
for mask in ['raw', 'label', 'removal']:
    replace('Core', 'AnchoredProjection', f'{mask} = new(display.Size, MatType.CV_8UC1, Scalar.Black);',
        f'{mask} = AnchoredLifetime.Track(new Mat(display.Size, MatType.CV_8UC1, Scalar.Black), "D_mask");')
for title, field in [('Raw', 'raw'), ('Label', 'label'), ('Removal', 'removal')]:
    nullable = '?' if field == 'removal' else ''
    method = 'Nullable' if nullable else 'Track'
    replace('Core', 'PageComparison', f'public Mat{nullable} {title}Mask {{ get; }} = {field}Mask;',
        f'public Mat{nullable} {title}Mask {{ get; }} = AnchoredLifetime.{method}({field}Mask, "comparison_mask");')
replace('Cli', 'Program', 'return CliApplication.Run(args, Console.Out, Console.Error);',
    '''var result = CliApplication.Run(args, Console.Out, Console.Error);
if (Environment.GetEnvironmentVariable("REPORTDIFF_ANCHORED_LIFETIME") is { } path) ReportDiff.Core.AnchoredLifetime.Write(path);
return result;''')
(out/'source-hashes.json').write_text(json.dumps(hashes, indent=2)+'\n')
print(out)
