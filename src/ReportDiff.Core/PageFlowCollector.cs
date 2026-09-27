using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>送り経路だけの計算予算。超過は通常比較の中止ではなく、文書全体の送り見送りにする。</summary>
public static class PageFlowLimits
{
    public const int MaximumSelectedPages = 128;
    public const int MaximumLines = 32_768;
    public const long MaximumTextCharacters = 2_097_152;
    public const long MaximumPixels = 512L * 1024 * 1024;
    public const long MaximumDescriptorBytes = 64L * 1024 * 1024;
    public const int MaximumCandidates = 128;

    // 行ハッシュ32B、非白フラグ1B、行/ページの固定費とUTF-16本文の計上量。実RSSの保証値ではない。
    internal static long DescriptorBytes(int height, int lines, long characters) => checked(96L + 33L * height + 96L * lines + 2L * characters);
    internal static string? Exceeded(PageFlowUsage usage) => usage.Lines > MaximumLines ? "flow_line_limit"
        : usage.TextCharacters > MaximumTextCharacters ? "flow_text_limit"
        : usage.Pixels > MaximumPixels ? "flow_pixel_limit"
        : usage.DescriptorBytes > MaximumDescriptorBytes ? "flow_descriptor_limit" : null;
}

/// <summary>選択されたページだけを収集。完了・見送り後は再利用せず、部分的な記述データを返さない。</summary>
public sealed class PageFlowCollector
{
    private readonly HashSet<PageFlowPageKey> expected = [];
    private readonly Dictionary<PageFlowPageKey, PageFlowPageDescriptor> pages = [];
    private bool completed;
    public string? FailureReason { get; private set; }
    public PageFlowUsage Usage { get; private set; } = new(0, 0, 0, 0, 0);
    public int RetainedPages => pages.Count;

