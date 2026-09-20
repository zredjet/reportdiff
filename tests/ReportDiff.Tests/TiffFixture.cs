using System.Buffers.Binary;

namespace ReportDiff.Tests;

// 非圧縮 TIFF を .NET だけで作る。エンコーダーのアルファ解釈に依存しない入力にする。
internal static class TiffFixture
{
    public static byte[] Create(bool littleEndian, int depth, int channels, ushort[][] pages,
        ushort? alphaMode = null, bool bigTiff = false)
    {
        var valueSize = bigTiff ? 8 : 4;
        var headerSize = bigTiff ? 16 : 8;
        var entryCount = 9 + (channels > 1 ? 1 : 0) + (alphaMode.HasValue ? 1 : 0);
        var directorySize = (bigTiff ? 8 : 2) + entryCount * (bigTiff ? 20 : 12) + valueSize;
        var bitsInline = channels * 2 <= valueSize;
        var bitsOffset = headerSize + pages.Length * directorySize;
        var pixelOffset = bitsOffset + (bitsInline ? 0 : channels * 2);
        using var stream = new MemoryStream();
        stream.WriteByte(littleEndian ? (byte)'I' : (byte)'M');
        stream.WriteByte(littleEndian ? (byte)'I' : (byte)'M');
        U16(bigTiff ? (ushort)43 : (ushort)42);
        if (bigTiff) { U16(8); U16(0); }
        Offset((ulong)headerSize);
        for (var page = 0; page < pages.Length; page++)
        {
            if (bigTiff) U64((ulong)entryCount); else U16((ushort)entryCount);
            Field(256, 4, 1, (ulong)(pages[page].Length / channels));
            Field(257, 4, 1, 1);
            Field(258, 3, (ulong)channels, (ulong)bitsOffset);
            Field(259, 3, 1, 1);
            Field(262, 3, 1, channels > 1 ? 2UL : 1UL);
            Field(273, 4, 1, (ulong)pixelOffset);
            Field(277, 3, 1, (ulong)channels);
            Field(278, 4, 1, 1);
            Field(279, 4, 1, (ulong)(pages[page].Length * depth / 8));
            if (channels > 1) Field(284, 3, 1, 1);
            if (alphaMode.HasValue) Field(338, 3, 1, alphaMode.Value);
            Offset(page + 1 < pages.Length ? (ulong)(headerSize + (page + 1) * directorySize) : 0);
            pixelOffset += pages[page].Length * depth / 8;
        }
        if (!bitsInline) for (var i = 0; i < channels; i++) U16((ushort)depth);
        foreach (var page in pages)
        foreach (var value in page)
            if (depth == 8) stream.WriteByte((byte)value); else U16(value);
        return stream.ToArray();

        void Field(ushort tag, ushort type, ulong count, ulong value)
        {
            U16(tag); U16(type);
            if (bigTiff) U64(count); else U32((uint)count);
            if (tag == 258 && bitsInline)
            {
                for (var i = 0; i < channels; i++) U16((ushort)depth);
                stream.Write(new byte[valueSize - channels * 2]);
            }
            else if (type == 3 && count == 1)
            {
                U16((ushort)value); stream.Write(new byte[valueSize - 2]);
            }
            else if (type == 4 && count == 1)
            {
                U32((uint)value); stream.Write(new byte[valueSize - 4]);
            }
            else Offset(value);
        }
        void Offset(ulong value) { if (bigTiff) U64(value); else U32((uint)value); }
        void U16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            else BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }
        void U32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            else BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
        void U64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            if (littleEndian) BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            else BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
