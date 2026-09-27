using OpenCvSharp;

namespace ReportDiff.Pdf;

/// <summary>BGR 8bit の Pixels を所有する。呼び出し側で using によって破棄する。</summary>
public sealed class LoadedImage : IDisposable
{
    internal LoadedImage(Mat pixels, InputFormat format, int dpi)
    {
        this.pixels = pixels;
        Format = format;
        Dpi = dpi;
    }

    private Mat? pixels;
    public Mat Pixels => pixels ?? throw new ObjectDisposedException(nameof(LoadedImage));
    /// <summary>画像の所有権を呼出側へ移す。返却されたMatは呼出側がDisposeする。</summary>
    public Mat TakePixels()
    { var result = Pixels; pixels = null; return result; }
    public InputFormat Format { get; }
    public int Dpi { get; }
    public void Dispose() { pixels?.Dispose(); pixels = null; }
}

public sealed class ImageReadException(string message, Exception? inner = null) : Exception(message, inner);
