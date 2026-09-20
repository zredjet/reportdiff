using System.Globalization;
using System.Text;

namespace ReportDiff.Tests;

internal sealed record TextRun(string Text, double X, double Y, double Size = 12);

internal static partial class PdfFixture
{
    // 文字の図形・Unicode 対応を PDF 内に定義する自作 Type3 フォント。
    // OS フォント・代替フォント・第三者のフォントファイルに依存しない。
    public static byte[] CreateTextPage(IEnumerable<TextRun> runs, string media = "0 0 240 300",
        string? crop = null, int rotation = 0, int userUnit = 1, bool brokenText = false, bool inheritedRotation = false)
    {
        var items = runs.ToArray();
        var glyphs = items.SelectMany(r => r.Text.EnumerateRunes()).Distinct().ToArray();
        if (glyphs.Length > 254) throw new ArgumentException("合成フォントの文字種が多すぎます。");
        var codes = glyphs.Select((r, i) => (r, code: i + 1)).ToDictionary(x => x.r, x => x.code);
        var content = new StringBuilder();
        foreach (var run in items)
        {
            var hex = string.Concat(run.Text.EnumerateRunes().Select(r => codes[r].ToString("X2", CultureInfo.InvariantCulture)));
            content.Append(CultureInfo.InvariantCulture, $"BT /F1 {run.Size} Tf 1 0 0 1 {run.X} {run.Y} Tm <{hex}> Tj ET\n");
        }
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CIDSystemInfo << /Registry (Test) /Ordering (Unicode) /Supplement 0 >> def\n/CMapName /Test def /CMapType 2 def\n1 begincodespacerange <00> <FF> endcodespacerange\n");
        foreach (var chunk in glyphs.Chunk(100))
        {
            cmap.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach (var r in chunk)
                cmap.Append(CultureInfo.InvariantCulture, $"<{codes[r]:X2}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(r.ToString()))}>\n");
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap CMapName currentdict /CMap defineresource pop end end\n");
        var font = $"<< /Type /Font /Subtype /Type3 /Name /F1 /FontBBox [0 0 500 700] /FontMatrix [0.001 0 0 0.001 0 0] /FirstChar 1 /LastChar {Math.Max(1, glyphs.Length)} /Widths [{string.Join(' ', Enumerable.Repeat("600", Math.Max(1, glyphs.Length)))}] /Resources << >> /Encoding << /Type /Encoding /Differences [1 {string.Join(' ', glyphs.Select(r => "/g" + codes[r]))}] >> /CharProcs << {string.Join(' ', glyphs.Select(r => $"/g{codes[r]} {codes[r] + 6} 0 R"))} >> /ToUnicode 6 0 R >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [3 0 R] /Count 1 {(inheritedRotation ? $"/Rotate {rotation}" : "")} >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [{media}] {(crop is null ? "" : $"/CropBox [{crop}]")} {(inheritedRotation ? "" : $"/Rotate {rotation}")} /UserUnit {userUnit} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            font, TextStream(content.ToString()), TextStream(cmap.ToString(), brokenText ? "/Filter /UnsupportedTestFilter" : "")
        };
        foreach (var r in glyphs)
        {
            var shape = new StringBuilder("600 0 0 0 500 700 d1\n");
            if (!Rune.IsWhiteSpace(r))
            {
                shape.Append("0 0 80 700 re f\n0 0 500 80 re f\n");
                for (var bit = 0; bit < 8; bit++)
                    if ((r.Value & (1 << bit)) != 0)
                        shape.Append(CultureInfo.InvariantCulture, $"{150 + bit % 2 * 200} {130 + bit / 2 * 140} 120 90 re f\n");
            }
            objects.Add(TextStream(shape.ToString()));
        }
        return CreateDocument(objects.ToArray());
    }

    private static string TextStream(string text, string options = "") => $"<< /Length {Encoding.ASCII.GetByteCount(text)} {options} >>\nstream\n{text}endstream";
}
