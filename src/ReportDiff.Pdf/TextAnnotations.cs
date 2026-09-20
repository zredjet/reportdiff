using System.Text;
using OpenCvSharp;
using ReportDiff.Core;

namespace ReportDiff.Pdf;

internal sealed record TextWord(string Text, Rect2d Bounds);

internal static class TextAnnotations
{
    internal const int MaximumTextRunes = 2000;

    internal static PageTextAnnotations Create(IEnumerable<TextWord> words, IReadOnlyList<DifferenceCluster> clusters,
        IReadOnlyList<RectMm> exclusions, int dpi)
    {
        var excluded = exclusions.Select(e => new Rect2d(Units.MmToPixels(e.X, dpi), Units.MmToPixels(e.Y, dpi),
            Units.MmToPixels(e.W, dpi), Units.MmToPixels(e.H, dpi))).ToArray();
        var candidates = words.Select(w => w with { Text = Clean(w.Text) }).Where(w => w.Text.Length > 0
            && !excluded.Any(e => Intersects(w.Bounds, e))).Distinct().OrderBy(w => w.Bounds.Top).ThenBy(w => w.Bounds.Left)
            .ThenBy(w => w.Text, StringComparer.Ordinal).ToArray();
        var texts = new Dictionary<int, string>();
        var warnings = new List<TextAnnotationWarning>();
        foreach (var cluster in clusters)
        {
            var bounds = new Rect2d(cluster.Bounds.X, cluster.Bounds.Y, cluster.Bounds.Width, cluster.Bounds.Height);
            var lines = new List<List<TextWord>>();
            foreach (var word in candidates.Where(w => Intersects(w.Bounds, bounds)))
            {
                // 行の先頭語との重なりで決める。連鎖して隣の行まで結合しない。
                var line = lines.FirstOrDefault(line => SameLine(line[0].Bounds, word.Bounds));
                if (line is null) { line = []; lines.Add(line); }
                line.Add(word);
            }
            var text = string.Join('\n', lines.Select(line => string.Join(' ', line.OrderBy(w => w.Bounds.Left)
                .ThenBy(w => w.Bounds.Top).ThenBy(w => w.Text, StringComparer.Ordinal).Select(w => w.Text))));
            var runes = text.EnumerateRunes().Take(MaximumTextRunes + 1).ToArray();
            if (runes.Length > MaximumTextRunes)
            {
                text = string.Concat(runes.Take(MaximumTextRunes - 1).Select(r => r.ToString())) + "…";
                warnings.Add(new("TEXT_ANNOTATION_TRUNCATED", $"相違 {cluster.Id}: PDF テキストが {MaximumTextRunes} 文字を超えたため末尾を省略しました。"));
            }
            texts.Add(cluster.Id, text);
        }
        return new(texts, warnings.AsReadOnly());
    }

    private static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(part => string.Concat(part.Where(c => !char.IsControl(c))))).Trim();
    private static bool SameLine(Rect2d a, Rect2d b) => Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= Math.Min(a.Height, b.Height) / 2;
    private static bool Intersects(Rect2d a, Rect2d b) => a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0
        && a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
