# 行整列の最終受け入れ用計測

自作Type3フォントのA4合成PDFで、rows無効／有効の通常CLI時間・最大RSS・保存量を計測する。製品ソースをコピーし、処理段階の前後にタイマーだけを追加したCLIも別プロセスで測る。製品の比較式・設定・採用条件は変更しない。

完全一致、R02と同内容の行挿入＋数値変更、列の矛盾、改善不足での見送り、2／3種類の実効diffを持つ領域を使う。固定罫線・文字だけ送りのL03と、構造帯の全内容除外用PDFも出力する。元のR系テスト入力は変更しない。PDFのMediaBoxは210×297mmで、今回のPDFium描画結果は2480×3507px。合成データによる計測であり、実帳票の性能保証ではない。

```bash
dotnet restore tools/ReportDiff.RowBenchmark/ReportDiff.RowBenchmark.csproj \
  --source out/packages -p:RestoreFallbackFolders="$HOME/.nuget/packages" \
  -p:NuGetAudit=false --disable-parallel --disable-build-servers -m:1
dotnet build tools/ReportDiff.RowBenchmark/ReportDiff.RowBenchmark.csproj \
  --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.RowBenchmark/bin/Release/net10.0/ReportDiff.RowBenchmark.dll \
  out/row-benchmark/inputs
python3 tools/ReportDiff.RowBenchmark/instrument.py out/row-benchmark/instrumented
dotnet restore out/row-benchmark/instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj \
  --source out/packages -p:RestoreFallbackFolders="$HOME/.nuget/packages" \
  -p:NuGetAudit=false --disable-parallel --disable-build-servers -m:1
dotnet build out/row-benchmark/instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj \
  --no-restore -c Release --disable-build-servers -m:1
python3 tools/ReportDiff.RowBenchmark/measure.py out/row-benchmark/inputs \
  out/row-benchmark/measurements --instrumented \
  out/row-benchmark/instrumented/src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll
python3 tools/ReportDiff.RowBenchmark/verify.py out/row-benchmark/inputs \
  out/row-benchmark/cli-audit --measurements out/row-benchmark/measurements
```

新しい出力先を指定する。通常CLIのビルドを済ませ、計測中はテスト・ビルド・梱包等を同時に実行しない。計測対象は独立したCLIプロセスの起動、PDF描画・文字取得、比較、注釈、HTML／JSON／PNG／確認用オーバーレイの保存まで。条件ごと・バイナリごとに準備1回＋本計測3回を逐次実行し、条件順・バイナリ順を交互にする。OSキャッシュは消去しないため、コールド起動の保証ではない。

RSSはmacOSの `os.wait4` が返す子プロセス別 `ru_maxrss`（bytes）。先に終了した子プロセスの最大値を引き継ぐ `RUSAGE_CHILDREN` の累積値は使わない。`/usr/bin/time -l` がsandbox内でsysctlを拒否されたため、子プロセスの終了時統計を直接取得する。計測コピーの親子段階を二重加算しない。段階時間にはJIT・初回呼び出しの負荷も含まれる。

生成日時以外の全JSON／HTMLとPNGの全バイトを、同じ条件の通常CLI・計測コピー・全反復間で照合する。最初の実行も記録するが中央値には含めない。`verification.json` は全生データ・中央値・範囲・出力ハッシュを含む。計測が途中で失敗した場合は直前の反復までの `checkpoint.json` を残す。

24行の長い表は、今回の条件では内容比較面の生差分が増え、`low_improvement`で従来比較へ戻る。採用例へ差し替えて隠さず、R02相当の採用例とは別条件として計測する。L03も結果の記録にとどめ、検出必須の期待やしきい値を変更しない。

`verify.py` は両方向の内包領域・除外YAML貼り戻し・`--no-regions`、構造帯の全内容除外、支持まで除外した場合の見送り、L03を実CLIで確認する。`excluded-blocks` はCoreの支持・除外境界をPDF経路へ通すための自作図形と不可視の文字層であり、実帳票の代表例ではない。除外しない語の矩形が図形の縦方向全体を覆うようにしている。幅・高さの異なる不可視文字を置けば、文字と画像の再探索に一意性がなく見送りになる場合がある。
