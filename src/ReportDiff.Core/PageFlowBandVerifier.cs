using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
using static ReportDiff.Core.PageFlowInference;

namespace ReportDiff.Core;

/// <summary>元画像を再確認し、完全一致帯を元ページへ置換して両端・両順序を比較する。除外設定は証明に使用しない。</summary>
public static class PageFlowBandVerifier
{
    public sealed record Verification(Proposal Proposal, int OriginalComparisons);

    public static Verification Verify(Proposal proposal, PageFlowPageDescriptor source, PageFlowPageDescriptor target,
        Mat imageSource, Mat imageTarget, ComparisonParameters parameters, ComparisonParameters? targetParameters = null)
        => Verify(proposal, source.Original, target.Original, imageSource, imageTarget, parameters, targetParameters);

    public static Verification Verify(Proposal proposal, PageFlowOriginalIdentity source, PageFlowOriginalIdentity target,
        Mat imageSource, Mat imageTarget, ComparisonParameters parameters, ComparisonParameters? targetParameters = null)
    {
        if (proposal.Status != "candidate") return new(proposal, 0);
        VerifyOriginal(source, imageSource); VerifyOriginal(target, imageTarget);
        var from = proposal.Source; var to = proposal.Target;
        if (from is null || to is null || from.Page != source.Key || to.Page != target.Key
            || from.Page.Side == to.Page.Side || (long)from.Page.Page + 1 != to.Page.Page
            || from.Bottom > source.Size.Height || to.Bottom > target.Size.Height
            || from.Height != to.Height || source.Size.Width != target.Size.Width)
            return Skip("invalid_flow_endpoint");
        using var a = new Mat(imageSource, new Rect(0, from.Top, source.Size.Width, from.Height));
        using var b = new Mat(imageTarget, new Rect(0, to.Top, target.Size.Width, to.Height));
        if (Cv2.Norm(a, b, NormTypes.INF) != 0) return Skip("nonidentical_band_not_proven");
        // 実効設定に加えて、許容移動なしの検査も実施する。別の領域の除外・優先順位で証拠を隠さない。
        targetParameters ??= parameters;
        var profiles = new[] { parameters.Diff, targetParameters.Diff, new DiffOptions { MaxShiftMm = 0, EdgeTolerance = 0 },
            new DiffOptions { MaxShiftMm = .30 } }.Concat(parameters.Regions.Concat(targetParameters.Regions).Where(r => r.Mode == "compare").Select(r => r.Diff)).Distinct().ToArray();
        var comparisons = 0;
        foreach (var (original, band, donor, settings) in new[] { (imageSource, from, b, parameters), (imageTarget, to, a, targetParameters) })
        {
            using var replaced = original.Clone();
            using (var destination = new Mat(replaced, new Rect(0, band.Top, original.Width, band.Height))) donor.CopyTo(destination);
            if (Cv2.Norm(original, replaced, NormTypes.INF) != 0) return Skip("original_substitution_not_proven");
            foreach (var diff in profiles)
            foreach (var reverse in new[] { false, true })
            {
                using var comparison = PageComparer.Compare(reverse ? replaced : original, reverse ? original : replaced,
                    settings with { Diff = diff, Exclude = [], Regions = [] });
                comparisons++;
                if (comparison.RawPixels != 0) return Skip("original_substitution_not_proven", comparisons);
            }
        }
        return new(proposal with { Status = "band_verified", Reason = null }, comparisons);
        Verification Skip(string reason, int count = 0) => new(proposal with { Status = "skipped", Reason = reason }, count);
    }

    /// <summary>ストライドを含まない元BGRの再読込検査。変更は送りの見送りではなく処理エラーにする。</summary>
    public static void VerifyOriginal(PageFlowPageDescriptor descriptor, Mat image) => VerifyOriginal(descriptor.Original, image);

    public static void VerifyOriginal(PageFlowOriginalIdentity descriptor, Mat image)
    {
        if (image.Empty() || image.Dims != 2 || image.Type() != MatType.CV_8UC3 || image.Size() != descriptor.Size)
            throw new InvalidOperationException("flow_original_changed: 元画像の寸法または形式が変わりました。");
        if (Digest(image) != descriptor.PixelSha256)
            throw new InvalidOperationException("flow_original_changed: 記述収集後に元画像が変わりました。");
    }

    internal static string Digest(Mat image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var size = image.Size(); var row = new byte[size.Width * 3];
        for (var y = 0; y < size.Height; y++)
        {
            Marshal.Copy(image.Ptr(y), row, 0, row.Length); hash.AppendData(row);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
