namespace ReportDiff.Report;

public sealed record DirectoryReportDocument(int SchemaVersion, string ReportType, ReportTool Tool,
    DateTimeOffset GeneratedAt, DirectoryInputs Inputs, DirectoryConfiguration Configuration,
    DirectorySummary Summary, IReadOnlyList<DirectoryFileResult> Files,
    IReadOnlyList<DirectoryIgnoredFile> Ignored, IReadOnlyList<ReportWarning> Warnings);
public sealed record DirectoryInputs(string A, string B);
public sealed record ConfigurationSource(string Path, string Sha256);
public sealed record DirectoryConfiguration(ConfigurationSource? Common, ConfigurationSource? Rules,
    IReadOnlyList<ConfigurationSource> Referenced, DirectoryCliOptions Cli);
public sealed record DirectoryCliOptions(string? Profile, int? Dpi, string? Pages, bool SaveAllPages, bool NoHtml,
    bool Force, bool Quiet)
{
    public bool RawOverlay { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool NoRegions { get; init; }
}
public sealed record DirectorySummary(string Status, int Total, int Compared, int Same, int Different,
    int OnlyInA, int OnlyInB, int Error, int Ignored);
public sealed record DirectorySelectedRule(int Index, string Pattern, string Config);
public sealed record DirectoryFileError(string Code, string Message)
{
    public IReadOnlyList<DirectorySelectedRule> MatchedRules { get; init; } = [];
}
public sealed record DirectoryFileResult(string Id, string RelativePath, string? RelativePathA, string? RelativePathB,
    string Status, DirectorySelectedRule? SelectedRule, ReportSummary? Comparison, int? WarningCount,
    string? Json, string? Html, DirectoryFileError? Error);
public sealed record DirectoryIgnoredFile(string Side, string RelativePath, string Reason);
