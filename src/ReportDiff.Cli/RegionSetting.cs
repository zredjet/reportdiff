using ReportDiff.Core;
using ReportDiff.Report;
using YamlDotNet.RepresentationModel;

namespace ReportDiff.Cli;

public sealed record RegionSetting(string Name, int? Page, double X, double Y, double W, double H,
    string Mode = "compare", string? Profile = null, RegionDiffOverride? Diff = null)
{
    public RectMm Bounds => new(X, Y, W, H);
    public DiffOptions Resolve(DiffOptions baseline) => (Diff ?? new()).Apply(ConfigurationLoader.ApplyProfile(baseline, Profile));
    public ReportRegion ToReport(DiffOptions baseline) => new(Name, Page, X, Y, W, H, Mode, Profile, Diff,
        Mode == "compare" ? Resolve(baseline) : null);
}

public static partial class ConfigurationLoader
{
    internal static DiffOptions ApplyProfile(DiffOptions diff, string? profile, string key = "profile") => profile switch
    {
        null => diff,
        "normal" => diff with { MaxShiftMm = new DiffOptions().MaxShiftMm, EdgeTolerance = new DiffOptions().EdgeTolerance },
        "strict" => diff with { MaxShiftMm = 0, EdgeTolerance = 0 },
        "loose" => diff with { MaxShiftMm = 0.30, EdgeTolerance = new DiffOptions().EdgeTolerance },
        _ => throw Error(key, "normal / strict / loose のいずれかにしてください")
    };

    private static RegionSetting[] ReadRegions(YamlNode node)
    {
        if (node is not YamlSequenceNode sequence) throw Error("regions", "配列にしてください");
        var result = new List<RegionSetting>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in sequence.Children)
        {
            var key = $"regions[{result.Count}]";
            var values = Mapping(entry, key, "name", "page", "x", "y", "w", "h", "mode", "profile", "diff");
            foreach (var required in new[] { "name", "page", "x", "y", "w", "h" })
                if (!values.ContainsKey(required)) throw Error(key + "." + required, "必須です");
            var name = Scalar(values["name"], key + ".name");
            if (string.IsNullOrWhiteSpace(name)) throw Error(key + ".name", "空でない名前を指定してください");
            if (!names.Add(name)) throw Error(key + ".name", "名前が重複しています");
            var page = Scalar(values["page"], key + ".page");
            int? number = page is "all" or "null" or "~" or "" ? null : Integer(values["page"], key + ".page");
            if (number <= 0) throw Error(key + ".page", "all または 1 以上の整数にしてください");
            var mode = values.TryGetValue("mode", out var modeNode) ? Scalar(modeNode, key + ".mode") : "compare";
            if (mode is not ("compare" or "exclude")) throw Error(key + ".mode", "compare / exclude のいずれかにしてください");
            if (mode == "exclude" && (values.ContainsKey("profile") || values.ContainsKey("diff")))
                throw Error(key + (values.ContainsKey("profile") ? ".profile" : ".diff"), "mode: exclude と併用できません");
            var profile = values.TryGetValue("profile", out var profileNode) ? Scalar(profileNode, key + ".profile") : null;
            ApplyProfile(new(), profile, key + ".profile");
            RegionDiffOverride? diff = null;
            if (values.TryGetValue("diff", out var diffNode))
            {
                var fields = Mapping(diffNode, key + ".diff", "max_shift_mm", "color_threshold", "edge_tolerance");
                double? Read(string name) => fields.ContainsKey(name) ? Number(fields, name, key + ".diff", 0) : null;
                diff = new(Read("max_shift_mm"), Read("color_threshold"), Read("edge_tolerance"));
            }
            result.Add(new(name, number, Number(values, "x", key, 0), Number(values, "y", key, 0),
                Number(values, "w", key, 0), Number(values, "h", key, 0), mode, profile, diff));
        }
        return result.ToArray();
    }

    private static void ValidateRegions(AppSettings settings, bool pixels)
    {
        for (var i = 0; i < settings.Regions.Count; i++)
        {
            var r = settings.Regions[i]; var key = $"regions[{i}]";
            Nonnegative(r.X, key + ".x"); Nonnegative(r.Y, key + ".y");
            Range(r.W, key + ".w", 0, double.MaxValue, positive: true);
            Range(r.H, key + ".h", 0, double.MaxValue, positive: true);
            if (!double.IsFinite(r.X + r.W)) throw Error(key + ".w", "座標との合計を有限値にしてください");
            if (!double.IsFinite(r.Y + r.H)) throw Error(key + ".h", "座標との合計を有限値にしてください");
            var diff = r.Resolve(settings.Diff);
            Nonnegative(diff.MaxShiftMm, key + ".diff.max_shift_mm");
            Nonnegative(diff.ColorThreshold, key + ".diff.color_threshold");
            if (!double.IsFinite(diff.EdgeTolerance) || diff.EdgeTolerance is < 0 or >= 1)
                throw Error(key + ".diff.edge_tolerance", "0 以上 1 未満にしてください");
            if (pixels)
            foreach (var dpi in new[] { settings.Dpi, settings.ImageDpi }.Distinct())
            {
                if (Math.Round(Units.MmToPixels(diff.MaxShiftMm, dpi)) > (Math.Sqrt(int.MaxValue) - 1) / 2)
                    throw Error(key + ".diff.max_shift_mm", $"{dpi}dpi では探索候補数が整数の上限を超えます");
                foreach (var (value, field) in new[] { (r.X + r.W, "w"), (r.Y + r.H, "h") })
                    if (Math.Ceiling(Units.MmToPixels(value, dpi)) > int.MaxValue)
                        throw Error(key + "." + field, $"{dpi}dpi では座標が整数の上限を超えます");
            }
            for (var j = 0; j < i; j++)
            {
                var other = settings.Regions[j];
                if (r.Page is not null && other.Page is not null && r.Page != other.Page) continue;
                try
                {
                    RegionGeometry.ValidatePair(r.Bounds, other.Bounds, $"{key} / regions[{j}]");
                    if (pixels)
                        foreach (var dpi in new[] { settings.Dpi, settings.ImageDpi }.Distinct())
                            RegionGeometry.ValidatePair(r.Bounds, other.Bounds, $"{key} / regions[{j}]", dpi);
                }
                catch (ArgumentException e) { throw new ConfigurationException("設定 " + e.Message, e); }
            }
        }
    }
}
