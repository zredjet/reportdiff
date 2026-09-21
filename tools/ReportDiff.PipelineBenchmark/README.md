# パイプラインの内訳を調べる評価ツール

描画・比較・テキスト注釈・画像保存をページごとに計測する開発用ツール。製品からは参照しない。測定結果と試作の採否は [PERF-2g](../../docs/verification/perf-2g-pipeline-profile.md) に記録する。

## 通常の計測

リポジトリのルートから実行する。macOS Apple Silicon / Windows が対象で、製品と同じネイティブランタイムとNuGetの復元が必要。

```sh
dotnet build tools/ReportDiff.PipelineBenchmark -c Release
dotnet tools/ReportDiff.PipelineBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll \
  input-a.pdf input-b.pdf samples/private/pipeline-run 300 > out/pipeline.json
```

引数は `入力A 入力B 空の出力先 [dpi=300] [最大ページ数]`。PDFと画像を製品の `ComparisonInput` で読み、既定設定・全ページ画像保存で比較する。最大ページ数は先頭からの計測範囲を制限する開発用引数で、製品のページ選択オプションとは異なる。CLIの全オプションや出力の置換処理を再現するツールではない。成功時の終了コードは相違の有無によらず0。

既存の内部APIへアクセスするためfriend assembly名 `ReportDiff.Tests` を使う。実テストや他の同名DLLと出力先を混在させない。ソリューションの製品ビルド・テストとは別にビルドする。

標準出力のJSONは、各ページの上位段階 `stage_ms`、比較コアの `timings`、マネージド割り当て・GC情報を含む。`PreparationMs` 等は `compare` の内数、`GroupIndexMs` は `ShiftsMs` の内数なので重複して合計しない。`elapsed_ms` は内部計測であり、プロセス起動・終了・標準出力のJSON化は含まない。保存までの全体比較では別途製品CLIを実行し、プロセスの経過時間と最大RSSを測る。GCヒープはネイティブMat等を含むRSSではない。

入力のパス・文字注釈・画像・レポートを扱うため、出力は `samples/private/` 等のGit管理外に置く。既存出力先の上書きには使用しない。

## 詳細計測と比較用コピー

以下のPythonスクリプトは標準ライブラリだけを使用する。現在の製品ソースを `out/` 内にコピーして差分を適用し、製品ソースを編集しない。コピー先が既存・`out/` 外の場合は拒否し、差分の適用箇所が一意でない場合も停止する。途中失敗したコピーは未完成なので使用しない。製品の変更後に無条件に使える汎用パッチではない。

```sh
python3 tools/ReportDiff.PipelineBenchmark/instrument.py out/pipeline-detail
dotnet build out/pipeline-detail/Probe/Probe.csproj -c Release
dotnet out/pipeline-detail/Probe/bin/Release/net10.0/ReportDiff.Tests.dll \
  input-a.pdf input-b.pdf samples/private/pipeline-detail-run 300 > out/detail.json

python3 tools/ReportDiff.PipelineBenchmark/experiments.py out/pipeline-experiments
dotnet build out/pipeline-experiments/png2/Probe/Probe.csproj -c Release
dotnet out/pipeline-experiments/png2/Probe/bin/Release/net10.0/reportdiff.dll \
  compare input-a.pdf input-b.pdf --out samples/private/pipeline-png2-run --dpi 300 --save-all-pages
```

ビルド時にローカルNuGetキャッシュ等の環境変数が必要な環境では製品と同じ指定を使う。性能測定はビルド完了後、別の測定・ビルド・テストと重ねずに行う。

`instrument.py` はPNG圧縮／書き込み、重ね描き／差分切り出し、PdfPigオープン／ページ解析／単語化／対応付け、特徴量worker内の演算別時間、グループ別探索時間を記録する。ページ単位の `detail.events` に入り、毎画素のイベントは生成しない。**並列workerの時間の合計は経過時間ではない**。グループの最長時間やイベントの開始・終了範囲と合わせて読む。特徴量の演算時間もworker内の合計なので、CPU時間の測定ではない。計測の追加負荷は通常版との比較で評価する。

`experiments.py` は次の3構成を生成する。各構成の `Probe/Probe.csproj` を個別にビルドできる。

| 構成 | 試作する変更 |
|---|---|
| `png2` | ページA/BのPNG圧縮・書き込みだけを最大2並列にする。全worker終了後にA/B順で例外を再送出する |
| `priority` | 初期候補の密度が高い走査区間を先に評価する。候補ずれの順序・同点規則・全画素の対象範囲は維持する |
| `combined` | 上の2変更を組み合わせる |

これは採否を判断する試作であり、製品実装の完了を意味しない。`priority` はPERF-2gの測定で遅くなったため不採用。`png2` の小画像・CPU数による逐次復帰、例外注入、出力置換、Windows等の受け入れは次の実装タスクに残す。製品のCLI・既定値・出力形式はこのツールでは変更しない。

PERF-2hで製品へPNG並列化を導入したため、PERF-2g当時の試作を再生成する場合は `34d9ec9` のチェックアウトを使う。後続の製品ソースに `experiments.py` を無理に適用せず、一意性チェックの拒否を維持する。現在のPNG保存の逐次／並列比較には [PngBenchmark](../ReportDiff.PngBenchmark/README.md) を使う。
