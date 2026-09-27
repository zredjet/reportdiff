using ReportDiff.Core;

namespace ReportDiff.Pdf;

/// <summary>C/D断片の元物理ページから取得した語だけを写す。所属ページの同じYを代用しない。</summary>
public static class AnchoredTextAnnotations
{
    public static PageTextAnnotations Create(IReadOnlyList<AnchoredContentPiece> pieces, PageSpace side,
        Func<PageFlowPageKey, RowTextResult> read, int dpi, IReadOnlyList<DifferenceCluster> clusters,
        TextOptions options, AnchoredContentBudget budget)
    {
        var reservations = new List<AnchoredContentBudget.Reservation>();
        try
        {
            var words = new List<TextWord>();
            foreach (var piece in pieces)
            {
                var source = side == PageSpace.A ? piece.A : piece.B;
                if (source is null) continue;
                var text = read(source.Page);
                if (text.Status != "available") return new(new Dictionary<int, string>(),
                    [new("TEXT_ANNOTATION_SKIPPED", $"元{side} {source.Page.Page}ページの本文を取得できません（{text.Detail ?? text.Status}）。")]);
                Reserve(AnchoredAllocation.DisplayPart, checked(4L * text.Words.Count + 4));
                Reserve(AnchoredAllocation.TextCharacter, checked(text.Words.Sum(w => (long)w.Text.Length) * 8));
                foreach (var word in text.Words)
                {
                    var top = Math.Max(word.Bounds.Top, source.Top); var bottom = Math.Min(word.Bounds.Bottom, source.Bottom);
                    if (top >= bottom) continue;
                    words.Add(new(word.Text, new(word.Bounds.Left, top + piece.Top - source.Top,
                        word.Bounds.Right - word.Bounds.Left, bottom - top)));
                }
            }
            return TextAnnotations.Create(words, clusters, [], dpi, options);
        }
        finally { foreach (var r in reservations) r.Dispose(); }
        void Reserve(AnchoredAllocation kind, long capacity)
        {
            if (!budget.TryReserve(kind, capacity, out var token)) throw new AnchoredContentResourceLimitException();
            try { reservations.Add(token!); } catch { token!.Dispose(); throw; }
        }
    }
}
