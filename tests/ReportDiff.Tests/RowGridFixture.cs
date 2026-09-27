using System.Globalization;
using System.Text;

namespace ReportDiff.Tests;

internal static partial class PdfFixture
{
    internal sealed record MappedToneOptions(bool Changed, int? PatchDistance = null);

    internal static byte[] RowProbePage(bool inserted, bool changed, double ruleWidth, int? boundaryToneDistancePx = null,
        MappedToneOptions? mappedTone = null)
    {
        // 300dpi で 1 行 100px。グリフと文字コードの対応は A/B 共通。
        const double pageHeight = 300, top = 48, step = 24;
        var rows = new List<string> { "HEAD START", "SUB FIRST", "ITEM ALPHA", "ITEM BETA", "ITEM GAMMA", changed ? "SUM TOTAL 111" : "SUM TOTAL 555", "END CHECK" };
        if (inserted) rows.Insert(2, "NEW DELTA");
        var content = new StringBuilder("0 g\n");
        for (var row = 0; row < rows.Count; row++)
        {
            var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(rows[row]));
            content.Append(CultureInfo.InvariantCulture, $"BT /F1 9.6 Tf 1 0 0 1 40 {pageHeight - (top + row * step + 16.8)} Tm <{hex}> Tj ET\n");
            if (ruleWidth > 0)
                content.Append(CultureInfo.InvariantCulture, $"24 {pageHeight - (top + (row + 1) * step - 4)} 192 0.48 re f\n");
        }
        if (ruleWidth > 0)
            content.Append(CultureInfo.InvariantCulture, $"24 {pageHeight - (top + rows.Count * step - 4)} {ruleWidth} {rows.Count * step - 4} re f\n");
        if (boundaryToneDistancePx is int distance)
        {
            var y = top + 2 * step + (inserted ? step : 0) + distance * 0.24;
            content.Append(CultureInfo.InvariantCulture, $"0.25098039215686274 g 24 {pageHeight - y - 0.48} {ruleWidth} 0.48 re f\n");
        }
        if (mappedTone is not null)
        {
            // 特徴量写像の試行専用。単独の灰色の縦罫線は切断位置を横切る。
            var boundary = top + 2 * step + (inserted ? step : 0);
            content.Append(CultureInfo.InvariantCulture, $"{128.0 / 255} g 24 {pageHeight - (top + rows.Count * step - 4)} 1.44 {rows.Count * step - 4} re f\n");
            if (mappedTone.Changed)
                content.Append(CultureInfo.InvariantCulture, $"{160.0 / 255} g 24 {pageHeight - boundary - 0.48} 1.44 0.48 re f\n");
            if (inserted && mappedTone.PatchDistance is int patchDistance)
                content.Append(CultureInfo.InvariantCulture, $"0 g 24 {pageHeight - boundary + patchDistance * 0.24} 1.44 1.44 re f\n");
        }
        return RowFontDocument(content.ToString());
    }

    internal static byte[] RowScenario(string id, bool revised, double dxPoints = 0, double dyPoints = 0)
    {
        const int height = 300;
        var rows = new[] { "HEAD START", "SUB FIRST", "ITEM ALPHA", "ITEM BETA", "ITEM GAMMA", "ITEM DELTA", "SUM TOTAL 555", "END CHECK" };
        if (id == "R06") rows = ["HEAD START", "SUB FIRST", "ITEM SAME", "ITEM SAME", "ITEM SAME", "ITEM SAME", "SUM TOTAL 555", "END CHECK"];
        var content = new StringBuilder("0 g\n");
        if (dxPoints != 0 || dyPoints != 0) content.Append(CultureInfo.InvariantCulture, $"q 1 0 0 1 {dxPoints} {dyPoints} cm\n");
        var entries = new List<(string Text, double X, double Top, double Width)>();
        if (id is "R09" or "numeric_columns")
        {
            var left = new List<string> { "LEFTA", "LEFTB", "LEFTC", "LEFTD", "LEFTE", "LEFTF", "LEFTG", "LEFTH" };
            if (revised) left.Insert(2, "LEFTNEW");
            for (var i = 0; i < left.Count; i++) entries.Add((left[i], 32, 48 + 24 * i, 80));
            string[] numbers = ["31", "73", "14", "62", "28", "89", "40", "95"];
            for (var i = 0; i < 8; i++) entries.Add((id == "numeric_columns" ? numbers[i] : "RIGHT" + (char)('A' + i), 136, 48 + 24 * i, 80));
        }
        else
        {
            var inserted = 0; var serial = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                if (revised && (i == 2 || id == "R04" && i == 4))
                {
                    var word = id == "R06" ? "ITEM SAME" : "NEW ADDED";
                    if (id == "R07") word = $"{++serial:D2} ITEM NEW";
                    entries.Add((word, 40, 48 + 24 * (i + inserted), 192)); inserted++;
                }
                var text = revised && id == "R02" && i == 6 ? "SUM TOTAL 111" : rows[i];
                if (revised && i == 6) text = id switch
                {
                    "replacement" => "OTHER VALUE 111", "decimal" => "SUM TOTAL 5.55", "minus" => "SUM TOTAL -555", _ => text
                };
                if (id == "R07" && i is >= 2 and <= 5) text = $"{++serial:D2} {text}";
                var y = id == "R05" && i >= 6 ? 240 + 24 * (i - 6) : 48 + 24 * (i + inserted);
                entries.Add((text, 40, y, 192));
            }
        }
        foreach (var entry in entries)
        {
            if (id != "R08")
            {
                var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(entry.Text));
                content.Append(CultureInfo.InvariantCulture, $"BT /F1 9.6 Tf 1 0 0 1 {entry.X} {height - entry.Top - 16.8} Tm <{hex}> Tj ET\n");
            }
            var left = id is "R09" or "numeric_columns" ? entry.X - 8 : 24;
            content.Append(CultureInfo.InvariantCulture, $"{left} {height - entry.Top - 20} {entry.Width} 0.48 re f\n");
            // フッター前は純白。独立した縦罫線の区間を連結し、同じ反復帯の全画素を揃える。
            content.Append(CultureInfo.InvariantCulture, $"{left} {height - entry.Top - 24} 1.44 24 re f\n");
        }
        if (dxPoints != 0 || dyPoints != 0) content.Append("Q\n");
        return RowFontDocument(content.ToString());
    }

    private static byte[] RowFontDocument(string content, double width = 240, double height = 300)
    {
        var glyphs = Enumerable.Range(32, 95).Select(c => (char)c).ToArray();
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CIDSystemInfo << /Registry (Test) /Ordering (Unicode) /Supplement 0 >> def\n/CMapName /RowProbe def /CMapType 2 def\n1 begincodespacerange <20> <7E> endcodespacerange\n95 beginbfchar\n");
        foreach (var ch in glyphs) cmap.Append(CultureInfo.InvariantCulture, $"<{(int)ch:X2}> <{(int)ch:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end\n");
        var font = $"<< /Type /Font /Subtype /Type3 /Name /F1 /FontBBox [0 0 500 700] /FontMatrix [0.001 0 0 0.001 0 0] /FirstChar 32 /LastChar 126 /Widths [{string.Join(' ', Enumerable.Repeat(600, 95))}] /Resources << >> /Encoding << /Type /Encoding /Differences [32 {string.Join(' ', glyphs.Select(ch => "/g" + (int)ch))}] >> /CharProcs << {string.Join(' ', glyphs.Select((ch, i) => $"/g{(int)ch} {7 + i} 0 R"))} >> /ToUnicode 6 0 R >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            font, TextStream(content), TextStream(cmap.ToString())
        };
        foreach (var ch in glyphs)
        {
            var shape = new StringBuilder("600 0 0 0 500 700 d1\n");
            if (ch != ' ')
            {
                shape.Append("0 0 80 700 re f\n0 0 500 80 re f\n");
                for (var bit = 0; bit < 8; bit++)
                    if (((int)ch & (1 << bit)) != 0)
                        shape.Append(CultureInfo.InvariantCulture, $"{150 + bit % 2 * 200} {130 + bit / 2 * 140} 120 90 re f\n");
            }
            objects.Add(TextStream(shape.ToString()));
        }
        return CreateDocument(objects.ToArray());
    }
}
