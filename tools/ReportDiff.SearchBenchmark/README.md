# 位置ずれ探索の追加調査ツール

製品ソースのコピーで、候補ずれごとの実走査数・早期終了と改善案を比較する。製品からは参照せず、比較式・公開設定を変更しない。調査結果と採否は [PERF-2k](../../docs/verification/perf-2k-group-inner.md) を参照。

## 比較コピー

```sh
python3 tools/ReportDiff.SearchBenchmark/experiments.py out/search-trial
dotnet build out/search-trial/baseline/Probe/Probe.csproj -c Release
```

各構成の `Probe/Probe.csproj` を個別にビルドする。製品と同じ.NET・ローカルNuGetランタイムを使う。コピー先は存在しないリポジトリ内 `out/` 配下に限定する。既存の製品ソースとパッチ対象が一致しなければ停止する。コピーのハッシュは `source-manifest.json` に記録する。

| 構成 | 変更 |
|---|---|
| baseline | 現行製品のコピー |
| trace | 候補ごとのカウント上限、実走査数、最後の座標、訪問区間数、ページ端に触れる区間の画素数、経過時間を追加 |
| linear | 区間全体がページ内を参照する場合、横座標のClampを画素ループの外で判定 |
| inner2 / inner4 | 100万画素以上のグループを、候補ごとに最大2／4workerで分担 |
| inner4trace | inner4に実走査数と候補ごとの時間を追加 |

`inner`は候補ずれを従来順に逐次評価する。1候補の区間を約16,384画素以上の連続ブロックにまとめ、workerが動的に取得する。区間を途中で分割しないため、1区間が長いとブロックも長くなる。差分数をブロック末尾で合計し、最良値以上なら残りを止める。すでに実行中のブロックによる余分な走査はあり得る。最良値未満なら全ブロックの正確な総数、最良値以上なら不採用として扱う。同点の優先順位と最良値0での終了は維持する。

大グループの処理を終えてから、残りを既存の独立グループ並列で処理する。並列段階は重ねない。特徴量は読み取り専用で共有し、raw画像は最良ずれの確定後に逐次で書く。worker数分の画像複製は行わないが、大グループの区間配列とブロック一覧は追加する。`Parallel.For`終了後まで所有Matを保持する。

100万画素・16,384画素は試作の条件であり、製品採用済みの基準ではない。内部の `GroupSearchExecution` による並列度の指定への対応、失敗注入、製品用の選択条件は実装工程に残る。

## 実CLIと段階計測

```sh
python3 tools/ReportDiff.SearchBenchmark/measure.py out/search-trial \
  input-a.pdf input-b.pdf samples/private/search-cli-300 --dpi 300 --repeats 5
python3 tools/ReportDiff.SearchBenchmark/measure.py out/search-trial \
  input-a.pdf input-b.pdf samples/private/search-trace-300 --dpi 300 --repeats 3 \
  --probe --names baseline,trace,inner4trace
```

`measure.py`はmacOSの `/usr/bin/time -l` を使う。各構成を交互・逆順で実行し、最初の1回をウォームアップとして除外する。GCの強制実行はしない。通常CLIは起動からHTML保存・終了まで、probeは既存の [PipelineBenchmark](../ReportDiff.PipelineBenchmark/README.md) と同じ範囲を処理する。JSONの生成日時以外の比較内容と全PNGの相対パス・SHA-256を照合する。HTML生成は時間に含むが、HTMLのバイト一致は検証しない。入力には差異のあるペアを使う。

詳細イベントはprobeの `stages` 内の `detail.events` に入る。並列workerの経過時間の合計とプロセス全体時間を混同しない。カウンター追加による時間の変化があるため、採否の判断には `trace` の付かない構成の実CLIを使う。画素数は論理的な評価回数であり、CPUのキャッシュミスや実メモリ転送量の測定ではない。

出力先の上書きは拒否する。入力・PDF文字注釈・画像・標準出力ログは `samples/private/` などのGit管理外に置く。ビルド・別測定・テストと性能測定を同時実行しない。

## 合成画像と独立参照

```sh
dotnet build out/search-trial/inner4/Synthetic/Synthetic.csproj -c Release
dotnet out/search-trial/inner4/Synthetic/bin/Release/net10.0/ReportDiff.Tests.dll \
  out/search-inner4-synthetic.json
```

非連続ROI、画像端、1pxずれのみ、広範囲変更、ずれと局所変更、移動なしの局所変更、小画像をエッジ許容0／0.3で確認する。独立した全ページ・全候補の参照経路とのマスク・吸収数・最大ずれ一致と、親画像の不変を毎回検証する。各条件1回ウォームアップ後7回の段階時間も記録する。製品と同じfriend assembly名を使うため、他ツールや実テストとは出力ディレクトリを分ける。

コピー上の試作であり、Windows実機・低メモリ・長時間運用・例外注入を含む製品受け入れの完了には数えない。
