"""製品ソースのコピーへ計測だけを追加する。コピー先以外は変更しない。"""
from pathlib import Path
import shutil,sys
root=Path(__file__).resolve().parents[2]
dest=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else root/'out/perf2g-pipeline/instrumented'
if dest.exists():raise SystemExit('コピー先は存在しないディレクトリを指定してください。')
if not dest.is_relative_to(root/'out'):raise SystemExit('コピー先はリポジトリ内のout配下にしてください。')
for name in ['Core','Pdf','Report','Cli']:
 source=root/f'src/ReportDiff.{name}';target=dest/f'src/ReportDiff.{name}';target.mkdir(parents=True)
 for p in source.iterdir():
  if p.is_file():shutil.copy2(p,target/p.name)
def replace(project,name,before,after):
 p=dest/f'src/ReportDiff.{project}/{name}.cs';s=p.read_text()
 if s.count(before)!=1:raise RuntimeError(f'計測パッチ対象が一意でありません: {name}: {before}')
 p.write_text(s.replace(before,after))
p=dest/'src/ReportDiff.Core/PipelineMetrics.cs'
p.write_text('''using System.Collections.Concurrent;
using System.Diagnostics;
namespace ReportDiff.Core;
// 計測コピー専用。製品ソースには含めない。
public static class PipelineMetrics
{
    private static readonly ConcurrentQueue<object> events = new();
    private static long origin = Stopwatch.GetTimestamp();
    public static void Reset() { events.Clear(); origin = Stopwatch.GetTimestamp(); }
    public static object[] Snapshot() => events.ToArray();
    public static void Add(string kind, long start, object data) => events.Enqueue(new {
        kind, start_ms = Stopwatch.GetElapsedTime(origin, start).TotalMilliseconds,
        elapsed_ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
        thread = Environment.CurrentManagedThreadId, data });
}
''')
for project,name in [('Report','ReportWriter'),('Pdf','PdfTextReader'),('Core','ComparisonFeatures')]:
 p=dest/f'src/ReportDiff.{project}/{name}.cs';p.write_text('using System.Diagnostics;\n'+p.read_text())
replace('Report','ReportWriter','''        if (!Cv2.ImEncode(".png", image, out var bytes))''','''        var encodeStarted = Stopwatch.GetTimestamp();
        if (!Cv2.ImEncode(".png", image, out var bytes))''')
replace('Report','ReportWriter','''        var path = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));''','''        PipelineMetrics.Add("png.encode", encodeStarted, new { path = relativePath, width = image.Width, height = image.Height, bytes = bytes.Length });
        var writeStarted = Stopwatch.GetTimestamp();
        var path = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));''')
replace('Report','ReportWriter','''        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);''','''        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
        PipelineMetrics.Add("png.write", writeStarted, new { path = relativePath, bytes = bytes.Length });''')
replace('Report','ReportWriter','''                using var overlay = ReportImages.Overlay''','''                var overlayStarted = Stopwatch.GetTimestamp();
                using var overlay = ReportImages.Overlay''')
replace('Report','ReportWriter','''                WritePng(paths.Overlay!, overlay);''','''                PipelineMetrics.Add("images.overlay", overlayStarted, new { width = overlay.Width, height = overlay.Height });
                WritePng(paths.Overlay!, overlay);''')
replace('Report','ReportWriter','''                using var diff = ReportImages.DifferenceCrop''','''                var cropStarted = Stopwatch.GetTimestamp();
                using var diff = ReportImages.DifferenceCrop''')
replace('Report','ReportWriter','''                WritePng(crops.A, a);''','''                PipelineMetrics.Add("images.difference-crop", cropStarted, new { width = crop.Width, height = crop.Height });
                WritePng(crops.A, a);''')
replace('Pdf','PdfTextReader','''                stream = File.OpenRead(path);''','''                var openStarted = Stopwatch.GetTimestamp();
                stream = File.OpenRead(path);''')
replace('Pdf','PdfTextReader','''                document = PdfDocument.Open(stream);''','''                document = PdfDocument.Open(stream);
                PipelineMetrics.Add("text.open", openStarted, new { pageNumber });''')
replace('Pdf','PdfTextReader','''            cachedPage = document.GetPage(pageNumber);''','''            var pageStarted = Stopwatch.GetTimestamp();
            cachedPage = document.GetPage(pageNumber);
            PipelineMetrics.Add("text.page", pageStarted, new { pageNumber, letters = cachedPage.Letters.Count });''')
