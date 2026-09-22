using ReportDiff.Report;

namespace ReportDiff.Cli;

internal static class DirectoryComparison
{
    public static int Run(CompareCommand command, TextWriter output, TextWriter error,
        Func<CompareCommand, AppSettings, string, ReportDocument>? compare = null,
        Func<string, IEnumerable<DirectoryEntry>>? enumerate = null)
    {
        using var progress = ConsoleProgress.Create(output, command.Quiet);
        var rules = new DirectoryRules(command);
        var plan = DirectoryPlan.Create(command.InputA, command.InputB, command.Output, enumerate);
        using var workspace = new OutputWorkspace(command.Output, command.Force,
            rules.ProtectedFiles.Concat(new[] { plan.RootA, plan.RootB }).ToArray());
        var files = new List<DirectoryFileResult>();
        var warnings = new List<ReportWarning>();
        foreach (var pair in plan.Pairs)
        {
            if (pair.A is null || pair.B is null)
            {
                files.Add(Result(pair.A is null ? "only_in_b" : "only_in_a"));
                continue;
            }
            var matches = rules.Match(pair.A, pair.B);
            if (matches.Count > 1)
            {
                RecordError(new("CONFIG_RULE_AMBIGUOUS", "複数の帳票別設定が一致しました。選択定義を重複しない条件にしてください。")
                { MatchedRules = matches.Select(x => x.Description).ToArray() });
                continue;
            }
            var rule = matches.SingleOrDefault();
            var relative = $"files/{pair.Id}";
            var child = Path.Combine(workspace.StagingPath, "files", pair.Id);
            try
            {
                progress.File(pair.RelativePath);
                // 一覧用の / 区切りを、単一比較へ渡すときは OS 標準の絶対パスに戻す。
                var single = command with
                {
                    InputA = Path.GetFullPath(Path.Combine(plan.RootA, pair.A)),
                    InputB = Path.GetFullPath(Path.Combine(plan.RootB, pair.B)),
                    IsDirectory = false,
                    Rules = null
                };
                ReportDocument report;
                if (compare is not null) report = compare(single, rule?.Settings ?? rules.Common, child);
                else if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                    report = ComparisonRunner.Compare(single, rule?.Settings ?? rules.Common, child, progress);
                else throw new CommandLineException("この実行環境では比較処理に対応していません。");
                files.Add(Result(report.Summary.Status) with
                {
                    SelectedRule = rule?.Description,
                    Comparison = report.Summary,
                    WarningCount = report.Warnings.Count,
                    Json = relative + "/result.json",
                    Html = command.NoHtml ? null : relative + "/report.html"
                });
            }
            catch (Exception ex) when (ex is not ReportWriteException)
            {
                // 不完全な個別出力の削除に失敗した場合も全体を中止し、旧結果を保持する。
                if (Directory.Exists(child)) Directory.Delete(child, recursive: true);
                RecordError(new("COMPARISON_FAILED", CliApplication.ErrorMessage(ex)), rule?.Description);
            }

            DirectoryFileResult Result(string status) => new(pair.Id, pair.RelativePath, pair.A, pair.B, status, null, null, null, null, null, null);
            void RecordError(DirectoryFileError failure, DirectorySelectedRule? selected = null)
            {
                progress.EndLine();
                files.Add(Result("error") with { Error = failure, SelectedRule = selected });
                error.WriteLine($"エラー: {CliApplication.OneLine(pair.RelativePath)}: {CliApplication.OneLine(failure.Message)}");
            }
        }
        if (files.Count == 0)
        {
            var warning = new ReportWarning("NO_TARGET_FILES", "比較対象のファイルがありません。入力フォルダと対応拡張子を確認してください。");
            warnings.Add(warning); error.WriteLine("エラー: " + warning.Message);
        }
        int Count(string status) => files.Count(x => x.Status == status);
        var errors = Count("error"); var same = Count("same"); var different = Count("different");
        var onlyA = Count("only_in_a"); var onlyB = Count("only_in_b");
        var state = errors > 0 || files.Count == 0 ? "error" : different + onlyA + onlyB > 0 ? "different" : "same";
        var summary = new DirectorySummary(state, files.Count, same + different, same, different, onlyA, onlyB, errors, plan.Ignored.Count);
        var document = new DirectoryReportDocument(1, "directory_comparison", ReportTool.Current, DateTimeOffset.UtcNow,
            new(plan.RootA, plan.RootB), rules.Description, summary, files, plan.Ignored, warnings);
        progress.Report(directory: true);
        DirectoryReportWriter.Write(workspace.StagingPath, document, command.NoHtml);
        workspace.Commit();
        progress.EndLine();
        if (!command.Quiet)
            output.WriteLine($"{(state == "error" ? "エラーあり" : state == "same" ? "相違なし" : "相違あり")}: {files.Count} 件中 比較成功 {summary.Compared} 件、相違 {different} 件、A のみ {onlyA} 件、B のみ {onlyB} 件、エラー {errors} 件 → {CliApplication.OneLine(Path.Combine(command.Output, command.NoHtml ? "index.json" : "index.html"))}");
        return state == "error" ? 2 : state == "different" ? 1 : 0;
    }
}
