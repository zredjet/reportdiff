using System.Collections;
using System.Reflection;
using System.Text.Json;
using UglyToad.PdfPig.Fonts.SystemFonts;

// 調査専用の独立プロセス。インストール済みフォントの参照のみで、ファイルは変更しない。
// PdfPigのキャッシュだけを各反復間で消し、最初の同時検索を再現する。
if (args.Length is not (1 or 3) || File.Exists(args[0])) throw new ArgumentException("未使用のJSON出力先、任意で2つのフォント名を指定してください。");
var finder = SystemFontFinder.Instance;
var type = finder.GetType();
var cache = (IDictionary)type.GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
var names = (IDictionary)type.GetField("nameToFileNameMap", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(finder)!;
var files = (IDictionary)type.GetField("readFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(finder)!;
var firstName = args.Length == 3 ? args[1] : "Monaco";
var secondName = args.Length == 3 ? args[2] : "AppleSymbols";
var pairs = new[] { (firstName, firstName), (firstName, secondName) };
var records = new List<object>();
foreach (var (nameA, nameB) in pairs)
{
    Clear(); var expectedA = finder.GetTrueTypeFont(nameA)?.Name;
    Clear(); var expectedB = finder.GetTrueTypeFont(nameB)?.Name;
    if (expectedA is null || expectedB is null) throw new InvalidOperationException("このOSに比較対象のフォントがありません。");
    var mismatches = new List<object>();
    const int repetitions = 200;
    for (var repeat = 0; repeat < repetitions; repeat++)
    {
        Clear(); using var barrier = new Barrier(2);
        string? actualA = null, actualB = null;
        Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = 2 },
            () => { if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); actualA = finder.GetTrueTypeFont(nameA)?.Name; },
            () => { if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); actualB = finder.GetTrueTypeFont(nameB)?.Name; });
        if (actualA != expectedA || actualB != expectedB) mismatches.Add(new { repeat, actualA, actualB });
    }
    records.Add(new { nameA, nameB, expectedA, expectedB, repetitions, mismatches });
}
File.WriteAllText(args[0], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
void Clear() { cache.Clear(); names.Clear(); files.Clear(); }
