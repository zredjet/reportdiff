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
    public Rect[] ExclusionBounds { get; }
    private readonly DiffOptions baseline;
    private readonly DiffOptions validationBaseline;
    private readonly DiffOptions[] validationRegions;

    public RegionMap(int width, int height, ComparisonParameters parameters)
        : this(width, height, parameters, r => PageMap.CanvasRectangle(r, parameters.Dpi, new(width, height))) { }

    private RegionMap(int width, int height, ComparisonParameters parameters, Func<RectMm, Rect> rectangle)
    {
        Width = width; Height = height; baseline = parameters.Diff;
        Regions = parameters.Regions.ToArray();
        validationBaseline = baseline with { MaxShiftMm = 0 };
        validationRegions = Regions.Select(r => r.Diff with { MaxShiftMm = 0 }).ToArray();
        Owners = Enumerable.Repeat(-1, checked(width * height)).ToArray();
        Excluded = new bool[Owners.Length];
        Bounds = Regions.Select(r => rectangle(r.Bounds)).ToArray();
        ExclusionBounds = parameters.Exclude.Concat(Regions.Where(r => r.Mode == "exclude").Select(r => r.Bounds))
            .Distinct().Select(rectangle).ToArray();
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
            var box = rectangle(exclusion);
            for (var y = box.Top; y < box.Bottom; y++)
            for (var x = box.Left; x < box.Right; x++) Excluded[y * width + x] = true;
        }
        CountEffective();
    }

    private RegionMap(RegionMap display, RowComparisonSurface surface)
    {
        Width = surface.ContentMap.CanvasSize.Width; Height = surface.ContentMap.CanvasSize.Height;
        baseline = display.baseline; validationBaseline = display.validationBaseline; validationRegions = display.validationRegions;
        Regions = display.Regions; EffectivePixels = new int[Regions.Length];
        Owners = new int[checked(Width * Height)]; Excluded = new bool[Owners.Length];
        foreach (var piece in surface.Pieces)
        {
            Array.Copy(display.Owners, piece.DisplayStart * Width, Owners, piece.ContentStart * Width, piece.Length * Width);
            Array.Copy(display.Excluded, piece.DisplayStart * Width, Excluded, piece.ContentStart * Width, piece.Length * Width);
        }
        Bounds = display.Bounds.Select(box => Parts(box).Aggregate(new Rect(), Union)).ToArray();
        ExclusionBounds = display.ExclusionBounds.SelectMany(Parts).Distinct().ToArray();
        CountEffective();

        IEnumerable<Rect> Parts(Rect box)
        {
            foreach (var piece in surface.Pieces)
            {
                var top = Math.Max(box.Top, piece.DisplayStart); var bottom = Math.Min(box.Bottom, piece.DisplayStart + piece.Length);
                if (box.Width > 0 && bottom > top) yield return new(box.X, top - piece.DisplayStart + piece.ContentStart, box.Width, bottom - top);
            }
        }
        static Rect Union(Rect a, Rect b) => a.Width == 0 || a.Height == 0 ? b
            : new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right) - Math.Min(a.Left, b.Left),
                Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Top, b.Top));
    }

    internal static RegionMap ForRows(RowComparisonSurface surface, ComparisonParameters parameters)
        => new(ForRowDisplay(surface.DisplayMap, parameters), surface);

    internal static RegionMap ForRowDisplay(PageMap map, ComparisonParameters parameters)
    {
        return new RegionMap(map.CanvasSize.Width, map.CanvasSize.Height, parameters, rectangle =>
        {
            var bounds = map.MapBounds(PageMap.RoundedPixels(rectangle, parameters.Dpi), PageSpace.A, PageSpace.Canvas);
            return bounds is not { } box ? new() : new((int)box.Left, (int)box.Top, (int)(box.Right - box.Left), (int)(box.Bottom - box.Top));
        });
    }

    private void CountEffective()
    {
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
