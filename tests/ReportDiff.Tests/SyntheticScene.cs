using OpenCvSharp;
using ReportDiff.Core;

namespace ReportDiff.Tests;

// reference/prototype.py の Scene と描画命令。4 倍で描き、INTER_AREA で縮小する。
internal sealed class SyntheticScene
{
    private const int Scale = 4;
    private readonly List<(string Group, Action<Mat, int, int> Draw)> operations = [];
    private static int Mm(double value) => Units.RoundPixels(value, 300 * Scale);
    private static int Thickness(double width) => Math.Max(1, (int)Math.Round(width * Scale));

    private SyntheticScene Add(string group, Action<Mat, int, int> draw)
    {
        operations.Add((group, draw)); return this;
    }

    public Mat Render(double dx = 0, double dy = 0, IReadOnlyDictionary<string, (double X, double Y)>? groupShifts = null)
    {
        using var large = new Mat(960 * Scale, 1350 * Scale, MatType.CV_8UC3, Scalar.All(255));
        foreach (var (group, draw) in operations)
        {
            var offset = groupShifts?.GetValueOrDefault(group) ?? (0d, 0d);
            draw(large, (int)Math.Round((dx + offset.Item1) * Scale), (int)Math.Round((dy + offset.Item2) * Scale));
        }
        var result = new Mat();
        try { Cv2.Resize(large, result, new Size(1350, 960), interpolation: InterpolationFlags.Area); return result; }
        catch { result.Dispose(); throw; }
    }

    private SyntheticScene Line(string group, double x0, double y0, double x1, double y1, Scalar color, double width = 1) =>
        Add(group, (m, ox, oy) => Cv2.Line(m, new(Mm(x0) + ox, Mm(y0) + oy), new(Mm(x1) + ox, Mm(y1) + oy), color, Thickness(width)));

    private SyntheticScene Rectangle(string group, double x, double y, double width, double height, double stroke = 1, Scalar? fill = null) =>
        Add(group, (m, ox, oy) =>
        {
            var p0 = new Point(Mm(x) + ox, Mm(y) + oy); var p1 = new Point(Mm(x + width) + ox, Mm(y + height) + oy);
            if (fill is { } color) Cv2.Rectangle(m, p0, p1, color, -1);
            if (stroke > 0) Cv2.Rectangle(m, p0, p1, Scalar.All(0), Thickness(stroke));
        });

    private SyntheticScene Text(string group, double x, double y, string text) =>
        Add(group, (m, ox, oy) =>
        {
            var scale = Mm(3.2) / 22.0;
            Cv2.PutText(m, text, new(Mm(x) + ox, Mm(y) + oy), HersheyFonts.HersheySimplex,
                scale, Scalar.All(0), Math.Max(1, (int)Math.Round(scale * 1.6)), LineTypes.Link8);
        });

    // Python の periodic_scene と同じ整数境界・濃淡丸め。比較設定は変更しない。
    internal static SyntheticScene Periodic(int period = 4, int fade = 128, bool vertical = false, bool missing = false)
    {
        var scene = new SyntheticScene();
        var length = 2 * fade + 24 * period;
        var half = period / 2;
        for (var offset = 0; offset < length; offset++)
        {
            if (offset % period >= half || missing && offset >= fade + 12 * period && offset < fade + 12 * period + half)
                continue;
            var position = offset;
            var gray = 255 - (int)Math.Round(255.0 * Math.Min(Math.Min(offset, length - 1 - offset), fade) / fade);
            scene.Add("periodic", (m, ox, oy) =>
            {
                var x = vertical ? 240 : 240 + position;
                var y = vertical ? 240 + position : 240;
                var width = vertical ? 16 : 1;
                var height = vertical ? 1 : 16;
                using var strip = new Mat(m, new Rect(x * Scale + ox, y * Scale + oy, width * Scale, height * Scale));
                strip.SetTo(Scalar.All(gray));
            });
        }
        return scene;
    }

    public static Mat Original(string id) => id switch
    {
        "I10" or "D20" => Periodic().Render(),
        "I11" => Periodic(vertical: true).Render(),
        "I12" or "D21" => Periodic(8, 256).Render(),
        _ => Base().Render()
    };

