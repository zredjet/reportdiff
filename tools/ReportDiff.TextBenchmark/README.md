# PDFテキスト処理の評価ツール

製品とは別の実行ファイルで注釈処理を評価する。既存の`Program.cs`は合成600単語・1/120/500クラスタの単体測定と、`--fixture 出力先`による自作Type3フォントのPDF生成を行う。単体測定は強制GCを含むため、通常CLIの性能値と混同しない。

## A/Bの解析分担の調査

[PERF-2mの検証記録](../../docs/verification/perf-2m-pdf-text.md)に対応する。開始点は`fa28228`。製品の`src/`をコピーし、コピーだけへ計測と試作を適用する。出力先は未使用のリポジトリ内`out/`に限定し、パッチ対象が一意でなければ停止する。各コピーのソースのハッシュは`source-manifest.json`に保存する。

```sh
python3 tools/ReportDiff.TextBenchmark/experiments.py out/text-trial
dotnet build out/text-trial/baseline/Probe -c Release
dotnet build out/text-trial/pair2/Probe -c Release
dotnet build out/text-trial/trace/Probe -c Release
dotnet build out/text-trial/pair2trace/Probe -c Release
```

| 構成 | 内容 |
|---|---|
| baseline | 製品のコピー |
| pair2 | PDFごとに単語抽出器を持ち、A/Bの注釈とフォント検査をそれぞれ最大2並列にする試作 |
| trace | baselineにオープン・ページ解析・単語化・座標変換・対応付け・フォント検査の計測を追加 |
| pair2trace | pair2に同じ計測を追加 |

`pair2`は両入力がPDFの場合だけ分担し、注釈はクラスタのあるページだけ分担する。CPU数1・混合入力・片側ページは逐次。PDFiumは従来どおり逐次で、ページ間・画像比較・画像保存との並列実行は追加しない。各reader内のページ解析とフォント検査も逐次で、1ページのキャッシュを共有する。両workerが終了してからA/B順に出力し、想定外の例外は両側を回収後にA側を優先して再送出する。警告や注釈の省略条件は変えない。

```sh
python3 tools/ReportDiff.SearchBenchmark/measure.py out/text-trial \
  input-a.pdf input-b.pdf samples/private/text-cli --names baseline,pair2 --repeats 5
python3 tools/ReportDiff.SearchBenchmark/measure.py out/text-trial \
  input-a.pdf input-b.pdf samples/private/text-probe --probe \
  --names baseline,pair2,trace,pair2trace --repeats 3
```

同一PDFのCLIは`--expected-exit 0`、CPU数1は両構成へ`DOTNET_PROCESSOR_COUNT=1`を指定する。プロセスごとに測定するので最初の実行を除いてもプロセス内初回のJIT・共有データ初期化は含む。4/20ページの1ページ目と後続を分けて評価する。`trace`のworker内時間合計を並列段階の経過時間として扱わない。採否は計測を追加していない通常CLIの時間・最大RSS・JSON／PNG一致で判断する。ビルド・テスト・他測定と重ねず、強制GCは行わない。

## 合成PDFと失敗時の確認

```sh
dotnet build out/text-trial/baseline/Checks -c Release
dotnet build out/text-trial/pair2/Checks -c Release
dotnet out/text-trial/baseline/Checks/bin/Release/net10.0/ReportDiff.Tests.dll out/text-check-baseline
dotnet out/text-trial/pair2/Checks/bin/Release/net10.0/ReportDiff.Tests.dll out/text-check-pair2
```

`TrialChecks.cs`は既存の自作PDF生成コードを利用し、17条件×8反復で、A/Bの注釈・補正・除外・警告を逐次参照と照合する。各版の`checks.json`の`Id/repeat/result_sha256/input_sha256`も一致することを確認する。並列構成では、B側の終了を遅らせた両側例外の待機・送出順も検証する。失敗時には非0終了し、完了JSONを生成しない。

`FontConcurrencyProbe.cs`は別のコンソールプロジェクトへコピーし、製品Pdfを参照して使う補助調査。独立プロセス内のPdfPigのフォントキャッシュだけを各反復間でクリアし、同一／異なるフォントの初回同時検索を調べる。OSのフォントは読み取るだけで変更しない。非公開フィールドに依存するためPdfPig 0.1.16専用。引数は未使用のJSON出力先と、任意で2つのフォント名。既定の`Monaco`／`AppleSymbols`がない環境では、PdfPigから取得できる名前を指定する。速度測定と同時実行しない。

この調査の成功は、依存ライブラリ全体のスレッド安全性やWindows実機、低メモリ環境、製品への導入完了を保証しない。入力・注釈本文・レポート・生ログはGit管理外へ保存する。CLIのスキーマや公開設定は変更しない。
