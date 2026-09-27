using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>確定した送り写像の内容比較Cと表示投影Dを所有する。元A/Bの寿命は呼び出し側が管理する。</summary>
public sealed class PageFlowComparison : IDisposable
{
    public int Page { get; }
    public Mat ContentA { get; }
    public Mat ContentB { get; }
    public PageComparison Content { get; }
    public RowDisplayProjection Display { get; }
    private readonly RowComparisonSurface surface;
    internal PageFlowComparison(int page, Mat a, Mat b, PageComparison content, RowDisplayProjection display, RowComparisonSurface surface)
    { this.surface = surface; Page = page; ContentA = a; ContentB = b; Content = content; Display = display; }
    public void Dispose() { Display.Dispose(); Content.Dispose(); ContentA.Dispose(); ContentB.Dispose(); }

    internal static PageFlowComparison Create(int page, RowComparisonSurface surface, Mat a, Mat b, ComparisonParameters parameters, PageMap? globalMap = null)
    {
        Mat? ca = null, cb = null; PageComparison? content = null; RowDisplayProjection? display = null;
        try
        {
            ca = surface.ContentMap.Render(a, PageSpace.A); cb = surface.ContentMap.Render(b, PageSpace.B);
            content = parameters.Regions.Count == 0 && parameters.Exclude.Count == 0
                ? PageComparer.Compare(ca, cb, parameters, true, retainProjection: true)
                : RegionalComparer.Compare(ca, cb, parameters with { Exclude = [] }, RegionMap.ForRows(surface, parameters), retainProjection: true);
            display = RowProjection.Create(content, surface, a, b, parameters, globalMap: globalMap);
            var result = new PageFlowComparison(page, ca, cb, content, display, surface);
            ca = cb = null; content = null; display = null;
            return result;
        }
        finally { display?.Dispose(); content?.Dispose(); ca?.Dispose(); cb?.Dispose(); }
    }

    /// <summary>実際の構造IDを維持したG座標の集約入力。DisplayのO座標を集約へ逆流させない。</summary>
    public PageFlowAggregation.Page Describe()
    {
        var structures = Display.StructuralChanges.Select(s => new PageFlowAggregation.Structure(new(Page, s.Id), s.Kind,
            Band(RowGeometry.Source(surface.DisplayMap, s.DisplayBounds, PageSpace.A, null), PageSpace.A),
            Band(RowGeometry.Source(surface.DisplayMap, s.DisplayBounds, PageSpace.B, null), PageSpace.B), s.DisplacementPx?.Dy, s.Excluded)).ToArray();
        return new(Page, true, Content.Clusters.Count, Display.DifferenceCount, Display.DifferenceCountComplete, false,
            Array.AsReadOnly(structures));
        PageFlowBand? Band(RowSourceBounds? source, PageSpace side)
        {
            if (source is null) return null;
            var bounds = source.Bounds;
            // 非連続/部分幅の構造は集約に使わない。比較と表示の構造・件数は維持する。
            if (bounds.Top != (int)bounds.Top || bounds.Bottom != (int)bounds.Bottom || bounds.Left != 0
                || bounds.Right != ContentA.Width || source.Parts.Sum(p => p.Bottom - p.Top) != bounds.Bottom - bounds.Top)
                return null;
            return new(new(side, Page), (int)bounds.Top, (int)(bounds.Bottom - bounds.Top));
        }
    }
}
