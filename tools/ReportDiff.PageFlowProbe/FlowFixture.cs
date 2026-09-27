using System.Globalization;
using System.Text;

internal sealed record FlowCase(string Id, int Rows, string Mutation = "none", bool Footer = true);
internal sealed record FlowLink(int APage, int ASlot, int BPage, int BSlot, string Text);
internal sealed record FlowLayout(double Width = 240, double Height = 300, double Top = 72, double Step = 24,
    double FooterTop = 252, double FontSize = 9.6, double BaselineOffset = 16.8, double RuleOffset = 20, double LineWidth = 192);

// 自作Type3を各ページで共有。図形・文字層・ページ割りをOSフォントから独立させる。
internal static class FlowFixture
{
    internal const double Width = 240, Height = 300, Top = 72, Step = 24;
    internal const int Capacity = 6, Insertion = 2;
    internal static readonly FlowCase[] Cases = [new("R10", 10), new("R11", 12), new("chain3", 16),
        new("text-change", 10, "text"), new("same-text-pixels", 10, "pixels"),
        new("band-edge-tone", 10, "edge"), new("neighbor-tone", 10, "neighbor"),
        new("new-page-extra", 12, "extra"), new("repeated", 10, "repeated"), new("no-footer", 10, Footer: false)];

    internal static string[] Rows(FlowCase test, bool revised)
    {
        var result = Enumerable.Range(0, test.Rows).Select(i => $"ITEM {(char)('A' + i)}{(char)('A' + i)}{(char)('A' + i)}{(char)('A' + i)}").ToList();
        result[^1] = "SUM TOTAL 555";
        if (test.Mutation == "repeated") result[4] = result[5] = "ITEM SAME";
        if (revised)
        {
            result.Insert(Insertion, "NEW ADDED");
            if (test.Mutation == "text") result[Capacity] = "OTHER VALUE";
        }
        return result.ToArray();
    }

    internal static FlowLink[] Links(FlowCase test)
    {
        var rows = Rows(test, false);
        return Enumerable.Range(0, test.Rows).Where(i => i >= Insertion && i % Capacity == Capacity - 1)
            .Select(i => new FlowLink(i / Capacity + 1, i % Capacity, (i + 1) / Capacity + 1, 0, rows[i])).ToArray();
    }

    internal static byte[] Create(FlowCase test, bool revised, FlowLayout? layout = null, string[][]? explicitPages = null, IReadOnlySet<string>? faintRows = null,
        Func<int, string>? pageDrawing = null)
    {
        layout ??= new();
        var pages = explicitPages ?? Rows(test, revised).Chunk(Capacity).ToArray();
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "", "" };
        var glyphs = Enumerable.Range(32, 95).Select(c => (char)c).ToArray();
        var glyphIds = new Dictionary<char, int>();
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
            glyphIds[ch] = objects.Count + 1; objects.Add(Stream(shape.ToString()));
        }
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CIDSystemInfo << /Registry (Test) /Ordering (Unicode) /Supplement 0 >> def\n/CMapName /FlowProbe def /CMapType 2 def\n1 begincodespacerange <20> <7E> endcodespacerange\n95 beginbfchar\n");
        foreach (var ch in glyphs) cmap.Append(CultureInfo.InvariantCulture, $"<{(int)ch:X2}> <{(int)ch:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end\n");
        objects[3] = Stream(cmap.ToString());
        objects[2] = $"<< /Type /Font /Subtype /Type3 /Name /F1 /FontBBox [0 0 500 700] /FontMatrix [0.001 0 0 0.001 0 0] /FirstChar 32 /LastChar 126 /Widths [{string.Join(' ', Enumerable.Repeat(600, 95))}] /Resources << >> /Encoding << /Type /Encoding /Differences [32 {string.Join(' ', glyphs.Select(c => "/g" + (int)c))}] >> /CharProcs << {string.Join(' ', glyphs.Select(c => $"/g{(int)c} {glyphIds[c]} 0 R"))} >> /ToUnicode 4 0 R >>";
        var pageIds = new List<int>();
        for (var page = 0; page < pages.Length; page++)
        {
            var content = new StringBuilder("0 g\n");
            Text("HEAD START", 24); Text("SUB FIRST", 48);
            for (var slot = 0; slot < pages[page].Length; slot++)
            {
                var top = layout.Top + slot * layout.Step;
                if (faintRows?.Contains(pages[page][slot]) == true) content.Append("0.97 g\n");
                else if (revised && ((page == 1 && slot == 0 && test.Mutation == "pixels")
                    || (page == 0 && slot == 3 && test.Mutation == "paired")
                    || (page == 1 && slot == 4 && test.Mutation == "terminal-tone"))) content.Append("0.65 g\n");
                Text(pages[page][slot], top);
                if (faintRows?.Contains(pages[page][slot]) != true) content.Append("0 g\n");
                content.Append(CultureInfo.InvariantCulture, $"24 {layout.Height - top - layout.RuleOffset} {layout.LineWidth} 0.48 re f\n24 {layout.Height - top - layout.Step} 1.44 {layout.Step} re f\n");
                if (faintRows?.Contains(pages[page][slot]) == true) content.Append("0 g\n");
            }
            if (test.Footer) { Text("FOOT FIXED", layout.FooterTop); Text("END CHECK", layout.FooterTop + 24); }
            if (revised && page == 1 && test.Mutation is "edge" or "neighbor")
            {
                var y = layout.Top + (test.Mutation == "edge" ? 0 : -0.48);
                content.Append(CultureInfo.InvariantCulture, $"0.5 g 24 {layout.Height - y - 0.48} 8 0.48 re f\n");
            }
            if (revised && page == pages.Length - 1 && test.Mutation == "extra")
                content.Append("0.85 g 130 155 25 8 re f\n");
            if (revised && page == 0 && test.Mutation == "paired-edge")
                content.Append(CultureInfo.InvariantCulture, $"0.5 g 24 {layout.Height - layout.Top - 3 * layout.Step - 0.48} 8 0.48 re f\n");
            content.Append(pageDrawing?.Invoke(page));
            var pageId = objects.Count + 1; pageIds.Add(pageId);
            objects.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {layout.Width} {layout.Height}] /Resources << /Font << /F1 3 0 R >> >> /Contents {pageId + 1} 0 R >>"));
            objects.Add(Stream(content.ToString()));
            void Text(string text, double top)
            {
                var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(text));
                content.Append(CultureInfo.InvariantCulture, $"BT /F1 {layout.FontSize} Tf 1 0 0 1 40 {layout.Height - top - layout.BaselineOffset} Tm <{hex}> Tj ET\n");
            }
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(i => $"{i} 0 R"))}] /Count {pageIds.Count} >>";
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.7\n"); var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++) { offsets.Add(output.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = output.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
    private static string Stream(string text) => $"<< /Length {Encoding.ASCII.GetByteCount(text)} >>\nstream\n{text}endstream";
}
