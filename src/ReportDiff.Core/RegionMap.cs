using OpenCvSharp;

namespace ReportDiff.Core;

public sealed record ComparisonRegion(int Index, string Name, RectMm Bounds, string Mode, DiffOptions Diff);
public sealed record RegionRun(int Id, DiffOptions Diff, int AbsorbedGroups, int MaxShiftPx);
public sealed record RegionResult(int Index, string Name, string Mode, Rect Bounds, int EffectivePixels,
    int RawPixels, int SuppressedPixels, int SuppressedComponents, int ExcludedPixels, int? RunId, string Status);

/// <summary>領域の所有権。内側を優先し、除外は比較より常に優先する。</summary>
internal sealed class RegionMap
{
    public int Width { get; }
    public int Height { get; }
    public int[] Owners { get; }
    public bool[] Excluded { get; }
    public ComparisonRegion[] Regions { get; }
    public Rect[] Bounds { get; }
    public int[] EffectivePixels { get; }
    private readonly DiffOptions baseline;
    private readonly DiffOptions validationBaseline;
    private readonly DiffOptions[] validationRegions;

    public RegionMap(int width, int height, ComparisonParameters parameters)
    {
        Width = width; Height = height; baseline = parameters.Diff;
        Regions = parameters.Regions.ToArray();
        validationBaseline = baseline with { MaxShiftMm = 0 };
        validationRegions = Regions.Select(r => r.Diff with { MaxShiftMm = 0 }).ToArray();
        Owners = Enumerable.Repeat(-1, checked(width * height)).ToArray();
        Excluded = new bool[Owners.Length];
        Bounds = Regions.Select(r => PageMap.CanvasRectangle(r.Bounds, parameters.Dpi, new(width, height))).ToArray();
        EffectivePixels = new int[Regions.Length];
        // 内包する側から塗る。同面積は非交差だけなので宣言順で結果は変わらない。
        var order = Enumerable.Range(0, Regions.Length).OrderByDescending(i => Regions[i].Bounds.W * Regions[i].Bounds.H).ToArray();
        foreach (var mode in new[] { "compare", "exclude" })
        foreach (var i in order.Where(i => Regions[i].Mode == mode))
        {
            var box = Bounds[i];
            for (var y = box.Top; y < box.Bottom; y++)
            for (var x = box.Left; x < box.Right; x++)
            {
                var pixel = y * width + x;
                Owners[pixel] = i;
                if (mode == "exclude") Excluded[pixel] = true;
            }
        }
        foreach (var exclusion in parameters.Exclude)
        {
            var box = PageMap.CanvasRectangle(exclusion, parameters.Dpi, new(width, height));
            for (var y = box.Top; y < box.Bottom; y++)
            for (var x = box.Left; x < box.Right; x++) Excluded[y * width + x] = true;
        }
        for (var pixel = 0; pixel < Owners.Length; pixel++)
            if (Owners[pixel] is var owner && owner >= 0 && (!Excluded[pixel] || Regions[owner].Mode == "exclude"))
                EffectivePixels[owner]++;
    }

    public DiffOptions DiffAt(int pixel) => Owners[pixel] is var i && i >= 0 && Regions[i].Mode == "compare"
        ? Regions[i].Diff : baseline;
    public DiffOptions DiffAt(int x, int y) => DiffAt(y * Width + x);
    public DiffOptions ValidationDiffAt(int x, int y) => Owners[y * Width + x] is var i && i >= 0 && Regions[i].Mode == "compare"
        ? validationRegions[i] : validationBaseline;
}

public static class RegionGeometry
{
    public static bool Contains(RectMm outer, RectMm inner) => outer.X <= inner.X && outer.Y <= inner.Y
        && outer.X + outer.W >= inner.X + inner.W && outer.Y + outer.H >= inner.Y + inner.H;

    public static void ValidatePair(RectMm a, RectMm b, string key, int? dpi = null)
    {
        if (a == b) throw new ArgumentException($"{key}: 同一の領域矩形は指定できません。");
        var nested = Contains(a, b) || Contains(b, a);
        var aa = dpi is int d ? Pixels(a, d) : a;
        var bb = dpi is int d2 ? Pixels(b, d2) : b;
        if (aa.X < bb.X + bb.W && bb.X < aa.X + aa.W && aa.Y < bb.Y + bb.H && bb.Y < aa.Y + aa.H
            && !nested)
            throw new ArgumentException($"{key}: 領域の部分交差は指定できません{(dpi is null ? "" : $"（{dpi}dpi の丸め後）")}。");
    }

    private static RectMm Pixels(RectMm r, int dpi)
    {
        var box = PageMap.RoundedPixels(r, dpi);
        return new(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top);
    }
}