replace('Pdf','PdfTextReader','''            var words = page.GetWords(WordExtractor)''','''            var wordsStarted = Stopwatch.GetTimestamp();
            var words = page.GetWords(WordExtractor)''')
replace('Pdf','PdfTextReader','''            if (words.Length > options.MaxWordsPerPage)''','''            PipelineMetrics.Add("text.words", wordsStarted, new { pageNumber, count = words.Length });
            if (words.Length > options.MaxWordsPerPage)''')
replace('Pdf','PdfTextReader','''            var sx = originalSize.Width / page.Width;''','''            var mapStarted = Stopwatch.GetTimestamp();
            var sx = originalSize.Width / page.Width;''')
replace('Pdf','PdfTextReader','''            return TextAnnotations.Create(mapped, clusters, exclusions, dpi, options);''','''            PipelineMetrics.Add("text.map", mapStarted, new { pageNumber, words = mapped.Count, clusters = clusters.Count });
            var matchStarted = Stopwatch.GetTimestamp();
            var annotations = TextAnnotations.Create(mapped, clusters, exclusions, dpi, options);
            PipelineMetrics.Add("text.match", matchStarted, new { pageNumber, words = mapped.Count, clusters = clusters.Count });
            return annotations;''')
# ワーカー内の演算別時間を合計し、ワーカー終了時に1件だけ記録する。
replace('Core','ComparisonFeatures','''        var rows = image.Rows;
        var cols = image.Cols;
        // カーネル''','''        var rows = image.Rows;
        var cols = image.Cols;
        var workerStarted = Stopwatch.GetTimestamp();
        var parts = new double[7];
        long partStarted;
        // カーネル''')
replace('Core','ComparisonFeatures','''            using var lab = ImageInk.ToLab(source);''','''            partStarted = Stopwatch.GetTimestamp();
            using var lab = ImageInk.ToLab(source);
            parts[0] += Stopwatch.GetElapsedTime(partStarted).TotalMilliseconds;''')
for slot,line in enumerate(['Cv2.Blur(center, outputValues, new Size(3, 3));','Cv2.Dilate(center, outputContrast, kernel);','Cv2.Erode(center, minimum, kernel);','Cv2.Subtract(outputContrast, minimum, outputContrast);'],1):
 replace('Core','ComparisonFeatures','                '+line,'                partStarted = Stopwatch.GetTimestamp();\n                '+line+f'\n                parts[{slot}] += Stopwatch.GetElapsedTime(partStarted).TotalMilliseconds;')
replace('Core','ComparisonFeatures','''                using var ink = ImageInk.FromLab(lab, parameters.Dpi, parameters.Ink);
                CopyRows(ink, Ink, y - top, y, end - y);''','''                partStarted = Stopwatch.GetTimestamp();
                using var ink = ImageInk.FromLab(lab, parameters.Dpi, parameters.Ink);
                parts[5] += Stopwatch.GetElapsedTime(partStarted).TotalMilliseconds;
                partStarted = Stopwatch.GetTimestamp();
                CopyRows(ink, Ink, y - top, y, end - y);
                parts[6] += Stopwatch.GetElapsedTime(partStarted).TotalMilliseconds;''')
replace('Core','ComparisonFeatures','''        }
    }

    private static void CopyRows''','''        }
        PipelineMetrics.Add("features.worker", workerStarted, new { width = cols, height = rows, firstStripe, lastStripe,
            lab_ms = parts[0], blur_ms = parts[1], dilate_ms = parts[2], erode_ms = parts[3], subtract_ms = parts[4], ink_ms = parts[5], copy_ink_ms = parts[6] });
    }

    private static void CopyRows''')
replace('Core','TolerantDifference','''            results[group] = GroupShiftSearch.Evaluate''','''            var groupStarted = Stopwatch.GetTimestamp();
            results[group] = GroupShiftSearch.Evaluate''')
replace('Core','TolerantDifference','''                options, index.Runs(group), index.InitialCount(group), shifts, raw);''','''                options, index.Runs(group), index.InitialCount(group), shifts, raw);
            PipelineMetrics.Add("search.group", groupStarted, new { group, pixels = index.PixelCount(group), runs = index.Runs(group).Length,
                initial = index.InitialCount(group), remaining = results[group].Remaining, best_shift = results[group].ShiftIndex });''')
probe=dest/'Probe';probe.mkdir()
shutil.copy2(root/'tools/ReportDiff.PipelineBenchmark/Program.cs',probe/'Program.cs')
(probe/'Probe.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ReportDiff.Tests</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="../src/ReportDiff.Cli/ReportDiff.Cli.csproj" /></ItemGroup></Project>''')
print(dest)
