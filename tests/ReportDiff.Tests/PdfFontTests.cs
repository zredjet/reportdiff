using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PdfFontTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void StandardFontInTextFormsAndInheritedResourcesIsReported(bool form, bool inherited)
    {
        var warning = Assert.Single(Inspect(PdfFixture.CreateFontReport(form: form, inheritedResources: inherited)));
        Assert.Equal("NON_EMBEDDED_FONT", warning.Code);
        Assert.Contains("Helvetica", warning.Message); Assert.Contains("3 0 R", warning.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("BT /F1 12 Tf ET")]
    public void UnusedResourceAndSelectedButUnusedFontDoNotWarn(string content) =>
        Assert.Empty(Inspect(PdfFixture.CreateFontReport(contents: [content])));

    [Theory]
    [InlineData("BT /F1 12 Tf 3 Tr (A) Tj ET")]
    [InlineData("BT /F1 12 Tf ( ) Tj ET")]
    [InlineData("BT /F1 12 Tf [(A) 10 (A)] TJ /Alias 15 Tf (A) Tj ET")]
    public void InvisibleWhitespaceAndAliasesUseOneFont(string content) =>
        Assert.Equal("NON_EMBEDDED_FONT", Assert.Single(Inspect(PdfFixture.CreateFontReport(contents: [content]))).Code);

    [Fact]
    public void SameNameDifferentObjectsRemainSeparate()
    {
        var warnings = Inspect(PdfFixture.CreateFontReport(contents: ["BT /F1 12 Tf (A) Tj /F2 12 Tf (A) Tj ET"]));
        Assert.Equal(2, warnings.Count); Assert.All(warnings, w => Assert.Equal("NON_EMBEDDED_FONT", w.Code));
        Assert.Contains("3 0 R", warnings[0].Message); Assert.Contains("4 0 R", warnings[1].Message);
    }

    [Fact]
    public void SubsetPrefixDoesNotProveEmbeddingOrUseFallbackName()
    {
        var warning = Assert.Single(Inspect(PdfFixture.CreateFontReport(font: PdfFixture.StandardFont.Replace("Helvetica", "ABCDEF+OriginalName"))));
        Assert.Equal("NON_EMBEDDED_FONT", warning.Code); Assert.Contains("ABCDEF+OriginalName", warning.Message);
    }

    [Fact]
    public void DirectFontIsUnknownAndRepeatedLettersDoNotDuplicateWarning()
    {
        var warning = Assert.Single(Inspect(PdfFixture.CreateFontReport(direct: true, contents: ["BT /F1 12 Tf (AAA) Tj ET"])));
        Assert.Equal("FONT_INSPECTION_INCOMPLETE", warning.Code); Assert.Contains("直接辞書", warning.Message);
    }

    [Fact]
    public void SelfMadeTrueTypeAndType3AreEmbeddedAndActuallyRenderable()
    {
        foreach (var bytes in new[] { PdfFixture.CreateFontReport(font: PdfFixture.EmbeddedTrueType), PdfFixture.CreateTextPage([new("A", 20, 40)]) })
        {
            Assert.Empty(Inspect(bytes));
            using var file = new PdfTestFile(bytes); using var reader = PdfReader.Open(file.FilePath);
            using var image = reader.ReadPage(1, 72);
            using var gray = new Mat(); Cv2.CvtColor(image.Pixels, gray, ColorConversionCodes.BGR2GRAY);
            using var dark = new Mat(); Cv2.InRange(gray, new Scalar(0), new Scalar(239), dark);
            Assert.True(Cv2.CountNonZero(dark) > 10);
            using var stream = new MemoryStream(bytes); using var pdf = PdfDocument.Open(stream);
            Assert.Equal("A", Assert.Single(pdf.GetPage(1).Letters).Value);
        }
    }

    [Fact]
    public void EmbeddedAndUnembeddedSameNameOnlyWarnsForMissingProgram()
    {
        var warnings = Inspect(PdfFixture.CreateFontReport(font: PdfFixture.EmbeddedTrueType,
            secondFont: PdfFixture.StandardFont.Replace("Helvetica", "ABCDEF+ReportDiffTest"),
            contents: ["BT /F1 12 Tf (A) Tj /F2 12 Tf (A) Tj ET"]));
        var warning = Assert.Single(warnings); Assert.Equal("NON_EMBEDDED_FONT", warning.Code); Assert.Contains("4 0 R", warning.Message);
    }

    [Theory]
    [InlineData("Type1", "FontFile", "", null)]
    [InlineData("MMType1", "FontFile", "", null)]
    [InlineData("Type1", "FontFile3", "/Subtype /Type1C", null)]
    [InlineData("TrueType", "FontFile2", "", null)]
    [InlineData("TrueType", "FontFile3", "/Subtype /OpenType", null)]
    [InlineData("TrueType", "FontFile", "", "FONT_INSPECTION_INCOMPLETE")]
    [InlineData("Type1", "FontFile2", "", "FONT_INSPECTION_INCOMPLETE")]
    [InlineData("Type1", "FontFile3", "", "FONT_INSPECTION_INCOMPLETE")]
    [InlineData("Unknown", "FontFile", "", "FONT_INSPECTION_INCOMPLETE")]
    public void ProgramPresenceChecksMetadataWithoutClaimingGlyphValidity(string subtype, string key, string options, string? code)
    {
        var bytes = PdfFixture.CreateFontReport(font: $"<< /Type /Font /Subtype /{subtype} /BaseFont /Test /FontDescriptor 5 0 R >>",
            descriptor: $"<< /{key} 6 0 R >>", program: $"<< /Length 1 {options} >>\nstream\nx\nendstream");
        Assert.Equal(code, InspectDictionary(bytes)?.Code);
    }

    [Theory]
    [InlineData("CIDFontType0", "FontFile3", "/Subtype /CIDFontType0C", null)]
    [InlineData("CIDFontType2", "FontFile2", "", null)]
    [InlineData("CIDFontType2", "FontFile3", "/Subtype /OpenType", null)]
    [InlineData("Unknown", "FontFile2", "", "FONT_INSPECTION_INCOMPLETE")]
    public void Type0InspectsDescendantProgram(string childType, string key, string options, string? code)
    {
        var bytes = PdfFixture.CreateFontReport(font: "<< /Subtype /Type0 /BaseFont /Subset /DescendantFonts [7 0 R] >>",
            descendant: $"<< /Subtype /{childType} /FontDescriptor 5 0 R >>",
            descriptor: $"<< /{key} 6 0 R >>", program: $"<< /Length 1 {options} >>\nstream\nx\nendstream");
        Assert.Equal(code, InspectDictionary(bytes)?.Code);
    }

    [Fact]
    public void EmbeddedType0TrueTypeIsUsedWithoutWarning()
    {
        var bytes = PdfFixture.CreateFontReport(font: "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+ReportDiffTest /Encoding /Identity-H /DescendantFonts [7 0 R] >>",
            descendant: "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+ReportDiffTest /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /CIDToGIDMap /Identity /DW 600 >>",
            contents: ["BT /F1 12 Tf 20 40 Td <0001> Tj ET"]);
        Assert.Empty(Inspect(bytes));
        using var stream = new MemoryStream(bytes); using var document = PdfDocument.Open(stream);
        Assert.Single(document.GetPage(1).Letters);
        using var file = new PdfTestFile(bytes); using var reader = PdfReader.Open(file.FilePath);
        using var image = reader.ReadPage(1, 72);
        Assert.Equal(new Vec3b(0, 0, 0), image.Pixels.At<Vec3b>(57, 23));
    }

    [Theory]
    [InlineData("<< /Subtype /Type1 /BaseFont /Test /FontDescriptor 5 0 R >>", "<< >>", "NON_EMBEDDED_FONT")]
    [InlineData("<< /Subtype /Type0 /BaseFont /Test /DescendantFonts [7 0 R] >>", "<< >>", "NON_EMBEDDED_FONT")]
    [InlineData("<< /Subtype /TrueType /BaseFont /Test /FontDescriptor 999 0 R >>", "<< >>", "FONT_INSPECTION_INCOMPLETE")]
    [InlineData("<< /Subtype /Type1 /BaseFont /Test /FontDescriptor 5 0 R >>", "<< /FontFile 5 0 R >>", "FONT_INSPECTION_INCOMPLETE")]
    [InlineData("<< /Subtype /Type0 /DescendantFonts [] >>", "<< >>", "FONT_INSPECTION_INCOMPLETE")]
    [InlineData("<< /Subtype /Type3 /CharProcs << >> >>", "<< >>", "FONT_INSPECTION_INCOMPLETE")]
    [InlineData("<< /Subtype /Type3 /CharProcs << /A 999 0 R >> >>", "<< >>", "FONT_INSPECTION_INCOMPLETE")]
    [InlineData("<< /Subtype /Type3 /CharProcs << /A 8 0 R >> /Resources << /Font << /F1 4 0 R >> >> >>", "<< >>", "FONT_INSPECTION_INCOMPLETE")]
    public void MissingAndMalformedDefinitionsRemainDistinct(string font, string descriptor, string code) =>
        Assert.Equal(code, InspectDictionary(PdfFixture.CreateFontReport(font: font, descriptor: descriptor,
            descendant: "<< /Subtype /CIDFontType2 >>"))?.Code);

    [Theory]
    [InlineData("<< /Length 0 >>\nstream\nendstream")]
    [InlineData("<< /Length 1 /Filter /UnsupportedTestFilter >>\nstream\nx\nendstream")]
    [InlineData("42")]
    [InlineData("6 0 R")]
    public void BrokenEmptyAndCyclicStreamsAreIncomplete(string program) =>
        Assert.Equal("FONT_INSPECTION_INCOMPLETE", InspectDictionary(PdfFixture.CreateFontReport(font: PdfFixture.EmbeddedTrueType, program: program))?.Code);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnnotationsAndPatternsAreExplicitlyIncomplete(bool annotation, bool pattern) =>
        Assert.Equal("FONT_INSPECTION_INCOMPLETE", Assert.Single(Inspect(PdfFixture.CreateFontReport(contents: [""], annotation: annotation, pattern: pattern))).Code);

    [Fact]
    public void FailedPageDoesNotLoseWarningsOrBlockLaterPages()
    {
        using var file = new PdfTestFile(PdfFixture.CreateFontReport(contents: ["BT /Missing 12 Tf (A) Tj ET", PdfFixture.FontText]));
        using var reader = new PdfTextReader(file.FilePath);
        Assert.Equal("FONT_INSPECTION_INCOMPLETE", Assert.Single(reader.InspectFonts(1)).Code);
        Assert.Equal("FONT_INSPECTION_INCOMPLETE", Assert.Single(reader.InspectFonts(1)).Code);
        Assert.Equal("NON_EMBEDDED_FONT", Assert.Single(reader.InspectFonts(2)).Code);
        Assert.Equal("FONT_INSPECTION_INCOMPLETE", Assert.Single(reader.InspectFonts(1)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public void AnnotationLimitsAndRotationDoNotSuppressFonts(int rotation)
    {
        using var file = new PdfTestFile(PdfFixture.CreateFontReport(contents: ["BT /F1 12 Tf (AAA) Tj ET"], rotation: rotation));
        using var reader = new PdfTextReader(file.FilePath, new() { MaxLettersPerPage = 1 });
        var cluster = new DifferenceCluster(1, new Rect(0, 0, 100, 100), 10);
        var annotation = reader.Annotate(1, new Size(100, 100), 72, [cluster], []);
        Assert.Contains(annotation.Warnings, w => w.Code == "TEXT_ANNOTATION_SKIPPED");
        Assert.Equal("NON_EMBEDDED_FONT", Assert.Single(reader.InspectFonts(1)).Code);
    }

    [Fact]
    public void ReaderDisposalReleasesInspectionFile()
    {
        using var file = new PdfTestFile(PdfFixture.CreateFontReport());
        var reader = new PdfTextReader(file.FilePath);
        Assert.NotEmpty(reader.InspectFonts(1)); reader.Dispose(); reader.Dispose();
        File.Delete(file.FilePath);
        Assert.Throws<ObjectDisposedException>(() => reader.InspectFonts(1));
    }

    [Fact]
    public void FailedOpenIsReportedWithoutRepeatedAttempts()
    {
        using var file = new PdfTestFile([1, 2, 3]);
        using var reader = new PdfTextReader(file.FilePath);
        Assert.Equal("FONT_INSPECTION_INCOMPLETE", Assert.Single(reader.InspectFonts(1)).Code);
        Assert.Equal("FONT_INSPECTION_INCOMPLETE", Assert.Single(reader.InspectFonts(2)).Code);
        var annotation = reader.Annotate(1, new Size(100, 100), 72, [new(1, new Rect(0, 0, 100, 100), 1)], []);
        Assert.Equal("TEXT_EXTRACTION_FAILED", Assert.Single(annotation.Warnings).Code);
    }

    private static IReadOnlyList<PdfFontWarning> Inspect(byte[] bytes)
    {
        using var file = new PdfTestFile(bytes); using var reader = new PdfTextReader(file.FilePath);
        return reader.InspectFonts(1);
    }

    private static PdfFontWarning? InspectDictionary(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var document = PdfDocument.Open(stream);
        return new PdfFontInspector(document).InspectFont(new IndirectReference(3, 0));
    }
}
