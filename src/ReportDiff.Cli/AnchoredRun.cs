using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

namespace ReportDiff.Cli;

/// <summary>事前比較と保存時の再比較を分離し、新経路の試行全体を所有する。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal sealed class AnchoredRun : IDisposable
{
    private readonly ComparisonInput a, b;
    private readonly ReportInputs inputs;
    private readonly AppSettings settings;
    private readonly PageFlowPlan previous;
    private readonly AnchoredContentSettings snapshot;
    private readonly string settingsDigest;
    private readonly List<AnchoredContentBudget.Reservation> reservations = [];
    private string? signature;
    private bool bandVerified;
    internal AnchoredContentPlan Plan { get; }
    internal string? Reason { get; private set; }
    private AnchoredRun(ComparisonInput a, ComparisonInput b, ReportInputs inputs, AppSettings settings,
        PageFlowPlan previous, AnchoredContentSettings snapshot, AnchoredContentPlan plan)
    {
        this.a = a; this.b = b; this.inputs = inputs; this.settings = settings; this.previous = previous; this.snapshot = snapshot;
        Plan = plan; settingsDigest = Hash(settings);
    }
    internal static AnchoredRun? Prepare(ComparisonInput a, ComparisonInput b, ReportInputs inputs, PageFlowRun? flow,
        PagePairingPlan pairing, AppSettings settings, bool selectionLimited)
    {
        if (flow?.Applied != false || flow.Plan is not { } previous || a.Format != InputFormat.Pdf || b.Format != InputFormat.Pdf
            || a.PageCount != 2 || b.PageCount != 2 || pairing.Pages.Count != 2 || selectionLimited) return null;
        var snapshot = new AnchoredContentSettings(settings.ForPage(1, a.Dpi), settings.ForPage(2, a.Dpi), settings.Rows,
            inputs.A.Sha256, inputs.B.Sha256, Hash(settings.Text), settings.Text.MinLineOverlap, selectionLimited, settings.Align.Enabled);
        if (snapshot.ScopeReason is not null) return null;
        var plan = AnchoredContentPlan.Prepare(previous, snapshot, out _);
        if (plan is null) return null;
        var run = new AnchoredRun(a, b, inputs, settings, previous, snapshot, plan);
        try
        {
            // 出力証拠のレコード、参照配列、指紋および省略理由を計画と同じ予算に保持する。
            run.Reserve(AnchoredAllocation.DisplayPart, checked(128 + previous.Usage.Lines * 16L));
            run.Reserve(AnchoredAllocation.TextCharacter, 4096);
            run.Verify();
            using var result = plan.Compare(run.Read, run.Text, out var reason);
            run.Reason = reason;
            if (result is not null) { run.signature = Signature(result); run.bandVerified = true; }
            return run;
        }
        catch (AnchoredContentResourceLimitException) { run.Reason = "flow_descriptor_limit"; return run; }
        catch { run.Dispose(); throw; }
    }
    internal ReportAnchoredContent Audit() => ReportAnchored.Audit(Plan, false, Reason, bandVerified);

    internal ReportDocument? Save(CompareCommand command, string output, ConsoleProgress? progress,
        Action<string, AnchoredContentPlan, string>? stage = null)
    {
        if (signature is null) return null;
        Directory.CreateDirectory(output);
        var trial = Path.Combine(output, ".anchored-trial");
        var retained = reservations.Count;
        try
        {
            stage?.Invoke("before_recompare", Plan, trial); Verify();
            using var result = Plan.Compare(Read, Text, out var reason);
            if (result is null)
            {
                if (reason is "flow_descriptor_limit" or "resource_limit") { Reason = reason; return null; }
                throw new InvalidOperationException("内容面の事前採否と再比較が一致しません。");
            }
            if (signature != Signature(result)) throw new InvalidOperationException("内容面の比較値・マスク・採否が変わりました。");
            var items = result.Contents.Sum(c => (long)c.Clusters.Count + c.Parts.Count) + result.Structures.Count;
            Reserve(AnchoredAllocation.DisplayPart, checked(128 + 32 * items));
            // 注釈、JSON/HTMLの文字複製の保守的容量。元本文は共有し、語の一時配列はPdf側で別予約。
            Reserve(AnchoredAllocation.TextCharacter, checked((items + 4) * settings.Text.MaxRunesPerCluster * 32L
                + previous.Usage.TextCharacters * 16));
            var writer = new ReportWriter(trial, inputs, settings.ToReportConfiguration(), command.SaveAllPages);
            var contentImages = new PageImages[2];
            for (var page = 1; page <= 2; page++)
            {
                using var images = Plan.RenderContent(page, Read);
                contentImages[page - 1] = writer.AddAnchoredContent(page, images);
                stage?.Invoke($"content_saved_{page}", Plan, trial);
            }
            var raw = new RawEvidence?[2]; string? source = null, target = null;
            for (var page = 1; page <= 2; page++)
            {
                using var originalA = Read(new(PageSpace.A, page)); using var originalB = Read(new(PageSpace.B, page));
                raw[page - 1] = writer.AddAnchoredRaw(page, originalA, originalB, a.Dpi);
                foreach (var side in new[] { PageSpace.A, PageSpace.B })
                {
                    var key = new PageFlowPageKey(side, page); var image = side == PageSpace.A ? originalA : originalB;
                    if (Plan.Evidence.Source.Page == key) source = Band(Plan.Evidence.Source, true, image);
                    if (Plan.Evidence.Target.Page == key) target = Band(Plan.Evidence.Target, false, image);
                }
            }
            for (var page = 1; page <= 2; page++)
            {
                progress?.Page(page, 2, page, false);
                using var display = result.ProjectDisplay(page); using var images = Plan.RenderDisplay(page, Read);
                var content = result.Contents[page - 1]; var pieces = Plan.Surfaces[page - 1].Pieces;
                var structures = display.Structures.Select(s => new DifferenceCluster(s.Structure.Reference.StructuralChangeId, s.DisplayBounds, 0)).ToArray();
                var displayPieces = Plan.Displays[page - 1].Segments.Select(s => s.Band).ToArray();
                var textA = Annotate(pieces, PageSpace.A, content.Clusters); var textB = Annotate(pieces, PageSpace.B, content.Clusters);
                var structureA = Annotate(displayPieces, PageSpace.A, structures); var structureB = Annotate(displayPieces, PageSpace.B, structures);
                writer.AddAnchoredPage(Plan, result, display, images, contentImages[page - 1], raw[page - 1], textA, textB, structureA, structureB);
                writer.AddFontWarnings(page, "A", a.InspectFonts(page)); writer.AddFontWarnings(page, "B", b.InspectFonts(page));
                stage?.Invoke($"display_saved_{page}", Plan, trial);
            }
            progress?.Report();
            var report = writer.Complete(AnchoredFlowReport.Create(Plan, result, source!, target!));
            stage?.Invoke("json_saved", Plan, trial);
            if (!command.NoHtml) HtmlReportWriter.Write(trial, report);
            stage?.Invoke("before_publication", Plan, trial);
            AnchoredFlowReport.Validate(report, trial); Verify();
            // この所有範囲は外側OutputWorkspaceのstaging内。公開は呼出側で一度だけ行う。
            foreach (var path in Directory.EnumerateFileSystemEntries(trial))
            {
                var destination = Path.Combine(output, Path.GetFileName(path));
                if (Directory.Exists(path)) Directory.Move(path, destination); else File.Move(path, destination);
            }
            progress?.EndLine(); return report;
            string Band(OriginalRowSpan span, bool isSource, Mat image) => writer.AddFlowBand(Plan.Evidence.CandidateId, isSource,
                image, new(span.Page, span.Top, span.Height));
            PageTextAnnotations Annotate(IReadOnlyList<AnchoredContentPiece> pp, PageSpace side, IReadOnlyList<DifferenceCluster> cc) =>
                AnchoredTextAnnotations.Create(pp, side, Text, a.Dpi, cc, settings.Text, Plan.Budget);
        }
        catch (AnchoredContentResourceLimitException) { Reason = "flow_descriptor_limit"; return null; }
        finally
        {
            for (var i = reservations.Count - 1; i >= retained; i--) { reservations[i].Dispose(); reservations.RemoveAt(i); }
            if (Directory.Exists(trial)) Directory.Delete(trial, true);
        }
    }
    private Mat Read(PageFlowPageKey key)
    {
        using var image = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page);
        previous.VerifyOriginal(key, image.Pixels); return image.TakePixels();
    }
    private RowTextResult Text(PageFlowPageKey key)
    {
        var size = Plan.Displays[key.Page - 1].OriginalSize;
        return (key.Side == PageSpace.A ? a : b).ReadRowWords(key.Page, PageMap.Unaligned(size, size), key.Side);
    }
    private void Verify()
    {
        Plan.VerifyBinding(previous, snapshot);
        if (settingsDigest != Hash(settings)) throw new InvalidOperationException("内容面の設定スナップショットが変わりました。");
        if (a.Describe().Sha256 != inputs.A.Sha256 || b.Describe().Sha256 != inputs.B.Sha256)
            throw new CommandLineException("送りの検証中に入力ファイルが変わりました。入力を固定して再実行してください。");
    }
    private void Reserve(AnchoredAllocation kind, long count)
    {
        if (!Plan.Budget.TryReserve(kind, count, out var token)) throw new AnchoredContentResourceLimitException();
        try { reservations.Add(token!); } catch { token!.Dispose(); throw; }
    }
    private static string Signature(AnchoredComparisonResult result) => Hash(new
    {
        Contents = result.Contents.Select(c => new { c.Id, c.Status, c.RawPixels, c.NoiseDropped, c.AbsorbedGroups,
            c.MaxShiftPx, c.Warnings, c.Clusters, c.Parts, c.Annotations, c.MaskSha256 }),
        result.Adoption, result.Structures, result.BandVerification, result.Aggregation, result.OmittedBandPixels, result.OmittedNonwhitePixels
    });
    private static string Hash<T>(T value)
    {
        using var hash = SHA256.Create();
        using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write)) JsonSerializer.Serialize(stream, value);
        return Convert.ToHexStringLower(hash.Hash!);
    }
    public void Dispose() { foreach (var r in reservations) r.Dispose(); Plan.Dispose(); }
}
