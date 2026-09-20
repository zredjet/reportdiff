using System.Buffers.Binary;

namespace ReportDiff.Pdf;

internal static class TiffAlpha
{
    // 16bit TIFF は OpenCV が画素をそのまま返すため、先頭 IFD の ExtraSamples を読む。
    // 8bit は LibTIFF の RGBA 経路で乗算済みになるので、この判定を使わない。
    public static bool IsAssociated(ReadOnlySpan<byte> data)
    {
        var little = data[0] == (byte)'I';
        var bigTiff = Read(data, 2, 2, little) == 43;
        var countSize = bigTiff ? 8 : 2;
        var entrySize = bigTiff ? 20 : 12;
        var directory = Read(data, bigTiff ? 8UL : 4UL, bigTiff ? 8 : 4, little);
        var count = Read(data, directory, countSize, little);
        if (count > (ulong)data.Length / (ulong)entrySize) throw InvalidHeader();
        var entries = checked(directory + (ulong)countSize);
        for (ulong i = 0; i < count; i++)
        {
            var entry = checked(entries + i * (ulong)entrySize);
            if (Read(data, entry, 2, little) != 338) continue;
            if (Read(data, entry + 2, 2, little) != 3
                || Read(data, entry + 4, bigTiff ? 8 : 4, little) != 1) throw InvalidHeader();
            return Read(data, entry + (bigTiff ? 12UL : 8UL), 2, little) != 2;
        }
        // 4 チャンネルでタグがない場合は LibTIFF と同じ扱いにする。
        return true;
    }

    private static ulong Read(ReadOnlySpan<byte> data, ulong offset, int size, bool little)
    {
        if (offset > (ulong)data.Length || (ulong)size > (ulong)data.Length - offset) throw InvalidHeader();
        var bytes = data.Slice((int)offset, size);
        return size switch
        {
            2 => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
            4 => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes),
            8 => little ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes),
            _ => throw InvalidHeader()
        };
    }

    private static ImageReadException InvalidHeader() => new("TIFF の透明度情報が不正です。");
}
