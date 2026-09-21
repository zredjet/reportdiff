using System.Globalization;
using ReportDiff.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ReportDiff.Cli;

public static class ConfigurationLoader
{
    public static AppSettings Load(string? yaml = null, string? profile = null, int? dpi = null)
    {
        var settings = new AppSettings();
        int? imageDpi = null;
        if (!string.IsNullOrWhiteSpace(yaml))
        {
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(yaml));
                if (stream.Documents.Count != 1)
                    throw Error("設定ファイル", "YAML ドキュメントは 1 個にしてください");
                var root = Mapping(stream.Documents[0].RootNode, "", "dpi", "image_dpi", "diff", "cluster", "move", "align", "exclude", "report");
                if (root.TryGetValue("dpi", out var node)) settings = settings with { Dpi = Integer(node, "dpi") };
                if (root.TryGetValue("image_dpi", out node)) imageDpi = Integer(node, "image_dpi");
                if (root.TryGetValue("diff", out node))
                {
                    var values = Mapping(node, "diff", "max_shift_mm", "color_threshold", "edge_tolerance");
                    settings = settings with { Diff = new DiffOptions
                    {
                        MaxShiftMm = Number(values, "max_shift_mm", "diff", 0.15),
                        ColorThreshold = Number(values, "color_threshold", "diff", 3),
                        EdgeTolerance = Number(values, "edge_tolerance", "diff", 0.3)
                    }};
                }
                if (root.TryGetValue("cluster", out node))
                {
                    var values = Mapping(node, "cluster", "merge_x_mm", "merge_y_mm", "min_pixels", "max_clusters_per_page", "max_diff_ratio");
                    settings = settings with { Cluster = new ClusterOptions
                    {
                        MergeXMm = Number(values, "merge_x_mm", "cluster", 3),
                        MergeYMm = Number(values, "merge_y_mm", "cluster", 1),
                        MinPixels = values.TryGetValue("min_pixels", out var min) ? Integer(min, "cluster.min_pixels") : 4,
                        MaxClustersPerPage = values.TryGetValue("max_clusters_per_page", out var max) ? Integer(max, "cluster.max_clusters_per_page") : 500,
                        MaxDiffRatio = Number(values, "max_diff_ratio", "cluster", 0.30)
                    }};
                }
                if (root.TryGetValue("move", out node))
                {
                    var values = Mapping(node, "move", "search_mm", "min_score", "min_score_gap");
                    settings = settings with { Move = new MoveOptions
                    {
                        SearchMm = Number(values, "search_mm", "move", 5),
                        MinScore = Number(values, "min_score", "move", 0.98),
                        MinScoreGap = Number(values, "min_score_gap", "move", 0.02)
                    }};
                }
                if (root.TryGetValue("align", out node))
                {
                    var values = Mapping(node, "align", "enabled", "max_shift_mm", "min_score", "min_score_gap", "min_improvement");
                    settings = settings with { Align = new AlignOptions
                    {
                        Enabled = values.TryGetValue("enabled", out var enabled) && Boolean(enabled, "align.enabled"),
                        MaxShiftMm = Number(values, "max_shift_mm", "align", 5),
                        MinScore = Number(values, "min_score", "align", 0.98),
                        MinScoreGap = Number(values, "min_score_gap", "align", 0.02),
                        MinImprovement = Number(values, "min_improvement", "align", 0.05)
                    }};
                }
                if (root.TryGetValue("report", out node))
                {
                    var values = Mapping(node, "report", "crop_margin_mm");
                    settings = settings with { Report = new ReportOptions { CropMarginMm = Number(values, "crop_margin_mm", "report", 2) } };
                }
                if (root.TryGetValue("exclude", out node))
                {
                    if (node is not YamlSequenceNode sequence) throw Error("exclude", "配列にしてください");
                    var exclusions = new List<ExclusionSetting>();
                    foreach (var entry in sequence.Children)
                    {
                        var key = $"exclude[{exclusions.Count}]";
                        var values = Mapping(entry, key, "page", "x", "y", "w", "h", "note");
                        foreach (var required in new[] { "page", "x", "y", "w", "h" })
                            if (!values.ContainsKey(required)) throw Error(key + "." + required, "必須です");
                        var page = Scalar(values["page"], key + ".page");
                        int? pageNumber = page == "all" ? null : Integer(values["page"], key + ".page");
                        if (pageNumber is <= 0) throw Error(key + ".page", "all または 1 以上の整数にしてください");
                        exclusions.Add(new(pageNumber, Number(values, "x", key, 0), Number(values, "y", key, 0),
                            Number(values, "w", key, 0), Number(values, "h", key, 0),
                            values.TryGetValue("note", out var note) ? Scalar(note, key + ".note") : ""));
                    }
                    settings = settings with { Exclude = exclusions.ToArray() };
                }
            }
            catch (YamlException ex)
            {
                throw new ConfigurationException($"設定ファイルの YAML が不正です（{ex.Start.Line} 行付近）。", ex);
            }
        }
        settings = settings with { ImageDpi = imageDpi ?? settings.Dpi };
        Validate(settings);
        if (profile is not null)
        {
            var (shift, edge) = profile switch
            {
                "normal" => (0.15, 0.3),
                "strict" => (0.0, 0.0),
                "loose" => (0.30, 0.3),
                _ => throw Error("profile", "normal / strict / loose のいずれかにしてください")
            };
            settings = settings with { Diff = settings.Diff with { MaxShiftMm = shift, EdgeTolerance = edge } };
        }
        settings = settings with { Dpi = dpi ?? settings.Dpi, ImageDpi = imageDpi ?? dpi ?? settings.Dpi };
        Validate(settings);
        return settings;
    }

    private static Dictionary<string, YamlNode> Mapping(YamlNode node, string path, params string[] keys)
    {
        if (node is not YamlMappingNode mapping) throw Error(path, "キーと値の組にしてください");
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var entry in mapping.Children)
        {
            var key = Scalar(entry.Key, path);
            var full = string.IsNullOrEmpty(path) ? key : path + "." + key;
            if (!keys.Contains(key)) throw Error(full, "未知のキーです");
            if (!result.TryAdd(key, entry.Value)) throw Error(full, "キーが重複しています");
        }
        return result;
    }

    private static string Scalar(YamlNode node, string path) =>
        node is YamlScalarNode { Value: not null } scalar ? scalar.Value : throw Error(path, "値を指定してください");

    private static int Integer(YamlNode node, string path) =>
        int.TryParse(Scalar(node, path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw Error(path, "整数を指定してください");

    private static bool Boolean(YamlNode node, string path) => Scalar(node, path) switch
    {
        "true" => true, "false" => false,
        _ => throw Error(path, "true または false を指定してください")
    };

    private static double Number(Dictionary<string, YamlNode> values, string key, string path, double fallback)
    {
        if (!values.TryGetValue(key, out var node)) return fallback;
        if (!double.TryParse(Scalar(node, path + "." + key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)) throw Error(path + "." + key, "有限の数値を指定してください");
        return value;
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.Dpi is < 72 or > 1200) throw Error("dpi", "72〜1200 にしてください");
        if (settings.ImageDpi is < 72 or > 1200) throw Error("image_dpi", "72〜1200 にしてください");
        Nonnegative(settings.Diff.MaxShiftMm, "diff.max_shift_mm");
        Nonnegative(settings.Diff.ColorThreshold, "diff.color_threshold");
        if (settings.Diff.EdgeTolerance is < 0 or >= 1) throw Error("diff.edge_tolerance", "0 以上 1 未満にしてください");
        Nonnegative(settings.Cluster.MergeXMm, "cluster.merge_x_mm");
        Nonnegative(settings.Cluster.MergeYMm, "cluster.merge_y_mm");
        if (settings.Cluster.MinPixels < 1) throw Error("cluster.min_pixels", "1 以上にしてください");
        if (settings.Cluster.MaxClustersPerPage < 1) throw Error("cluster.max_clusters_per_page", "1 以上にしてください");
        if (settings.Cluster.MaxDiffRatio is <= 0 or > 1) throw Error("cluster.max_diff_ratio", "0 より大きく 1 以下にしてください");
        if (!double.IsFinite(settings.Move.SearchMm) || settings.Move.SearchMm is < 0 or > 20)
            throw Error("move.search_mm", "0〜20mm にしてください（0 は移動注釈を無効化）");
        if (!double.IsFinite(settings.Move.MinScore) || settings.Move.MinScore is <= 0 or > 1)
            throw Error("move.min_score", "0 より大きく 1 以下にしてください");
        if (!double.IsFinite(settings.Move.MinScoreGap) || settings.Move.MinScoreGap is <= 0 or > 1)
            throw Error("move.min_score_gap", "0 より大きく 1 以下にしてください");
        Nonnegative(settings.Report.CropMarginMm, "report.crop_margin_mm");
        if (!double.IsFinite(settings.Align.MaxShiftMm) || settings.Align.MaxShiftMm is < 0 or > 20)
            throw Error("align.max_shift_mm", "0〜20mm にしてください（0 は全体補正の探索を省略）");
        foreach (var (value, key) in new[] { (settings.Align.MinScore, "min_score"),
            (settings.Align.MinScoreGap, "min_score_gap"), (settings.Align.MinImprovement, "min_improvement") })
            if (!double.IsFinite(value) || value is <= 0 or > 1)
                throw Error("align." + key, "0 より大きく 1 以下にしてください");
        foreach (var e in settings.Exclude)
        {
            Nonnegative(e.X, "exclude.x"); Nonnegative(e.Y, "exclude.y");
            Nonnegative(e.W, "exclude.w"); Nonnegative(e.H, "exclude.h");
        }
    }

    private static void Nonnegative(double value, string path)
    {
        if (!double.IsFinite(value) || value < 0) throw Error(path, "0 以上の有限の数値にしてください");
    }

    private static ConfigurationException Error(string path, string message) => new($"設定 {path}: {message}。");
}
