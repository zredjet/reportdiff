"""調査用の比較ビルドをoutに生成する。製品への採用・実装完了を意味しない。"""
from pathlib import Path
import shutil,sys
root=Path(__file__).resolve().parents[2]
base=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else root/'out/perf2g-pipeline/experiments'
if base.exists():raise SystemExit('コピー先は存在しないディレクトリを指定してください。')
if not base.is_relative_to(root/'out'):raise SystemExit('コピー先はリポジトリ内のout配下にしてください。')
for variant in ['png2','priority','combined']:
 dest=base/variant
 for name in ['Core','Pdf','Report','Cli']:
  source=root/f'src/ReportDiff.{name}';target=dest/f'src/ReportDiff.{name}';target.mkdir(parents=True)
  for p in source.iterdir():
   if p.is_file():shutil.copy2(p,target/p.name)
 def change(project,name,before,after):
  p=dest/f'src/ReportDiff.{project}/{name}.cs';s=p.read_text()
  if s.count(before)!=1:raise RuntimeError(f'試作パッチ対象が一意でありません: {name}')
  p.write_text(s.replace(before,after))
 if variant in ['png2','combined']:
  change('Report','ReportWriter','''                WritePng(paths.A!, images.A); WritePng(paths.B!, comparisonB);''','''                // A/Bの既存Matを共有して2つのPNGだけを並列化する試作。
                // 各書き込みが終了してから例外を再送出し、入力を先に解放しない。
                var failures = new Exception?[2];
                Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, side =>
                {
                    try { WritePng(side == 0 ? paths.A! : paths.B!, side == 0 ? images.A : comparisonB); }
                    catch (Exception ex) { failures[side] = ex; }
                });
                foreach (var failure in failures)
                    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();''')
 if variant in ['priority','combined']:
  change('Core','GroupRunIndex','''(long)offsets[count] * 3 * sizeof(int)''','''(long)offsets[count] * (3 * sizeof(int) + sizeof(long))''')
  change('Core','GroupRunIndex','''        return new(runs, offsets, initial, pixels);''','''        // 差分候補の密度が高い区間を先に評価する試作。ずれの順序・同点規則は維持する。
        // 全区間を残し、候補数が最良値に達した場合だけ既存の早期終了で打ち切る。
        // 同じ密度順位では元の区間順を維持し、キーの一時配列も予算に含める。
        var priority = new long[runs.Length];
        for (var i = 0; i < runs.Length; i++)
        {
            var run = runs[i]; var row = run.Y * width; var candidateCount = 0;
            for (var x = run.Left; x < run.Right; x++) if (candidates[row + x] != 0) candidateCount++;
            var density = (long)candidateCount * 1_000_000 / (run.Right - run.Left);
            priority[i] = ((1_000_000 - density) << 32) | (uint)i;
        }
        for (var group = 1; group < count; group++)
            Array.Sort(priority, runs, offsets[group], offsets[group + 1] - offsets[group]);
        return new(runs, offsets, initial, pixels);''')
 probe=dest/'Probe';probe.mkdir()
 shutil.copy2(root/'tools/ReportDiff.PipelineBenchmark/Program.cs',probe/'Program.cs')
 (probe/'Probe.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ReportDiff.Tests</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="../src/ReportDiff.Cli/ReportDiff.Cli.csproj" /></ItemGroup></Project>''')
 print(dest)
