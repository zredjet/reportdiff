using System.Buffers.Binary;
using System.Text;

namespace ReportDiff.Tests;

internal static partial class PdfFixture
{
    internal const string StandardFont = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    internal const string FontText = "BT /F1 12 Tf 20 40 Td (A) Tj ET\n";
    internal const string EmbeddedTrueType = "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+ReportDiffTest /Encoding /WinAnsiEncoding /FirstChar 65 /LastChar 65 /Widths [600] /FontDescriptor 5 0 R >>";
    internal const string TrueTypeDescriptor = "<< /Type /FontDescriptor /FontName /ABCDEF+ReportDiffTest /Flags 32 /FontBBox [0 0 500 700] /ItalicAngle 0 /Ascent 700 /Descent 0 /CapHeight 700 /StemV 80 /FontFile2 6 0 R >>";

    public static byte[] CreateFontResourceCycle() => CreateDocument(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 4 0 R >> /XObject << /Fm 6 0 R >> >> /Contents 5 0 R >>",
        StandardFont, TextStream(FontText),
        TextStream("", "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /XObject << /Loop 6 0 R >> >>")
    ]);

    // 全フォント・字形は自作。3=フォント、5=Descriptor、6=プログラム、7=子フォント。
    public static byte[] CreateFontReport(string font = StandardFont, string? secondFont = null,
        string[]? contents = null, bool direct = false, bool form = false, bool annotation = false,
        bool pattern = false, bool inheritedResources = false, int rotation = 0,
        string descriptor = TrueTypeDescriptor, string? program = null, string descendant = "null")
    {
        contents ??= [FontText];
        var resources = $"<< /Font << /F1 {(direct ? font : "3 0 R")} /Alias 3 0 R /F2 4 0 R >> /XObject << /Fm 9 0 R >> {(pattern ? "/Pattern << /P1 11 0 R >>" : "")} >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, contents.Length).Select(i => $"{12 + 2*i} 0 R"))}] /Count {contents.Length} {(inheritedResources ? "/Resources " + resources : "")} >>",
            font, secondFont ?? StandardFont, descriptor,
            program ?? TextStream(Convert.ToHexString(CreateTestTrueType()) + ">\n", "/Filter /ASCIIHexDecode"),
            descendant, TextStream("600 0 0 0 500 700 d1 0 0 500 700 re f\n"),
            TextStream(FontText, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /F1 3 0 R >> >>"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (test) /V (A) /Rect [0 0 100 100] /F 4 /AP << /N 9 0 R >> >>",
            TextStream(FontText, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100 /Resources << /Font << /F1 3 0 R >> >>")
        };
        for (var i = 0; i < contents.Length; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Rotate {rotation} {(inheritedResources ? "" : "/Resources " + resources)} /Contents {13 + 2 * i} 0 R {(annotation ? "/Annots [10 0 R]" : "")} >>");
            objects.Add(TextStream(form ? "/Fm Do\n" : contents[i]));
        }
        return CreateDocument(objects.ToArray());
    }

    // .notdef と三角形の A の 2 字形だけを持つ最小 TrueType。外部フォントを配布しない。
    internal static byte[] CreateTestTrueType()
    {
        static void U16(byte[] a, int i, int v) => BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(i), unchecked((ushort)v));
        static void U32(byte[] a, int i, uint v) => BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(i), v);
        static uint Sum(byte[] bytes)
        {
            uint sum = 0;
            for (var i = 0; i < bytes.Length; i += 4)
            {
                uint value = 0;
                for (var j = 0; j < 4; j++) value = (value << 8) | (i + j < bytes.Length ? bytes[i + j] : 0u);
                sum = unchecked(sum + value);
            }
            return sum;
        }
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var head = new byte[54]; U32(head, 0, 0x10000); U32(head, 4, 0x10000); U32(head, 12, 0x5f0f3cf5);
        U16(head, 18, 1000); U16(head, 40, 500); U16(head, 42, 700); U16(head, 46, 8); U16(head, 48, 2);
        tables["head"] = head;
        var hhea = new byte[36]; U32(hhea, 0, 0x10000); U16(hhea, 4, 700); U16(hhea, 10, 600);
        U16(hhea, 16, 500); U16(hhea, 18, 1); U16(hhea, 34, 2); tables["hhea"] = hhea;
        var maxp = new byte[32]; U32(maxp, 0, 0x10000); U16(maxp, 4, 2); U16(maxp, 6, 3); U16(maxp, 8, 1);
        U16(maxp, 14, 2); tables["maxp"] = maxp;
        var hmtx = new byte[8]; U16(hmtx, 0, 600); U16(hmtx, 4, 600); tables["hmtx"] = hmtx;
        // .notdef は 12 bytes、A は 32 bytes（4 bytes 境界で埋める）。
        var glyf = new byte[44]; U16(glyf, 12, 1); U16(glyf, 18, 500); U16(glyf, 20, 700);
        U16(glyf, 22, 2); glyf[26] = 1; glyf[27] = 1; glyf[28] = 1;
        U16(glyf, 29, 0); U16(glyf, 31, 500); U16(glyf, 33, -250);
        U16(glyf, 35, 0); U16(glyf, 37, 0); U16(glyf, 39, 700); tables["glyf"] = glyf;
        var loca = new byte[6]; U16(loca, 2, 6); U16(loca, 4, 22); tables["loca"] = loca;
        var cmap = new byte[44]; U16(cmap, 2, 1); U16(cmap, 4, 3); U16(cmap, 6, 1); U32(cmap, 8, 12);
        U16(cmap, 12, 4); U16(cmap, 14, 32); U16(cmap, 18, 4); U16(cmap, 20, 4); U16(cmap, 22, 1);
        U16(cmap, 26, 65); U16(cmap, 28, 65535); U16(cmap, 32, 65); U16(cmap, 34, 65535);
        U16(cmap, 36, -64); U16(cmap, 38, 1); tables["cmap"] = cmap;
        var post = new byte[32]; U32(post, 0, 0x30000); tables["post"] = post;
        var name = Encoding.BigEndianUnicode.GetBytes("ReportDiffTest");
        var names = new byte[18 + name.Length]; U16(names, 2, 1); U16(names, 4, 18);
        U16(names, 6, 3); U16(names, 8, 1); U16(names, 10, 0x409); U16(names, 12, 1); U16(names, 14, name.Length);
        name.CopyTo(names, 18); tables["name"] = names;
        var os2 = new byte[78]; U16(os2, 2, 600); U16(os2, 4, 400); U16(os2, 6, 5);
        U16(os2, 64, 65); U16(os2, 66, 65); U16(os2, 68, 700); U16(os2, 74, 700); tables["OS/2"] = os2;
        var offset = 12 + 16 * tables.Count;
        var result = new byte[offset + tables.Values.Sum(t => (t.Length + 3) / 4 * 4)];
        U32(result, 0, 0x10000); U16(result, 4, tables.Count); U16(result, 6, 128); U16(result, 8, 3); U16(result, 10, 32);
        var record = 12; var headOffset = 0;
        foreach (var (tag, bytes) in tables)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(result, record); U32(result, record + 4, Sum(bytes));
            U32(result, record + 8, (uint)offset); U32(result, record + 12, (uint)bytes.Length);
            bytes.CopyTo(result, offset); if (tag == "head") headOffset = offset;
            offset += (bytes.Length + 3) / 4 * 4; record += 16;
        }
        U32(result, headOffset + 8, unchecked(0xb1b0afba - Sum(result)));
        return result;
    }
}
