using System.Globalization;
using ReportDiff.Core;
using ReportDiff.Pdf;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ReportDiff.Cli;

public static class ConfigurationLoader
{
    // 省略の有無（特に image_dpi）を保つため、実効設定ではなく YAML の指定キーを重ねる。
    public static AppSettings LoadLayered(string? common, string? selected, string? profile = null, int? dpi = null)
    {
        Load(common); Load(selected); // 上書きによって隠れる不正な値も拒否する。
        var root = Parse(common);
        Merge(root, Parse(selected));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        return Load(writer.ToString(), profile, dpi);

        static YamlMappingNode Parse(string? yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml)) return new YamlMappingNode();
            var stream = new YamlStream(); stream.Load(new StringReader(yaml));
            // 別ファイルの同名アンカーを合成後の YAML で取り違えないよう、
            // 検証済みの参照先を値として複製し、アンカー名を持ち越さない。
            return (YamlMappingNode)CopyValue(stream.Documents[0].RootNode);
        }
        static YamlNode CopyValue(YamlNode node) => node switch
        {
            YamlScalarNode scalar => new YamlScalarNode(scalar.Value) { Style = scalar.Style, Tag = scalar.Tag },
            YamlMappingNode mapping => new YamlMappingNode(mapping.Children.Select(x =>
                new KeyValuePair<YamlNode, YamlNode>(CopyValue(x.Key), CopyValue(x.Value)))),
            YamlSequenceNode sequence => new YamlSequenceNode(sequence.Children.Select(CopyValue)),
            _ => throw new ConfigurationException("設定 YAML の値を読み込めません。")
        };
        static void Merge(YamlMappingNode target, YamlMappingNode overlay)
        {
            foreach (var (key, value) in overlay.Children)
                if (value is YamlMappingNode mapping && target.Children.TryGetValue(key, out var old) && old is YamlMappingNode previous)
                    Merge(previous, mapping);
                else target.Children[key] = value;
        }
    }

    public static AppSettings Load(string? yaml = null, string? profile = null, int? dpi = null)
    {
        var settings = new AppSettings();
        int? imageDpi = null;
        if (!string.IsNullOrWhiteSpace(yaml))
        {
            try
            {
                RejectDuplicateKeys(yaml);
                var stream = new YamlStream();
                stream.Load(new StringReader(yaml));
                if (stream.Documents.Count != 1)
                    throw Error("設定ファイル", "YAML ドキュメントは 1 個にしてください");
                var root = Mapping(stream.Documents[0].RootNode, "", "dpi", "image_dpi", "diff", "ink", "cluster", "move", "align", "text", "exclude", "report");
                if (root.TryGetValue("dpi", out var node)) settings = settings with { Dpi = Integer(node, "dpi") };
                if (root.TryGetValue("image_dpi", out node)) imageDpi = Integer(node, "image_dpi");
                if (root.TryGetValue("diff", out node))
                {
                    var values = Mapping(node, "diff", "max_shift_mm", "color_threshold", "edge_tolerance");
                    settings = settings with { Diff = new DiffOptions
                    {
                        MaxShiftMm = Number(values, "max_shift_mm", "diff", settings.Diff.MaxShiftMm),
                        ColorThreshold = Number(values, "color_threshold", "diff", settings.Diff.ColorThreshold),
                        EdgeTolerance = Number(values, "edge_tolerance", "diff", settings.Diff.EdgeTolerance)
                    }};
                }
                if (root.TryGetValue("ink", out node))
                {
                    var values = Mapping(node, "ink", "background_radius_mm", "contrast_threshold");
                    settings = settings with { Ink = new InkOptions
                    {
                        BackgroundRadiusMm = Number(values, "background_radius_mm", "ink", settings.Ink.BackgroundRadiusMm),
                        ContrastThreshold = Number(values, "contrast_threshold", "ink", settings.Ink.ContrastThreshold)
                    }};
                }
                if (root.TryGetValue("cluster", out node))
                {
                    var values = Mapping(node, "cluster", "merge_x_mm", "merge_y_mm", "min_pixels", "max_clusters_per_page", "max_diff_ratio", "reading_band_mm");
                    settings = settings with { Cluster = new ClusterOptions
                    {
                        MergeXMm = Number(values, "merge_x_mm", "cluster", settings.Cluster.MergeXMm),
                        MergeYMm = Number(values, "merge_y_mm", "cluster", settings.Cluster.MergeYMm),
                        MinPixels = Integer(values, "min_pixels", "cluster", settings.Cluster.MinPixels),
                        MaxClustersPerPage = Integer(values, "max_clusters_per_page", "cluster", settings.Cluster.MaxClustersPerPage),
                        MaxDiffRatio = Number(values, "max_diff_ratio", "cluster", settings.Cluster.MaxDiffRatio),
                        ReadingBandMm = Number(values, "reading_band_mm", "cluster", settings.Cluster.ReadingBandMm)
                    }};
                }
                if (root.TryGetValue("move", out node))
                {
                    var values = Mapping(node, "move", "search_mm", "min_score", "min_score_gap", "template_margin_mm");
                    settings = settings with { Move = new MoveOptions
                    {
                        SearchMm = Number(values, "search_mm", "move", settings.Move.SearchMm),
                        MinScore = Number(values, "min_score", "move", settings.Move.MinScore),
                        MinScoreGap = Number(values, "min_score_gap", "move", settings.Move.MinScoreGap),
                        TemplateMarginMm = Number(values, "template_margin_mm", "move", settings.Move.TemplateMarginMm)
                    }};
                }
                if (root.TryGetValue("align", out node))
                {
                    var values = Mapping(node, "align", "enabled", "max_shift_mm", "min_score", "min_score_gap", "min_improvement",
                        "coarse_max_side_samples", "refine_radius_samples", "min_support_cells", "min_support_rows", "min_support_columns", "min_ink_area_mm2");
                    settings = settings with { Align = new AlignOptions
                    {
                        Enabled = values.TryGetValue("enabled", out var enabled) ? Boolean(enabled, "align.enabled") : settings.Align.Enabled,
                        MaxShiftMm = Number(values, "max_shift_mm", "align", settings.Align.MaxShiftMm),
                        MinScore = Number(values, "min_score", "align", settings.Align.MinScore),
                        MinScoreGap = Number(values, "min_score_gap", "align", settings.Align.MinScoreGap),
                        MinImprovement = Number(values, "min_improvement", "align", settings.Align.MinImprovement),
                        CoarseMaxSideSamples = Integer(values, "coarse_max_side_samples", "align", settings.Align.CoarseMaxSideSamples),
                        RefineRadiusSamples = Integer(values, "refine_radius_samples", "align", settings.Align.RefineRadiusSamples),
                        MinSupportCells = Integer(values, "min_support_cells", "align", settings.Align.MinSupportCells),
                        MinSupportRows = Integer(values, "min_support_rows", "align", settings.Align.MinSupportRows),
                        MinSupportColumns = Integer(values, "min_support_columns", "align", settings.Align.MinSupportColumns),
                        MinInkAreaMm2 = Number(values, "min_ink_area_mm2", "align", settings.Align.MinInkAreaMm2)
                    }};
                }
                if (root.TryGetValue("text", out node))
                {
                    var values = Mapping(node, "text", "max_letters_per_page", "max_words_per_page", "max_runes_per_cluster", "min_line_overlap");
                    settings = settings with { Text = new TextOptions
                    {
                        MaxLettersPerPage = Integer(values, "max_letters_per_page", "text", settings.Text.MaxLettersPerPage),
                        MaxWordsPerPage = Integer(values, "max_words_per_page", "text", settings.Text.MaxWordsPerPage),
                        MaxRunesPerCluster = Integer(values, "max_runes_per_cluster", "text", settings.Text.MaxRunesPerCluster),
                        MinLineOverlap = Number(values, "min_line_overlap", "text", settings.Text.MinLineOverlap)
                    }};
                }
                if (root.TryGetValue("report", out node))
                {
                    var values = Mapping(node, "report", "crop_margin_mm", "snippet_margin_mm");
                    settings = settings with { Report = new ReportOptions
                    {
                        CropMarginMm = Number(values, "crop_margin_mm", "report", settings.Report.CropMarginMm),
                        SnippetMarginMm = Number(values, "snippet_margin_mm", "report", settings.Report.SnippetMarginMm)
                    }};
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
                "normal" => (new DiffOptions().MaxShiftMm, new DiffOptions().EdgeTolerance),
                "strict" => (0.0, 0.0),
                "loose" => (0.30, new DiffOptions().EdgeTolerance),
                _ => throw Error("profile", "normal / strict / loose のいずれかにしてください")
            };
            settings = settings with { Diff = settings.Diff with { MaxShiftMm = shift, EdgeTolerance = edge } };
        }
        settings = settings with { Dpi = dpi ?? settings.Dpi, ImageDpi = imageDpi ?? dpi ?? settings.Dpi };
        Validate(settings);
        ValidatePixelConversions(settings);
        return settings;
    }

    // YamlStream は重複を拒否するがキーのパスを残さないため、イベント段階で先に確認する。
    internal static void RejectDuplicateKeys(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        while (!parser.Accept<StreamEnd>(out _))
        {
            parser.Consume<DocumentStart>();
            Visit("");
            parser.Consume<DocumentEnd>();
        }
        parser.Consume<StreamEnd>();

        void Visit(string path)
        {
            if (parser.TryConsume<MappingStart>(out _))
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                while (!parser.Accept<MappingEnd>(out _))
                {
                    if (!parser.TryConsume<YamlDotNet.Core.Events.Scalar>(out var scalar))
                        throw Error(path, "キーは文字列にしてください");
                    var key = scalar.Value;
                    var full = path.Length == 0 ? key : path + "." + key;
                    if (!keys.Add(key)) throw Error(full, "キーが重複しています");
                    Visit(full);
                }
                parser.Consume<MappingEnd>();
            }
            else if (parser.TryConsume<SequenceStart>(out _))
            {
                var index = 0;
                while (!parser.Accept<SequenceEnd>(out _)) Visit($"{path}[{index++}]");
                parser.Consume<SequenceEnd>();
            }
            else if (!parser.TryConsume<AnchorAlias>(out _)) parser.Consume<YamlDotNet.Core.Events.Scalar>();
        }
    }

    internal static Dictionary<string, YamlNode> Mapping(YamlNode node, string path, params string[] keys)
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

    internal static string Scalar(YamlNode node, string path) =>
        node is YamlScalarNode { Value: not null } scalar ? scalar.Value : throw Error(path, "値を指定してください");

    private static int Integer(YamlNode node, string path) =>
        int.TryParse(Scalar(node, path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw Error(path, "整数を指定してください");

    private static int Integer(Dictionary<string, YamlNode> values, string key, string path, int fallback) =>
        values.TryGetValue(key, out var node) ? Integer(node, path + "." + key) : fallback;

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
        Range(settings.Ink.BackgroundRadiusMm, "ink.background_radius_mm", 0, 20, positive: true);
        Range(settings.Ink.ContrastThreshold, "ink.contrast_threshold", 0, 100);
        if (settings.Diff.EdgeTolerance is < 0 or >= 1) throw Error("diff.edge_tolerance", "0 以上 1 未満にしてください");
        Nonnegative(settings.Cluster.MergeXMm, "cluster.merge_x_mm");
        Nonnegative(settings.Cluster.MergeYMm, "cluster.merge_y_mm");
        if (settings.Cluster.MinPixels < 1) throw Error("cluster.min_pixels", "1 以上にしてください");
        if (settings.Cluster.MaxClustersPerPage < 1) throw Error("cluster.max_clusters_per_page", "1 以上にしてください");
        if (settings.Cluster.MaxDiffRatio is <= 0 or > 1) throw Error("cluster.max_diff_ratio", "0 より大きく 1 以下にしてください");
        Range(settings.Cluster.ReadingBandMm, "cluster.reading_band_mm", 0, 1000, positive: true);
        Range(settings.Move.TemplateMarginMm, "move.template_margin_mm", 0, 20);
        if (!double.IsFinite(settings.Move.SearchMm) || settings.Move.SearchMm is < 0 or > 20)
            throw Error("move.search_mm", "0〜20mm にしてください（0 は移動注釈を無効化）");
        if (!double.IsFinite(settings.Move.MinScore) || settings.Move.MinScore is <= 0 or > 1)
            throw Error("move.min_score", "0 より大きく 1 以下にしてください");
        if (!double.IsFinite(settings.Move.MinScoreGap) || settings.Move.MinScoreGap is <= 0 or > 1)
            throw Error("move.min_score_gap", "0 より大きく 1 以下にしてください");
        Nonnegative(settings.Report.CropMarginMm, "report.crop_margin_mm");
        Range(settings.Report.SnippetMarginMm, "report.snippet_margin_mm", 0, 20);
        Range(settings.Text.MaxLettersPerPage, "text.max_letters_per_page", 1, 1_000_000);
        Range(settings.Text.MaxWordsPerPage, "text.max_words_per_page", 1, 200_000);
        Range(settings.Text.MaxRunesPerCluster, "text.max_runes_per_cluster", 1, 100_000);
        Range(settings.Text.MinLineOverlap, "text.min_line_overlap", 0, 1, positive: true);
        Range(settings.Align.CoarseMaxSideSamples, "align.coarse_max_side_samples", 64, 4096);
        Range(settings.Align.RefineRadiusSamples, "align.refine_radius_samples", 1, 8);
        Range(settings.Align.MinSupportCells, "align.min_support_cells", 1, 9);
        Range(settings.Align.MinSupportRows, "align.min_support_rows", 1, 3);
        Range(settings.Align.MinSupportColumns, "align.min_support_columns", 1, 3);
        Range(settings.Align.MinInkAreaMm2, "align.min_ink_area_mm2", 0, 10000, positive: true);
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

    private static void Range(double value, string path, double minimum, double maximum, bool positive = false)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum || (positive && value == minimum))
            throw Error(path, $"{minimum} {(positive ? "より大きく" : "以上")} {maximum} 以下の有限の数値にしてください");
    }

    private static void ValidatePixelConversions(AppSettings settings)
    {
        // mm の上限を新設せず、実際に用いる整数・カーネル・探索配列の表現限界を確認する。
        foreach (var dpi in new[] { settings.Dpi, settings.ImageDpi }.Distinct())
        {
            var shift = Math.Round(Units.MmToPixels(settings.Diff.MaxShiftMm, dpi));
            if (shift > (Math.Sqrt(int.MaxValue) - 1) / 2)
                throw Error("diff.max_shift_mm", $"{dpi}dpi では探索候補数が整数の上限を超えます");
            foreach (var (mm, key) in new[] { (settings.Cluster.MergeXMm, "cluster.merge_x_mm"),
                (settings.Cluster.MergeYMm, "cluster.merge_y_mm") })
                if (Math.Ceiling(Units.MmToPixels(mm, dpi) / 2) > (int.MaxValue - 1) / 2)
                    throw Error(key, $"{dpi}dpi では結合カーネルが整数の上限を超えます");
        }
        foreach (var e in settings.Exclude)
            if (!double.IsFinite(e.X + e.W) || !double.IsFinite(e.Y + e.H))
                throw Error("exclude", "座標と大きさの合計を有限値にしてください");
    }

    private static ConfigurationException Error(string path, string message) => new($"設定 {path}: {message}。");
}
