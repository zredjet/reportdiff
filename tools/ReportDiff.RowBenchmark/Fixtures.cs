using System.Globalization;
using System.Text;

namespace ReportDiff.Tests;

internal static partial class PdfFixture
{
    // 自作Type3フォント。物理サイズだけをA4にし、行間は300dpiで100pxのまま維持する。
    internal static byte[] RowA4Benchmark(string scenario, bool revised)
    {
        const double width = 210 * 72 / 25.4, height = 297 * 72 / 25.4;
        var content = new StringBuilder("0 g\n");
        var columns = scenario == "columns" ? 2 : 1;
        for (var column = 0; column < columns; column++)
        {
            var entries = scenario == "short"
                ? new List<string> { "HEAD START", "SUB FIRST", "ITEM ALPHA", "ITEM BETA", "ITEM GAMMA", "ITEM DELTA", "SUM TOTAL 555", "END CHECK" }
                : Enumerable.Range(0, 24).Select(i => $"{(column == 0 ? "LEFT" : "RIGHT")}{new string((char)('A' + i), 4)} VALUE {(i == 20 ? "555" : "100")}").ToList();
            if (revised && column == 0) entries.Insert(scenario == "short" ? 2 : 8, "NEW ADDED 200");
            if (revised && scenario is "adopted" or "short")
                entries[scenario == "short" ? 7 : 21] = entries[scenario == "short" ? 7 : 21].Replace("555", "111", StringComparison.Ordinal);
            var left = column == 0 ? 24 : 310;
            var lineWidth = columns == 1 ? 540 : 252;
            for (var row = 0; row < entries.Count; row++)
            {
                var top = 48 + row * 24;
                Word(entries[row], left + 16, top);
                if (scenario != "fixed-grid") Rules(left, lineWidth, top);
            }
            if (scenario == "fixed-grid")
                for (var row = 0; row < 26; row++) Rules(left, lineWidth, 48 + row * 24);
        }
        return RowFontDocument(content.ToString(), width, height);

        void Word(string text, double x, double top) => content.Append(CultureInfo.InvariantCulture,
            $"BT /F1 9.6 Tf 1 0 0 1 {x} {height - top - 16.8} Tm <{Convert.ToHexString(Encoding.ASCII.GetBytes(text))}> Tj ET\n");
        void Rules(double x, double w, double top) => content.Append(CultureInfo.InvariantCulture,
            $"{x} {height - top - 20} {w} 0.48 re f\n{x} {height - top - 24} 1.44 24 re f\n");
    }

    internal static byte[] ExcludedRowBenchmark(bool revised, bool blocks = false)
    {
        const double height = 300;
        var content = new StringBuilder("0 g\n");
        content.Append(CultureInfo.InvariantCulture, $"24 {height - (revised ? 288 : 264)} 0.24 {(revised ? 288 : 264)} re f\n");
        foreach (var top in new[] { 19.2, 33.6, 192.0, 216.0 })
            Word("ROW " + top.ToString(CultureInfo.InvariantCulture), top + (revised && top >= 100 ? 24 : 0));
        if (revised) Word("INSERT", 108);
        return RowFontDocument(content.ToString(), 240, height);

        void Word(string text, double top)
        {
            if (blocks)
            {
                // Coreの支持・除外ケースをPDF経路へ通すための自作図形＋不可視の文字層。
                content.Append(CultureInfo.InvariantCulture, $"2.4 {height - top - 4.8} 28.8 4.8 re f\n");
                content.Append(CultureInfo.InvariantCulture, $"BT 3 Tr /F1 6.857142857142857 Tf 1 0 0 1 2.4 {height - top - 4.8} Tm <{Convert.ToHexString(Encoding.ASCII.GetBytes(text))}> Tj ET\n");
            }
            else content.Append(CultureInfo.InvariantCulture,
                $"BT /F1 9.6 Tf 1 0 0 1 40 {height - top - 16.8} Tm <{Convert.ToHexString(Encoding.ASCII.GetBytes(text))}> Tj ET\n");
        }
    }
}
