using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ReportDiff.Core;

public sealed partial class AnchoredContentPlan
{
    private string ComputeFingerprint()
    {
        using var hash = new FingerprintWriter();
        hash.Text("anchored_content/v1");
        hash.Text(Settings.InputSha256A); hash.Text(Settings.InputSha256B); hash.Text(Settings.TextSettingsSha256);
        hash.Number(Settings.MinimumLineOverlap);
        var p = Settings.Comparison; hash.Integer(p.Dpi);
        hash.Number(p.Diff.MaxShiftMm); hash.Number(p.Diff.ColorThreshold); hash.Number(p.Diff.EdgeTolerance);
        hash.Number(p.Ink.BackgroundRadiusMm); hash.Number(p.Ink.ContrastThreshold);
        hash.Number(p.Cluster.MergeXMm); hash.Number(p.Cluster.MergeYMm); hash.Integer(p.Cluster.MinPixels);
        hash.Integer(p.Cluster.MaxClustersPerPage); hash.Number(p.Cluster.MaxDiffRatio); hash.Number(p.Cluster.ReadingBandMm);
        hash.Number(p.Move.SearchMm); hash.Number(p.Move.MinScore); hash.Number(p.Move.MinScoreGap); hash.Number(p.Move.TemplateMarginMm);
        var r = Settings.Rows; hash.Integer(r.Enabled ? 1 : 0); hash.Integer(r.CarryEnabled ? 1 : 0);
        hash.Number(r.MaxShiftMm); hash.Number(r.MinWordMatch); hash.Number(r.RefineMm); hash.Number(r.MinImprovement);
        hash.Number(r.MinScoreGap); hash.Integer(r.MinSupportBands); hash.Number(r.MinSupportInkMm2); hash.Integer(r.MaxSegments);
        foreach (var side in new[] { PageSpace.A, PageSpace.B })
        for (var n = 1; n <= 2; n++)
        {
            var d = document!.Pages.Single(d => d.Key.Side == side && d.Key.Page == n);
            hash.Key(d.Key); hash.Integer(d.Size.Width); hash.Integer(d.Size.Height); hash.Text(d.PixelSha256);
            hash.Text(d.TextStatus); hash.Text(d.TextDetail); hash.Integer(d.Lines.Count);
            foreach (var line in d.Lines) hash.Line(line);
            var layout = (side == PageSpace.A ? previous!.Inference.Layouts.A : previous!.Inference.Layouts.B)[n - 1];
            hash.Integer(layout.HeaderEnd); hash.Integer(layout.FooterStart); hash.Integer(layout.BodyStart);
            hash.Integer(layout.BodyEnd); hash.Integer(layout.Pitch); hash.Integer(layout.Body.Count);
            foreach (var line in layout.Body) hash.Line(line);
        }
        hash.Integer(previous!.Inference.Proposals.Count);
        foreach (var candidate in previous.Inference.Proposals)
        {
            hash.Band(candidate.Source); hash.Band(candidate.Target); hash.Key(candidate.AmbiguousSource);
            hash.Integer(candidate.Support); hash.Text(candidate.Status); hash.Text(candidate.Reason);
            hash.Integer(candidate.Text.Count); foreach (var text in candidate.Text) hash.Text(text);
        }
        var proof = evidence!; hash.Integer(proof.CandidateIndex); hash.Span(proof.Source); hash.Span(proof.Target);
        hash.Integer(proof.Causes.Count); foreach (var cause in proof.Causes) hash.Row(cause);
        foreach (var rows in new[] { proof.CommonRows, proof.BeforeSupport, proof.BetweenSupport, proof.Crossing, proof.NextPageSupport })
        {
            hash.Integer(rows.Count); foreach (var row in rows) { hash.Row(row.Source); hash.Row(row.Counterpart); }
        }
        foreach (var surface in surfaces)
        {
            hash.Integer(surface.Id.OwnerPage); hash.Key(surface.Anchor); hash.Integer(surface.Size.Width); hash.Integer(surface.Size.Height);
            hash.Integer(surface.Pieces.Count); foreach (var piece in surface.Pieces) hash.Piece(piece);
        }
        foreach (var display in displays)
        {
            hash.Integer(display.Page); hash.Integer(display.Size.Width); hash.Integer(display.Size.Height); hash.Integer(display.Segments.Count);
            foreach (var segment in display.Segments) { hash.Piece(segment.Band); hash.Integer((int)segment.Role); }
        }
        return hash.Finish();
    }

    // 項目順・長さ・UTF-16コード単位・数値のビット表現を固定し、巨大なJSONや本文の複製を作らない。
    private sealed class FingerprintWriter : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal void Integer(long value)
        { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value); hash.AppendData(bytes); }
        internal void Number(double value) => Integer(BitConverter.DoubleToInt64Bits(value));
        internal void Text(string? value)
        {
            Integer(value?.Length ?? -1); if (value is null) return;
            Span<byte> bytes = stackalloc byte[256];
            for (var start = 0; start < value.Length; start += 128)
            {
                var length = Math.Min(128, value.Length - start);
                for (var i = 0; i < length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(2 * i, 2), value[start + i]);
                hash.AppendData(bytes[..(2 * length)]);
            }
        }
        internal void Key(PageFlowPageKey? key) { Integer(key is null ? -1 : (int)key.Side); Integer(key?.Page ?? 0); }
        internal void Span(OriginalRowSpan? span) { Key(span?.Page); Integer(span?.Top ?? 0); Integer(span?.Height ?? 0); }
        internal void Band(PageFlowBand? band) { Key(band?.Page); Integer(band?.Top ?? 0); Integer(band?.Height ?? 0); }
        internal void Line(PageFlowLine line)
        { Text(line.Text); Number(line.Bounds.Left); Number(line.Bounds.Top); Number(line.Bounds.Right); Number(line.Bounds.Bottom); Number(line.Baseline); }
        internal void Row(AnchoredRowReference row) { Span(row.Span); Integer(row.BodyIndex); Line(row.Line); }
        internal void Piece(AnchoredContentPiece piece) { Integer(piece.Top); Integer(piece.Height); Span(piece.A); Span(piece.B); }
        internal string Finish() => Convert.ToHexStringLower(hash.GetHashAndReset());
        public void Dispose() => hash.Dispose();
    }
}
