using OpenCvSharp;
using PDFtoImage;
using SkiaSharp;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

// PDFium を含む疎通確認は逐次実行する。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace ReportDiff.Tests;

public sealed class DependencySmokeTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    public void PdfRendersAt300DpiAndConvertsToBgr()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("この疎通テストは macOS・Windows・Linux が対象です。");
        }

        using var pdf = new MemoryStream();
        using (var document = SKDocument.CreatePdf(pdf))
        {
            using var canvas = document.BeginPage(144, 216);
            canvas.Clear(SKColors.White);
            using var rectangle = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawRect(24, 24, 48, 48, rectangle);
            using var line = new SKPaint { Color = SKColors.Red, StrokeWidth = 4, IsAntialias = true };
            canvas.DrawLine(24, 144, 120, 144, line);
            document.EndPage();
            document.Close();
        }

        pdf.Position = 0;
        using var bitmap = Conversion.ToImage(pdf, page: 0, leaveOpen: true,
            options: new RenderOptions(Dpi: 300, WithAnnotations: true, WithFormFill: true,
                BackgroundColor: SKColors.White));
        // ストライドと画素の並びを固定してから、所有権を保ったまま Mat へ渡す。
        using var bgraBitmap = bitmap.Copy(SKColorType.Bgra8888);
        Assert.NotNull(bgraBitmap);
        using var bgra = Mat.FromPixelData(bgraBitmap.Height, bgraBitmap.Width,
            MatType.CV_8UC4, bgraBitmap.GetPixels(), bgraBitmap.RowBytes);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);

        Assert.Equal(MatType.CV_8UC3, bgr.Type());
        Assert.InRange(bgr.Width, 599, 601);
        Assert.InRange(bgr.Height, 899, 901);
        Assert.Equal(new Vec3b(0, 0, 0), bgr.At<Vec3b>(200, 200));
        Assert.Equal(new Vec3b(255, 255, 255), bgr.At<Vec3b>(50, 50));
        Assert.Equal(new Vec3b(0, 0, 255), bgr.At<Vec3b>(600, 300));
    }

    [Fact]
    public void OpenCvOperationsAndImageCodecsWorkWithoutFfmpeg()
    {
        var build = Cv2.GetBuildInformation();
        if (OperatingSystem.IsMacOS())
        {
            Assert.DoesNotMatch(@"FFMPEG:\s+YES", build);
            Assert.DoesNotMatch(@"To be built:[^\r\n]*\bvideoio\b", build);
        }
        // Windows slim のビルド情報はリンク前の OpenCV 全体の構成を含む。
        // 実際にロードしたラッパーに動画 API がないことも確認する。
        var native = NativeLibrary.Load("OpenCvSharpExtern", typeof(Cv2).Assembly, null);
        try
        {
            Assert.False(NativeLibrary.TryGetExport(native, "videoio_VideoCapture_new1", out _));
        }
        finally
        {
            NativeLibrary.Free(native);
        }

        using var bgr = new Mat(3, 3, MatType.CV_8UC3, Scalar.All(255));
        bgr.Set(1, 1, new Vec3b(0, 0, 0));
        using var normalized = new Mat();
        bgr.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
        using var lab = new Mat();
        Cv2.CvtColor(normalized, lab, ColorConversionCodes.BGR2Lab);
        Assert.Equal(MatType.CV_32FC3, lab.Type());
        Assert.InRange(lab.At<Vec3f>(0, 0).Item0, 99.99f, 100.01f);
        Assert.InRange(lab.At<Vec3f>(0, 0).Item1, -0.01f, 0.01f);
        Assert.InRange(lab.At<Vec3f>(0, 0).Item2, -0.01f, 0.01f);
        Assert.Equal(new Vec3f(0, 0, 0), lab.At<Vec3f>(1, 1));
        using var blurred = new Mat();
        Cv2.Blur(lab, blurred, new Size(3, 3));
        Assert.InRange(blurred.At<Vec3f>(1, 1).Item0, 88.88f, 88.90f);

        using var mask = new Mat(7, 7, MatType.CV_8UC1, Scalar.All(0));
        mask.Set(3, 3, (byte)255);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var dilated = new Mat();
        using var eroded = new Mat();
        Cv2.Dilate(mask, dilated, kernel);
        Cv2.Erode(dilated, eroded, kernel);
        Assert.Equal(9, Cv2.CountNonZero(dilated));
        Assert.Equal(1, Cv2.CountNonZero(eroded));
        Assert.Equal((byte)255, eroded.At<byte>(3, 3));

        using var components = mask.Clone();
        components.Set(0, 0, (byte)255);
        using var labels = new Mat();
        Assert.Equal(3, Cv2.ConnectedComponents(components, labels, PixelConnectivity.Connectivity8));
        Assert.Equal(MatType.CV_32SC1, labels.Type());
        Assert.NotEqual(labels.At<int>(0, 0), labels.At<int>(3, 3));

        using var transform = new Mat(2, 3, MatType.CV_64FC1, Scalar.All(0));
        transform.Set(0, 0, 1.0);
        transform.Set(1, 1, 1.0);
        transform.Set(0, 2, 1.0);
        using var shifted = new Mat();
        Cv2.WarpAffine(mask, shifted, transform, mask.Size(), InterpolationFlags.Nearest,
            BorderTypes.Replicate);
        Assert.Equal(1, Cv2.CountNonZero(shifted));
        Assert.Equal((byte)255, shifted.At<byte>(3, 4));
        Assert.Equal((byte)0, shifted.At<byte>(3, 3));

        // ランタイムを縮小しても帳票の対応形式を失わないことを確認する。
        foreach (var extension in new[] { ".png", ".jpg", ".bmp", ".tiff" })
        {
            Assert.True(Cv2.ImEncode(extension, bgr, out var encoded));
            using var decoded = Cv2.ImDecode(encoded, ImreadModes.Color);
            Assert.False(decoded.Empty());
            Assert.Equal(bgr.Size(), decoded.Size());
            Assert.Equal(MatType.CV_8UC3, decoded.Type());
            if (extension != ".jpg")
            {
                Assert.Equal(0.0, Cv2.Norm(bgr, decoded, NormTypes.INF));
            }
        }
    }
}
