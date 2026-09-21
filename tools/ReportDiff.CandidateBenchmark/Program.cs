using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var hardware = new { vector128 = Vector128.IsHardwareAccelerated, vector256 = Vector256.IsHardwareAccelerated,
    selected = CandidateSimd.Selected.ToString(), cpu_count = Environment.ProcessorCount };
if (args.Length == 0 || args[0] == "verify")
{
    var checks = 0; var fmaCases = 0;
    var random = new Random(20260921);
    foreach (var count in Enumerable.Range(0, 34).Concat(new[] { 127, 128, 129, 1025 }))
    foreach (var first in new[] { 0, 1, 2, 3, 7 })
    foreach (var edge in new[] { 0.0, 0.3, 0.6 })
    foreach (var threshold in new[] { 0.0, 3.1, 1000.0 })
    {
        var length = (count + first + 9) * 3;
        var a = Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 256 - 128)).ToArray();
        var b = a.Select(v => v + (float)(random.NextDouble() * 20 - 10)).ToArray();
        var ca = edge == 0 ? [] : Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 50)).ToArray();
        var cb = edge == 0 ? [] : Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 50)).ToArray();
        Check(a,b,ca,cb,new() { EdgeTolerance=edge,ColorThreshold=threshold },first,count);
    }
    // 3チャネル、ベクトルをまたぐ画素、各レーンでしきい値の直前・同値・直後を検査する。
    foreach (var edge in new[] { 0.0, 0.3, 0.6, 1.0 })
    foreach (var threshold in new[] { 0.0, 0.3, 3.1, 50.0 })
    foreach (var contrast in new[] { 0f, 0.1f, 7.3f, 255f })
    foreach (var channel in new[] { 0, 1, 2 })
    foreach (var sign in new[] { -1, 1 })
    {
        var a = new float[99]; var b = new float[99];
        var ca = edge == 0 ? [] : Enumerable.Range(0,99).Select(i=>i%2==0?contrast:0f).ToArray();
        var cb = edge == 0 ? [] : Enumerable.Range(0,99).Select(i=>i%2==0?0f:contrast).ToArray();
        var limit=(float)threshold+(float)edge*contrast;
        for(var p=0;p<33;p++) b[p*3+channel]=sign*(p%3==0 ? MathF.BitDecrement(limit) : p%3==1 ? limit : MathF.BitIncrement(limit));
        Check(a,b,ca,cb,new() { EdgeTolerance=edge,ColorThreshold=threshold },0,33);
    }
    // 丸めがFMAと異なる入力を選び、融合した演算に置き換わっていないことを検査する。
    for(var attempt=0;attempt<100000 && fmaCases<1000;attempt++)
    {
        var threshold=(float)(random.NextDouble()*10);
        var tolerance=(float)random.NextDouble();var contrast=(float)(random.NextDouble()*100);
        var limit=threshold+tolerance*contrast;
        if(limit==MathF.FusedMultiplyAdd(tolerance,contrast,threshold))continue;
        var a=new float[99];var b=new float[99];
        for(var p=0;p<33;p++) b[p*3+p%3]=p%2==0?limit:MathF.BitIncrement(limit);
        var ca=Enumerable.Repeat(contrast,99).ToArray();var cb=new float[99];
        Check(a,b,ca,cb,new() { EdgeTolerance=tolerance,ColorThreshold=threshold },0,33);fmaCases++;
    }
    if(fmaCases!=1000)throw new Exception("FMA境界ケースが不足");
    foreach(var width in new[] {1,73})
    foreach(var height in new[] {1,7,513})
    foreach(var edge in new[] {0.0,0.3})
    {
        var count=width*height;
        var a=Enumerable.Range(0,count*3).Select(_=>(float)(random.NextDouble()*100)).ToArray();
        var b=a.Select(v=>v+(float)(random.NextDouble()*15-7.5)).ToArray();
        var ca=edge==0?[]:Enumerable.Repeat(7.3f,count*3).ToArray();
        var cb=edge==0?[]:Enumerable.Repeat(2.1f,count*3).ToArray();
        var options=new DiffOptions { EdgeTolerance=edge };
        var expected=new byte[count];InitialCandidates.Fill(new(a,ca),new(b,cb),options,0,expected);
        foreach(var kernel in Enum.GetValues<CandidateKernel>())
        foreach(var degree in new[]{1,2,4,8})
        {
            var actual=new byte[count];
            CandidateRowSchedule.Create(width,height,new() {MaxDegreeOfParallelism=degree,MinimumParallelPixels=0}).Run((first,last)=>
                CandidateSimd.Fill(new(a,ca),new(b,cb),options,first*width,actual.AsSpan(first*width,(last-first)*width),kernel));
            if(!expected.AsSpan().SequenceEqual(actual))throw new Exception($"行区間が不一致 {width}x{height}/{degree}/{kernel}");checks++;
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { hardware,checks,fma_cases=fmaCases,exact=true },jsonOptions));
    void Check(float[] a,float[] b,float[] ca,float[] cb,DiffOptions options,int first,int count)
    {
        var beforeA=a.ToArray();var beforeB=b.ToArray();var beforeCa=ca.ToArray();var beforeCb=cb.ToArray();
        var expected=new byte[count];InitialCandidates.Fill(new(a,ca),new(b,cb),options,first,expected);
        foreach(var kernel in Enum.GetValues<CandidateKernel>())
        {
            var guarded=Enumerable.Repeat((byte)79,count+16).ToArray();guarded.AsSpan(7,count).Clear();
            CandidateSimd.Fill(new(a,ca),new(b,cb),options,first,guarded.AsSpan(7,count),kernel);
            if(!expected.AsSpan().SequenceEqual(guarded.AsSpan(7,count)))throw new Exception($"不一致 {first}/{count}/{options}/{kernel}");
            if(guarded.AsSpan(0,7).ContainsAnyExcept((byte)79)||guarded.AsSpan(count+7).ContainsAnyExcept((byte)79))throw new Exception("範囲外の書き込み");
            checks++;
        }
        if(!beforeA.AsSpan().SequenceEqual(a)||!beforeB.AsSpan().SequenceEqual(b)||!beforeCa.AsSpan().SequenceEqual(ca)||!beforeCb.AsSpan().SequenceEqual(cb))throw new Exception("入力変更");
    }
}
else if(args[0]=="measure")
{
    var records=new List<object>();
    foreach(var (width,height) in new[]{(32,32),(512,512),(1000,1000),(4960,3508),(6614,4677)})
    foreach(var edge in new[]{0.0,0.3})
    foreach(var dense in new[]{false,true})
    {
        using var a=new Mat(height,width,MatType.CV_8UC3,Scalar.All(255));
        using var b=a.Clone();
        if(dense)b.SetTo(Scalar.All(0));
        else Cv2.Rectangle(b,new Rect(width/3,height/3,Math.Max(1,width/10),Math.Max(1,height/10)),Scalar.All(0),-1);
        var p=new ComparisonParameters { Diff=new() { EdgeTolerance=edge,MaxShiftMm=0 } };
        using var fa=ComparisonFeatures.Create(a,p,false);using var fb=ComparisonFeatures.Create(b,p,false);
        var expected=new byte[width*height];InitialCandidates.Fill(fa.Read(),fb.Read(),p.Diff,0,expected);
        var variants=new[]{("scalar-serial",CandidateKernel.Scalar,1),("simd-serial",CandidateKernel.Auto,1),
            ("scalar-parallel",CandidateKernel.Scalar,4),("simd-parallel",CandidateKernel.Auto,4)};
        var samples=variants.ToDictionary(v=>v.Item1,_=>new List<double>());
        for(var iteration=0;iteration<18;iteration++)
        foreach(var (name,kernel,degree) in iteration%2==0?variants:variants.Reverse())
        {
            var started=Stopwatch.GetTimestamp();
            var schedule=CandidateRowSchedule.Create(width,height,new() { MaxDegreeOfParallelism=degree });
            var actual=new byte[width*height];
            schedule.Run((first,last)=>CandidateSimd.Fill(fa.Read(),fb.Read(),p.Diff,first*width,actual.AsSpan(first*width,(last-first)*width),kernel));
            var ms=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if(!expected.AsSpan().SequenceEqual(actual))throw new Exception("計測結果不一致");
            if(iteration>=3)samples[name].Add(ms);
        }
        var medians=samples.ToDictionary(k=>k.Key,k=>k.Value.Order().ElementAt(k.Value.Count/2));
        records.Add(new{width,height,edge,dense,mask_sha256=Convert.ToHexString(SHA256.HashData(expected)),median_ms=medians,samples_ms=samples});
        Console.Error.WriteLine($"{width}x{height} edge={edge} dense={dense}: "+string.Join(", ",medians.Select(k=>$"{k.Key}={k.Value:F4}")));
    }
    Console.WriteLine(JsonSerializer.Serialize(new{hardware,measurements=records,exact=true},jsonOptions));
}
else throw new ArgumentException("verify または measure を指定してください。");
