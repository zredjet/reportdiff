using ReportDiff.Core;

namespace ReportDiff.Pdf;

/// <summary>採用済み行写像の注釈。行推定で取得した語を再利用し、差分の判定は変更しない。</summary>
public static class RowTextAnnotations
{
    public static RowTextResult ToAligned(RowTextResult text, PageMap globalMap, PageSpace side)
    {
        if (text.Status != "available") return text;
        var words = new List<RowWord>();
        foreach (var word in text.Words)
        {
            var parts = globalMap.MapBoundsParts(word.Bounds, side, PageSpace.Canvas);
            if (parts.Count != 1 || Math.Abs((parts[0].Right - parts[0].Left) - (word.Bounds.Right - word.Bounds.Left)) > 1e-7
                || Math.Abs((parts[0].Bottom - parts[0].Top) - (word.Bounds.Bottom - word.Bounds.Top)) > 1e-7)
                return new("text_unavailable", "global_word_clipped", []);
            var bounds = parts[0]; var dy = bounds.Top - word.Bounds.Top;
            words.Add(word with { Bounds = bounds, Baselines = word.Baselines.Select(y => y + dy).ToArray() });
        }
        return text with { Words = words.AsReadOnly() };
    }

    public static PageTextAnnotations Create(RowTextResult text, PageMap displayMap, PageSpace side, int dpi,
        IReadOnlyList<DifferenceCluster> clusters, IReadOnlyList<RectMm> exclusions, TextOptions options)
    {
        if (text.Status != "available") throw new ArgumentException("行注釈には取得済みの行テキストが必要です。");
        var words = text.Words.SelectMany(w => displayMap.MapBoundsParts(w.Bounds, side, PageSpace.Canvas)
            .Select(b => new TextWord(w.Text, b.Rectangle))).ToArray();
        var excluded = exclusions.Select(r => PageMap.ContinuousPixels(r, dpi))
            .Select(r => displayMap.MapBounds(new(r.Left, r.Top, r.Right, r.Bottom), PageSpace.A, PageSpace.Canvas))
            .Where(b => b is not null).Select(b => b!.Value.Rectangle).ToArray();
        return TextAnnotations.Create(words, clusters, [], dpi, options, excluded);
    }
}