    public static SyntheticScene Base(string amount = "120", bool dot = true, bool minus = true, double barEnd = 70,
        Scalar? barColor = null, bool dashed = false, Scalar? cellFill = null, bool marker = false,
        double frame = 1, string second = "ABC", string pair = "XY", Scalar? bar2Color = null)
    {
        var s = new SyntheticScene();
        s.Rectangle("table", 8, 8, 96, 24, frame);
        foreach (var y in new[] { 16, 24 }) s.Line("table", 8, y, 104, y, Scalar.All(0));
        foreach (var x in new[] { 40, 72 }) s.Line("table", x, 8, x, 32, Scalar.All(0));
        if (cellFill is not null) s.Rectangle("table", 73, 17, 30, 6, 0, cellFill);
        s.Text("t1", 10, 14, "QTY").Text("t2", 42, 14, amount);
        s.Text("t3", 10, 22, "RATE").Text("t4", 42, 22, "1");
        if (dot) s.Add("t4", (m, ox, oy) => Cv2.Circle(m, new(Mm(45.6) + ox, Mm(21.8) + oy), Math.Max(1, Mm(0.45) / 2), Scalar.All(0), -1));
        s.Text("t4", 46.4, 22, "25").Text("t5", 10, 30, "BAL");
        if (minus) s.Line("t6", 42, 28.6, 43.6, 28.6, Scalar.All(0), 1.4);
        s.Text("t6", 44.2, 30, "300").Text("t7", 74, 14, second).Text("t8", 74, 30, pair);
        s.Text("g1", 8, 44, "TASK A");
        var color = barColor ?? new Scalar(160, 60, 0);
        if (dashed)
            for (double x = 30; x < barEnd; x += 3) s.Line("bar", x, 43, Math.Min(x + 2, barEnd), 43, color);
        else s.Line("bar", 30, 43, barEnd, 43, color);
        s.Text("g2", 8, 54, "TASK B").Line("bar2", 30, 53, 90, 53, bar2Color ?? Scalar.All(0), 3);
        if (marker) s.Add("mk", (m, ox, oy) => Cv2.FillPoly(m,
            new[] { new[] { new Point(Mm(60) + ox, Mm(61) + oy), new Point(Mm(59) + ox, Mm(63) + oy), new Point(Mm(61) + ox, Mm(63) + oy) } }, Scalar.All(0)));
        return s;
    }

    public static Mat Changed(string id)
    {
        (double X, double Y)[] subpixel = [(0.25, 0), (0.5, 0), (0.75, 0), (0, 0.5), (0.5, 0.5)];
        (double X, double Y)[] integer = [(1, 0), (0, 1), (1, 1), (-1, 1), (1.5, 0)];
        if (id.StartsWith("I02-", StringComparison.Ordinal))
        {
            var shift = subpixel[int.Parse(id[4..]) - 1]; return Base().Render(shift.X, shift.Y);
        }
        if (id.StartsWith("I03-", StringComparison.Ordinal))
        {
            var shift = integer[int.Parse(id[4..]) - 1]; return Base().Render(shift.X, shift.Y);
        }
        return id switch
        {
            "I10" => Periodic().Render(2, 0),
            "I11" => Periodic(vertical: true).Render(0, 2),
            "I12" => Periodic(8, 256).Render(4, 0),
            "D20" => Periodic(missing: true).Render(2, 0),
            "D21" => Periodic(8, 256, missing: true).Render(4, 0),
            "I01" or "S02" => Base().Render(),
            "I04" => Base().Render(groupShifts: new Dictionary<string, (double, double)>
            {
                ["t1"] = (0.5, 0), ["t2"] = (0.25, 0.25), ["t3"] = (-0.5, 0), ["t4"] = (0.75, 0),
                ["t6"] = (0, 0.5), ["bar"] = (0, 0.5), ["bar2"] = (0.5, 0.25), ["table"] = (0.25, 0)
            }),
            "I05" => Base(amount: "999").Render(),
            "D01" => Base(dot: false).Render(),
            "D02" => Base(minus: false).Render(),
            "D03" => Base(amount: "125").Render(),
            "D06a" => Base(barEnd: 72).Render(),
            "D07" => Base(barColor: new(0, 60, 160)).Render(),
            "D08" => Base(dashed: true).Render(),
            "D09" => Base(cellFill: Scalar.All(242)).Render(),
            "D10" => Base(marker: true).Render(),
            "D11" => Base(frame: 5, amount: "125").Render(),
            "D12a" => Base(amount: "125", pair: "XZ").Render(),
            "D12b" => Base(second: "AXY").Render(),
            "D13" => Base(amount: "125").Render(0.5, 0.5),
            "D14" => Base(bar2Color: Scalar.All(110)).Render(),
            "D15" or "S01" => Base(frame: 3).Render(),
            "L01" => Base(barColor: new(190, 115, 70)).Render(),
            "L02" => Base().Render(3, 0),
            _ => throw new ArgumentException($"未定義の合成ケース: {id}")
        };
    }
}
