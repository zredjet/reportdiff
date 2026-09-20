namespace ReportDiff.Pdf;

public enum InputFormat
{
    Unknown,
    Pdf,
    Png,
    Jpeg,
    Bmp,
    Tiff
}

public static class InputFormatDetector
{
    /// <summary>拡張子を使わず、ファイル先頭のシグネチャを調べる。内容の検証はデコーダーが行う。</summary>
    public static InputFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("%PDF-"u8)) return InputFormat.Pdf;
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return InputFormat.Png;
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return InputFormat.Jpeg;
        if (data.StartsWith("BM"u8)) return InputFormat.Bmp;
        if (data.StartsWith(new byte[] { 73, 73, 42, 0 }) || data.StartsWith(new byte[] { 77, 77, 0, 42 })
            || data.StartsWith(new byte[] { 73, 73, 43, 0, 8, 0, 0, 0 })
            || data.StartsWith(new byte[] { 77, 77, 0, 43, 0, 8, 0, 0 })) return InputFormat.Tiff;
        return InputFormat.Unknown;
    }
}
