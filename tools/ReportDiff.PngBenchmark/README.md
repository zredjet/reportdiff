# PNG保存の逐次・並列比較

製品の `ReportWriter` を使い、小画像と100万画素の境界を計測する開発用ツール。入力と比較結果を一度だけ用意し、画像生成・保存の `AddComparedPage` を測る。PNG設定は製品と同じ。JSON/HTML生成、ハッシュ照合、ファイル削除は時間に含めない。PNG圧縮単体や実CLI全体の測定とは区別する。

```sh
dotnet build tools/ReportDiff.PngBenchmark -c Release
dotnet tools/ReportDiff.PngBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll \
  out/png-measure-images > out/png-measure.json
```

引数の保存先は存在しないディレクトリにする。生成した各回の出力は照合後に削除し、ルートは残す。製品と同じNuGet・ネイティブランタイムが必要。内部設定へアクセスするため、出力名はfriend assemblyの `ReportDiff.Tests`。実テストや他の同名ツールと出力ディレクトリを共有しない。

32×32、128×128、512×512、999×1000、1000×1000、1024×1024の6サイズと、白紙・罫線・ランダム画素の3パターン。逐次・サイズ制限なしの最大2並列・製品既定の自動選択を往復順で比較する。各構成3回ウォームアップ後15回、強制GCなし。CPU数が1の場合は全構成が逐次になる。全972回で、日時を固定したJSON/HTMLとPNGのファイル名・SHA-256を照合する。

同じプロセスと入力を繰り返す補助計測であり、プロセス初回の負荷や最大RSSは評価できない。採用判断にはA3の300/400dpi・連続ページ・小画像の独立したCLI計測を併用する。結果は [PERF-2h](../../docs/verification/perf-2h-png-parallel.md) を参照。