    public PageFlowCollector(IReadOnlyList<PageFlowPageKey> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count == 0) throw new ArgumentException("選択ページがありません。");
        if (selected.Count > PageFlowLimits.MaximumSelectedPages * 2)
        { FailureReason = "flow_page_limit"; return; }
        foreach (var key in selected)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (!expected.Add(key)) throw new ArgumentException("選択した元ページが重複しています。");
        }
        if (expected.Select(k => k.Page).Distinct().Count() > PageFlowLimits.MaximumSelectedPages)
        { expected.Clear(); FailureReason = "flow_page_limit"; }
    }

    public bool Add(PageFlowPageKey key, Mat image, RowTextResult text, double minimumLineOverlap)
        => AddCore(key, image, text, minimumLineOverlap, null, null);

    // Oの識別子とGの記述を同じ収集で作り、呼出し側が補正済み画像をOと称する余地をなくす。
    public bool AddAligned(PageFlowPageKey key, Mat original, GlobalShift shift, RowTextResult alignedText, double minimumLineOverlap)
    {
        CheckOpen();
        if (FailureReason is not null) return false;
        if (key.Side != PageSpace.B || shift.Dx != 0) throw new ArgumentException("送りはBの縦方向補正だけに対応しています。");
        Validate(original);
        // 元SHA、識別子、写像・区間の追加固定費。既存64MiB予算内に計上する。
        if (Usage.DescriptorBytes + PageFlowLimits.DescriptorBytes(original.Height, 0, 0) + 512 > PageFlowLimits.MaximumDescriptorBytes)
            return Fail("flow_descriptor_limit");
        if (Usage.Pixels + (long)original.Width * original.Height > PageFlowLimits.MaximumPixels) return Fail("flow_pixel_limit");
        var map = PageMap.Global(original.Size(), original.Size(), original.Size(), shift);
        var identity = new PageFlowOriginalIdentity(key, original.Size(), PageFlowBandVerifier.Digest(original));
        using var aligned = map.Render(original, key.Side);
        return AddCore(key, aligned, alignedText, minimumLineOverlap, identity, map);
    }

    private bool AddCore(PageFlowPageKey key, Mat image, RowTextResult text, double minimumLineOverlap,
        PageFlowOriginalIdentity? original, PageMap? globalMap)
    {
        CheckOpen();
        if (FailureReason is not null) return false;
        if (!expected.Contains(key) || pages.ContainsKey(key)) throw new ArgumentException("未選択または重複した元ページです。");
        var size = image.Size();
        Validate(image);
        var extraBytes = original is null ? 0 : 512;
        var pixels = (long)size.Width * size.Height;
        if (Usage.Pixels + pixels > PageFlowLimits.MaximumPixels) return Fail("flow_pixel_limit");
        var rowBytes = PageFlowLimits.DescriptorBytes(size.Height, 0, 0) + extraBytes;
        if (Usage.DescriptorBytes + rowBytes > PageFlowLimits.MaximumDescriptorBytes) return Fail("flow_descriptor_limit");
        IReadOnlyList<RowLine> lines = [];
        if (text.Status == "available")
        {
            // 文字整理・連結前にも検査し、巨大な文字列を複製しない。
            long rawCharacters = 0;
            foreach (var word in text.Words)
            {
                rawCharacters += word.Text.Length;
                if (Usage.TextCharacters + rawCharacters > PageFlowLimits.MaximumTextCharacters) return Fail("flow_text_limit");
            }
            try { lines = TextLineLayout.Lines(text.Words, minimumLineOverlap); }
            catch (RowResourceLimitException) { return Fail("row_limit"); }
            if (lines.Any(l => l.Bounds.Left < 0 || l.Bounds.Top < 0 || l.Bounds.Right > size.Width || l.Bounds.Bottom > size.Height))
                return Fail("flow_text_coordinates");
        }
        var characters = lines.Sum(l => l.Words.Sum(w => (long)w.Text.Length) + Math.Max(0, l.Words.Count - 1));
        var next = new PageFlowUsage(Usage.Pages + 1, Usage.Lines + lines.Count, Usage.TextCharacters + characters,
            Usage.Pixels + pixels, Usage.DescriptorBytes + PageFlowLimits.DescriptorBytes(size.Height, lines.Count, characters) + extraBytes);
        if (PageFlowLimits.Exceeded(next) is { } limit) return Fail(limit);
        var summaries = lines.Select(l => new PageFlowLine(string.Join(' ', l.Words.Select(w => w.Text)), l.Bounds, l.Baseline)).ToArray();
        var hashes = new byte[size.Height * 32]; var nonwhite = new bool[size.Height]; var bytes = new byte[size.Width * 3];
        using var wholePage = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var y = 0; y < size.Height; y++)
        {
            Marshal.Copy(image.Ptr(y), bytes, 0, bytes.Length);
            nonwhite[y] = bytes.Any(v => v != 255);
            SHA256.HashData(bytes, hashes.AsSpan(y * 32, 32)); wholePage.AppendData(bytes);
        }
        pages.Add(key, new(key, size, text.Status, text.Detail, summaries, hashes, nonwhite,
            Convert.ToHexStringLower(wholePage.GetHashAndReset()), original, globalMap));
        Usage = next;
        return true;
    }

    public PageFlowDocumentDescriptor? Complete()
    {
        CheckOpen(); completed = true;
        if (FailureReason is not null) return null;
        if (pages.Count != expected.Count) { Fail("missing_page_descriptor"); return null; }
        var result = new PageFlowDocumentDescriptor(pages.Values.OrderBy(p => p.Key.Page).ThenBy(p => p.Key.Side).ToArray(), Usage);
        pages.Clear(); expected.Clear();
        return result;
    }

    private static void Validate(Mat image)
    {
        if (image.Empty() || image.Dims != 2 || image.Type() != MatType.CV_8UC3 || image.Width > 16000 || image.Height > 16000)
            throw new ArgumentException("送りの記述には16000px以内のBGR 8bit画像を指定してください。");
    }
    private bool Fail(string reason) { FailureReason ??= reason; pages.Clear(); expected.Clear(); return false; }
    private void CheckOpen() { if (completed) throw new InvalidOperationException("送りの記述収集は完了しています。"); }
}
