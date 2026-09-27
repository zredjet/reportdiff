using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>一面のA/Bカラー画像を所有する。次の面を生成する前に破棄する。</summary>
public sealed class AnchoredImagePair : IDisposable
{
    public Mat A { get; }
    public Mat B { get; }
    internal AnchoredImagePair(Mat a, Mat b) { A = a; B = b; }
    public void Dispose() { A.Dispose(); B.Dispose(); }
}

public sealed partial class AnchoredContentPlan
{
    internal void CheckCurrent() { CheckAlive(); VerifyBinding(previous!, Settings); }
    internal PageFlowPageDescriptor Descriptor(PageFlowPageKey key)
    { CheckAlive(); return document!.Pages.Single(p => p.Key == key); }

    internal Mat ReadVerified(PageFlowPageKey key, Func<PageFlowPageKey, Mat> read)
    {
        CheckCurrent();
        using var reservation = new AnchoredReservations(Budget);
        reservation.Add(AnchoredAllocation.RowBuffer, Descriptor(key).Size.Width);
        var image = read(key);
        try { PageFlowBandVerifier.VerifyOriginal(Descriptor(key), image); return image; }
        catch { image.Dispose(); throw; }
    }

    /// <summary>readは所有権を渡す新しいMatを返す。元画像は一枚ずつ照合・転写して解放する。</summary>
    public AnchoredImagePair RenderContent(int page, Func<PageFlowPageKey, Mat> read)
    {
        CheckCurrent(); var surface = Surfaces.Single(s => s.Id.OwnerPage == page);
        return Render(surface.Size, surface.Pieces, read);
    }

    public AnchoredImagePair RenderDisplay(int page, Func<PageFlowPageKey, Mat> read)
    {
        CheckCurrent(); var display = Displays.Single(d => d.Page == page);
        return Render(display.Size, display.Segments.Select(s => s.Band), read);
    }

    private AnchoredImagePair Render(Size size, IEnumerable<AnchoredContentPiece> pieces, Func<PageFlowPageKey, Mat> read)
    {
        Mat? a = null, b = null;
        try
        {
            a = new(size, MatType.CV_8UC3, Scalar.White); b = new(size, MatType.CV_8UC3, Scalar.White);
            // 元ページごとに一回だけ読む。全元画像のキャッシュは作らない。
            foreach (var descriptor in document!.Pages)
            {
                if (!pieces.Any(p => p.A?.Page == descriptor.Key || p.B?.Page == descriptor.Key)) continue;
                using var original = ReadVerified(descriptor.Key, read);
                foreach (var piece in pieces)
                {
                    var span = descriptor.Key.Side == PageSpace.A ? piece.A : piece.B;
                    if (span?.Page != descriptor.Key) continue;
                    using var source = new Mat(original, new Rect(0, span.Top, size.Width, span.Height));
                    using var target = new Mat(descriptor.Key.Side == PageSpace.A ? a : b, new Rect(0, piece.Top, size.Width, piece.Height));
                    source.CopyTo(target);
                }
            }
            var pair = new AnchoredImagePair(a, b); a = b = null; return pair;
        }
        finally { a?.Dispose(); b?.Dispose(); }
    }
}
