using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ReportDiff.Pdf;

public static class ImageReader
{
    public static LoadedImage Read(string path, int imageDpi = 300)
    {
        byte[] data;
        try
        {
            // 日本語パスも .NET 側だけで扱い、ネイティブにはバイト列を渡す。
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ImageReadException($"画像ファイルを読み込めません: {path}", ex);
        }
        return Decode(data, imageDpi);
    }

    public static LoadedImage Decode(ReadOnlySpan<byte> data, int imageDpi = 300)
    {
        if (imageDpi is < 72 or > 1200) throw new ImageReadException("画像の DPI は 72〜1200 にしてください。");
        var format = InputFormatDetector.Detect(data);
        if (format is InputFormat.Unknown or InputFormat.Pdf)
            throw new ImageReadException("画像は PNG・JPEG・BMP・TIFF のいずれかを指定してください。");
        try
        {
            // アルファを保持する。TIFF も先頭ページだけをデコードする。
            using var decoded = Cv2.ImDecode(data, ImreadModes.Unchanged);
            if (decoded.Empty()) throw new ImageReadException("画像データを読み込めません。破損していないか確認してください。");
            var depth = decoded.Depth();
            if (depth != MatType.CV_8U && depth != MatType.CV_16U)
                throw new ImageReadException("この画素形式には対応していません。8bit または 16bit の符号なし整数画像を使用してください。");
            var channels = decoded.Channels();
            if (channels is not (1 or 3 or 4)) throw new ImageReadException("画像のチャンネル構成に対応していません。");
            var associated = format == InputFormat.Tiff && channels == 4
                && (depth == MatType.CV_8U || TiffAlpha.IsAssociated(data));
            return new LoadedImage(Normalize(decoded, channels, depth, associated), format, imageDpi);
        }
        catch (OpenCVException ex)
        {
            throw new ImageReadException("画像データを読み込めません。形式または破損を確認してください。", ex);
        }
    }

    private static Mat Normalize(Mat decoded, int channels, int depth, bool associated)
    {
        var output = new Mat();
        try
        {
            var maximum = depth == MatType.CV_8U ? 255.0 : 65535.0;
            if (channels == 3)
            {
                decoded.ConvertTo(output, MatType.CV_8UC3, 255.0 / maximum);
            }
            else if (channels == 1)
            {
                using var gray = new Mat();
                decoded.ConvertTo(gray, MatType.CV_8UC1, 255.0 / maximum);
                Cv2.CvtColor(gray, output, ColorConversionCodes.GRAY2BGR);
            }
            else
            {
                using var normalized = new Mat();
                decoded.ConvertTo(normalized, MatType.CV_32FC4, 1.0 / maximum);
                var rows = decoded.Rows;
                var cols = decoded.Cols;
                output.Create(rows, cols, MatType.CV_8UC3);
                var source = new float[checked(cols * 4)];
                var target = new byte[checked(cols * 3)];
                for (var y = 0; y < rows; y++)
                {
                    Marshal.Copy(normalized.Ptr(y), source, 0, source.Length);
                    for (var x = 0; x < cols; x++)
                    {
                        var alpha = source[x * 4 + 3];
                        for (var c = 0; c < 3; c++)
                        {
                            var color = source[x * 4 + c];
                            var value = alpha == 0 ? 1 : (associated ? color : color * alpha) + 1 - alpha;
                            target[x * 3 + c] = (byte)Math.Clamp((int)MathF.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
                        }
                    }
                    Marshal.Copy(target, 0, output.Ptr(y), target.Length);
                }
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }
}
