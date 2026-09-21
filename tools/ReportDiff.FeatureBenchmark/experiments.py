"""特徴量の比較用コピーをoutへ生成する。製品ソースは変更しない。"""
from pathlib import Path
import shutil
import sys

root = Path(__file__).resolve().parents[2]
base = (Path(sys.argv[1]) if len(sys.argv) > 1 else root / 'out/perf2i-features/variants').resolve()
if base.exists():
    raise SystemExit('コピー先は存在しないディレクトリを指定してください。')
if not base.is_relative_to(root / 'out'):
    raise SystemExit('コピー先はリポジトリ内のout配下にしてください。')

variants = [('stripe64', 64, False, False), ('stripe256', 256, False, False),
            ('ink128', 128, True, False), ('ink64', 64, True, False), ('reuse128', 128, False, True),
            ('combined128', 128, True, True), ('combined64', 64, True, True), ('combined256', 256, True, True)]
for name, stripe, ink, reuse in variants:
    dest = base / name
    for project in ['Core', 'Pdf', 'Report', 'Cli']:
        target = dest / f'src/ReportDiff.{project}'
        target.mkdir(parents=True)
        for source in (root / f'src/ReportDiff.{project}').iterdir():
            if source.is_file():
                shutil.copy2(source, target / source.name)

    def change(file, before, after):
        path = dest / f'src/ReportDiff.Core/{file}.cs'
        text = path.read_text()
        if text.count(before) != 1:
            raise RuntimeError(f'試作パッチ対象が一意でありません: {file}')
        path.write_text(text.replace(before, after))

    change('FeatureStripeSchedule', 'public const int StripeRows = 128;', f'public const int StripeRows = {stripe};')
    if ink:
        change('ComparisonFeatures', '        using var minimum = new Mat();', '''        using var minimum = new Mat();
        var radius = Math.Max(1, Units.RoundPixels(parameters.Ink.BackgroundRadiusMm, parameters.Dpi));
        using var inkKernel = includeInk ? Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * radius + 1, 2 * radius + 1)) : new Mat();''')
        change('ComparisonFeatures', '''                using var ink = ImageInk.FromLab(lab, parameters.Dpi, parameters.Ink);
                CopyRows(ink, Ink, y - top, y, end - y);''', '''                using var lightness = new Mat();
                using var background = new Mat();
                Cv2.ExtractChannel(lab, lightness, 0);
                using var centerLightness = new Mat(lightness, new Rect(0, y - top, cols, end - y));
                using var outputInk = new Mat(Ink, new Rect(0, y, cols, end - y));
                Cv2.Dilate(centerLightness, background, inkKernel);
                Cv2.Subtract(background, centerLightness, background);
                Cv2.Compare(background, parameters.Ink.ContrastThreshold, outputInk, CmpTypes.GT);''')
        # lightness/backgroundは帯の末尾で解放。保持するminimum + Lab変換時24bytesが上限。
        change('FeatureStripeSchedule', '(includeInk ? 25L : 24L) * labRows',
               '24L * labRows')
    if reuse:
        change('ComparisonFeatures', '        using var minimum = new Mat();', '''        using var minimum = new Mat();
        using var cache = new LabStripeCache(cols, (int)Math.Min(rows, FeatureStripeSchedule.StripeRows + 2L * margin));''')
        change('ComparisonFeatures', '''            using var source = new Mat(image, new Rect(0, top, cols, bottom - top));
            using var lab = ImageInk.ToLab(source);''', '''            using var lab = cache.Get(image, top, bottom);''')
        shutil.copy2(root / 'tools/ReportDiff.FeatureBenchmark/LabStripeCache.cs.txt', dest / 'src/ReportDiff.Core/LabStripeCache.cs')
    for folder, program, reference in [('Probe', 'PipelineBenchmark', 'Cli'), ('Features', 'FeatureBenchmark', 'Core')]:
        target = dest / folder
        target.mkdir()
        shutil.copy2(root / f'tools/ReportDiff.{program}/Program.cs', target / 'Program.cs')
        (target / f'{folder}.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ReportDiff.Tests</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="../src/ReportDiff.{reference}/ReportDiff.{reference}.csproj" /></ItemGroup></Project>''')
    print(dest)
