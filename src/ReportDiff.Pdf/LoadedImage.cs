using OpenCvSharp;

namespace ReportDiff.Pdf;

/// <summary>BGR 8bit の Pixels を所有する。呼び出し側で using によって破棄する。</summary>
public sealed class LoadedImage : IDisposable
{
    internal LoadedImage(Mat pixels, InputFormat format, int dpi)
    {
        Pixels = pixels;
        Format = format;
        Dpi = dpi;
    }

    public Mat Pixels { get; }
    public InputFormat Format { get; }
    public int Dpi { get; }
    public void Dispose() => Pixels.Dispose();
}

public sealed class ImageReadException(string message, Exception? inner = null) : Exception(message, inner);
