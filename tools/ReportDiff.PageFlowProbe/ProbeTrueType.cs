using System.Buffers.Binary;
using System.Text;

// PdfFontFixture.CreateTestTrueTypeと同じ最小構造をASCIIへ拡張。字形も自作でOSフォントを使わない。
internal static class ProbeTrueType
{
    internal static byte[] Create()
    {
        const int count = 96;
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var head = new byte[54]; U32(head, 0, 0x10000); U32(head, 4, 0x10000); U32(head, 12, 0x5f0f3cf5);
        U16(head, 18, 1000); U16(head, 40, 500); U16(head, 42, 700); U16(head, 46, 8); U16(head, 48, 2);
        tables["head"] = head;
        var hhea = new byte[36]; U32(hhea, 0, 0x10000); U16(hhea, 4, 700); U16(hhea, 10, 600);
        U16(hhea, 16, 500); U16(hhea, 18, 1); U16(hhea, 34, count); tables["hhea"] = hhea;
        var maxp = new byte[32]; U32(maxp, 0, 0x10000); U16(maxp, 4, count); U16(maxp, 6, 40); U16(maxp, 8, 10);
        U16(maxp, 14, 2); tables["maxp"] = maxp;
        var hmtx = new byte[count * 4];
        for (var i = 0; i < count; i++) U16(hmtx, i * 4, 600);
        tables["hmtx"] = hmtx;
        using var glyphData = new MemoryStream();
        var loca = new byte[(count + 1) * 2];
        for (var i = 0; i < count; i++)
        {
            U16(loca, i * 2, (int)glyphData.Length / 2);
            var polygons = new List<(int X, int Y)[]>();
            if (i > 1)
            {
                // 左は三角形。右の矩形群で各コードを区別し、斜辺の描画も含める。
                polygons.Add([(0, 0), (100, 700), (140, 0)]);
                for (var bit = 0; bit < 7; bit++)
                    if (((i + 31) & (1 << bit)) != 0)
                    {
                        var x = 160 + bit % 2 * 200; var y = 50 + bit / 2 * 160;
                        polygons.Add([(x, y), (x, y + 110), (x + 130, y + 110), (x + 130, y)]);
                    }
            }
            var points = polygons.SelectMany(p => p).ToArray();
            var glyph = new byte[12 + polygons.Count * 2 + points.Length * 5];
            U16(glyph, 0, polygons.Count); U16(glyph, 6, 500); U16(glyph, 8, 700);
            var n = 0;
            for (var c = 0; c < polygons.Count; c++) { n += polygons[c].Length; U16(glyph, 10 + c * 2, n - 1); }
            var at = 12 + polygons.Count * 2;
            for (var p = 0; p < points.Length; p++) glyph[at + p] = 1;
            at += points.Length; var px = 0; var py = 0;
            foreach (var (x, _) in points) { U16(glyph, at, x - px); px = x; at += 2; }
            foreach (var (_, y) in points) { U16(glyph, at, y - py); py = y; at += 2; }
            glyphData.Write(glyph);
            while (glyphData.Length % 4 != 0) glyphData.WriteByte(0);
        }
        U16(loca, count * 2, (int)glyphData.Length / 2); tables["glyf"] = glyphData.ToArray(); tables["loca"] = loca;
        var cmap = new byte[44]; U16(cmap, 2, 1); U16(cmap, 4, 3); U16(cmap, 6, 1); U32(cmap, 8, 12);
        U16(cmap, 12, 4); U16(cmap, 14, 32); U16(cmap, 18, 4); U16(cmap, 20, 4); U16(cmap, 22, 1);
        U16(cmap, 26, 126); U16(cmap, 28, 65535); U16(cmap, 32, 32); U16(cmap, 34, 65535);
        U16(cmap, 36, -31); U16(cmap, 38, 1); tables["cmap"] = cmap;
        var post = new byte[32]; U32(post, 0, 0x30000); tables["post"] = post;
        var name = Encoding.BigEndianUnicode.GetBytes("ReportDiffFlowProbe");
        var names = new byte[18 + name.Length]; U16(names, 2, 1); U16(names, 4, 18);
        U16(names, 6, 3); U16(names, 8, 1); U16(names, 10, 0x409); U16(names, 12, 1); U16(names, 14, name.Length);
        name.CopyTo(names, 18); tables["name"] = names;
        var os2 = new byte[78]; U16(os2, 2, 600); U16(os2, 4, 400); U16(os2, 6, 5);
        U16(os2, 64, 32); U16(os2, 66, 126); U16(os2, 68, 700); U16(os2, 74, 700); tables["OS/2"] = os2;
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
    private static void U16(byte[] a, int i, int v) => BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(i), unchecked((ushort)v));
    private static void U32(byte[] a, int i, uint v) => BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(i), v);
    private static uint Sum(byte[] bytes)
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
}
