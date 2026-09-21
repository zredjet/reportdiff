using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ReportDiff.Report;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ReportDiff.Cli;

/// <summary>設定の内容と出典を一度だけ読み、すべての候補を比較開始前に検証する。</summary>
internal sealed class DirectoryRules
{
    private sealed record Snapshot(ConfigurationSource Source, string Text);
    internal sealed record Rule(DirectorySelectedRule Description, Regex Pattern, AppSettings Settings);
    public AppSettings Common { get; }
    public IReadOnlyList<Rule> Rules { get; }
    public DirectoryConfiguration Description { get; }
    public IEnumerable<string> ProtectedFiles => new[] { Description.Common, Description.Rules }
        .Where(x => x is not null).Select(x => x!.Path).Concat(Description.Referenced.Select(x => x.Path));

    public DirectoryRules(CompareCommand command)
    {
        var snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        Snapshot Read(string path)
        {
            path = Path.GetFullPath(path);
            if (snapshots.TryGetValue(path, out var existing)) return existing;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var text = new UTF8Encoding(false, true).GetString(bytes);
                // ReadAllText と同じく UTF-8 BOM を許可する。ハッシュには BOM も含める。
                if (text.StartsWith('\uFEFF')) text = text[1..];
                var snapshot = new Snapshot(new(path, Convert.ToHexStringLower(SHA256.HashData(bytes))), text);
                snapshots.Add(path, snapshot);
                return snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            { throw new ConfigurationException($"設定ファイルを読み込めません: {path}"); }
        }

        var common = command.Config is null ? null : Read(command.Config);
        Common = command.ApplyReportOptions(ConfigurationLoader.Load(common?.Text, command.Profile, command.Dpi));
        var rules = command.Rules is null ? null : Read(command.Rules);
        var loaded = new List<Rule>();
        var referenced = new List<ConfigurationSource>();
        if (rules is not null)
        {
            try
            {
                ConfigurationLoader.RejectDuplicateKeys(rules.Text);
                var stream = new YamlStream(); stream.Load(new StringReader(rules.Text));
                if (stream.Documents.Count != 1) throw new ConfigurationException("選択定義の YAML ドキュメントは 1 個にしてください。");
                var root = ConfigurationLoader.Mapping(stream.Documents[0].RootNode, "", "schema_version", "rules");
                if (!root.TryGetValue("schema_version", out var version) || ConfigurationLoader.Scalar(version, "schema_version") != "1")
                    throw new ConfigurationException("選択定義の schema_version は 1 を指定してください。");
                if (!root.TryGetValue("rules", out var sequence) || sequence is not YamlSequenceNode items)
                    throw new ConfigurationException("選択定義の rules は配列を指定してください。");
                foreach (var item in items.Children)
                {
                    var index = loaded.Count + 1;
                    var mapping = ConfigurationLoader.Mapping(item, $"rules[{index}]", "pattern", "config");
                    string Required(string key)
                    {
                        if (!mapping.TryGetValue(key, out var node) || string.IsNullOrWhiteSpace(ConfigurationLoader.Scalar(node, key)))
                            throw new ConfigurationException($"rules[{index}].{key} を指定してください。");
                        return ConfigurationLoader.Scalar(node, key);
                    }
                    var pattern = Required("pattern");
                    Regex regex;
                    try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    { throw new ConfigurationException($"rules[{index}].pattern の正規表現が不正または非対応です。先読み・後読み・後方参照は使用できません。"); }
                    var config = Read(Path.Combine(Path.GetDirectoryName(rules.Source.Path)!, Required("config")));
                    var settings = command.ApplyReportOptions(ConfigurationLoader.LoadLayered(common?.Text, config.Text, command.Profile, command.Dpi));
                    loaded.Add(new(new(index, pattern, config.Source.Path), regex, settings));
                    if (!referenced.Contains(config.Source)) referenced.Add(config.Source);
                }
            }
            catch (YamlException) { throw new ConfigurationException("選択定義の YAML が不正です。キー・型・インデントを確認してください。"); }
        }
        Rules = loaded;
        Description = new(common?.Source, rules?.Source, referenced,
            new(command.Profile, command.Dpi, command.Pages, command.SaveAllPages, command.NoHtml, command.Force, command.Quiet)
                { RawOverlay = command.RawOverlay, NoRegions = command.NoRegions });
    }

    public IReadOnlyList<Rule> Match(string a, string b) => Rules.Where(rule =>
        rule.Pattern.IsMatch(a.Normalize(NormalizationForm.FormC)) || rule.Pattern.IsMatch(b.Normalize(NormalizationForm.FormC))).ToArray();
}
