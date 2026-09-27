# README用の日本語PDF・行挿入削除サンプル

架空の「発注明細書」。A4縦・1ページ、文字層と日本語フォントを持つPDF。
レイアウト・企業名・住所・品目・数量は、このプロジェクトのデモ用に作成した。
既存の検証PDF、実帳票、外部帳票テンプレートは流用していない。

| ファイル | 内容 |
|---|---|
| [order-before.pdf](order-before.pdf) | 変更前。明細100にラベルプリンターを記載 |
| [order-after.pdf](order-after.pdf) | 明細035のバーコードスキャナーを追加し、明細100を削除 |
| [manifest.json](manifest.json) | 入力PDF・フォントのSHA-256、変更内容、フォント取得元 |
| [verification.json](verification.json) | 掲載画面のブラウザ確認と実際の比較結果の要約 |
| [前後図](../../images/sample-pdf-rows.png) | 入力PDFの描画に説明用ガイドを添えた図。製品画面ではない |
| [構造変化一覧](../../images/sample-pdf-row-changes.png) | v0.1.5の実際のHTMLレポートを撮影した画像 |

明細番号は発注内の固定IDとして扱い、挿入後も既存番号を振り直さない。
明細040〜090の6行は、内容を保って1行下へ移動する。
単価は年間契約に従う数量明細のため、金額・税額の再計算による別差分は含めていない。

## 比較結果と範囲

normal・300dpi・[rows.yaml](../../../examples/rows.yaml)で、行整列を有効、ページ送りを無効にした。
行挿入1・行削除1・ブロック移動1の合計3箇所、内容クラスタ0、内容比較面の生差分0。
挿入・削除対象の品名は、構造変化のPDFテキスト注釈でも確認している。
終了コード1は比較完了・相違ありを表す。

この例は、見出しと末尾に罫線を置き、明細本文の罫線を省いた一覧形式。
同じ明細の縦横罫線版・本文横罫線版もローカルで試作したが、どちらも
`ambiguous / non_equivalent_safe_cuts` で行整列を見送り、通常差分38件となった。
任意の日本語帳票・罫線表での採用を保証する例ではない。
[行整列の条件と制限](../../CONFIGURATION.md#pdfの行整列)を参照。

前後図の赤・緑の枠は説明用で、入力PDFには描いていない。
構造変化一覧は製品のHTMLをそのまま撮影し、検出結果を加工していない。
この合成例は、実帳票評価・Windows実機確認・ページ送りの検証とは別のもの。

## CLIで再現

リポジトリルートで[開発環境](../../NATIVE_RUNTIME.md)を用意して実行する。
保存先には未作成または空のフォルダを指定する。

```sh
dotnet run --project src/ReportDiff.Cli -c Release -- compare \
  docs/samples/readme-rows/order-before.pdf docs/samples/readme-rows/order-after.pdf \
  --out out/readme-rows-result --config examples/rows.yaml
```

`out/readme-rows-result/report.html` をブラウザで開く。
生成済みのレポート一式はこのサンプルフォルダに保存せず、入力PDFから再現する。

## 入力PDFの再生成

[生成コード](../../../tools/create-readme-row-sample.py)はPythonの `reportlab` と `pypdf` を使用する。
掲載時はReportLab 4.4.9・pypdf 6.10.0で作成した。これらは素材生成用で、製品の実行時依存には追加しない。

BIZ UDGothicの `BIZUDGothic-Regular.ttf`・`BIZUDGothic-Bold.ttf`・`OFL.txt` を
[Google Fontsの配布元](https://github.com/google/fonts/tree/main/ofl/bizudgothic)から取得し、例えば `out/readme-row-fonts/` に保存する。
生成コードは掲載時のSHA-256との一致を確認してからPDFを作る。フォントの完全なTTFファイルは本サンプルには同梱しない。

```sh
python3 tools/create-readme-row-sample.py out/readme-rows-input --fonts-dir out/readme-row-fonts
```

出力先は未作成のフォルダを指定する。生成日時を固定しており、同じ生成環境・フォントで入力PDFを再生成できる。

## README画像の再生成

Popplerの `pdftoppm`、Node.js、Playwright、Chromeを用意する。必要に応じて `NODE_PATH` を指定する。

```sh
mkdir -p out/readme-rows-render
pdftoppm -scale-to 1400 -png -singlefile docs/samples/readme-rows/order-before.pdf out/readme-rows-render/order-before
pdftoppm -scale-to 1400 -png -singlefile docs/samples/readme-rows/order-after.pdf out/readme-rows-render/order-after
node tools/capture-readme-rows.mjs out/readme-rows-render out/readme-rows-result out/readme-rows-screens
```

前後図は1440px、構造変化一覧は1280pxのビューポートから撮影する。
描画・表示環境によりスクリーンショットの字体やアンチエイリアスは変わり得る。
撮影時に行整列の採用・件数・対象品名、ページエラーと外部通信がないことを確認する。

## 出自とライセンス

自作データ・レイアウト・生成コードは、本リポジトリの [MIT License](../../../LICENSE)（Copyright (c) 2026 zredjet）で提供する。
PDFに埋め込むBIZ UDGothicのサブセットには、フォント固有の
[SIL Open Font License 1.1原文](BIZUDGothic-OFL.txt)が適用される。
Copyright 2022 The BIZ UDGothic Project Authors。
PDF・画面資料のフォントの出自を、自作データのMIT表記で置き換えない。
取得元と実ファイルのSHA-256は [manifest.json](manifest.json) に記録している。
