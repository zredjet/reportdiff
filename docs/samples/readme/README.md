# README用の自作差異サンプル

架空の検査記録「DAILY INSPECTION REPORT」。レイアウト・文言・数値をこのプロジェクトのデモ用に作成した。実在の顧客情報、外部の帳票・写真・ロゴ・テンプレートは使っていない。`Sample Company (fictional)` も架空の表記。

自作の入力データ・生成コード・画面資料は、リポジトリの [MIT License](../../../LICENSE)（Copyright (c) 2026 zredjet）で提供する。文字描画は既存依存OpenCVの内蔵Hershey書体を使い、外部フォントファイルの取得・追加配布はしない。OpenCV等の依存は引き続き [第三者通知](../../../THIRD_PARTY_NOTICES.md) に従う。

| ファイル | 内容 |
|---|---|
| [inspection-a.png](inspection-a.png) | 基準画像。DocumentsのCOUNTは80、左下にDRAFT COPY |
| [inspection-b.png](inspection-b.png) | COUNTを85へ変更、DRAFT COPYを削除、右下にCHECKEDを追加 |
| [result/report.html](result/report.html) | v0.1.2の標準HTMLレポート。ダウンロード・ZIP展開後にブラウザで開く |
| [result/result.json](result/result.json) | 3箇所の相違と実効設定・入力ハッシュ |
| `result/pages/`・`result/crops/` | 標準のページ画像・差分・切り出し |

入力は1100×650pxのPNG。既定のnormal・300dpi（画像DPIも300）で比較し、変更1・削除（推定）1・追加（推定）1を検出する。削除の差分は緑、その他の差分は赤。番号は業務上の変更件数を保証するものではない。この例は画像入力なのでPDFテキスト注釈はない。

## 再生成

リポジトリルートで、[標準の.NET・ネイティブ依存](../../NATIVE_RUNTIME.md)を用意して実行する。既存の出力先は上書きしない。

```sh
dotnet run --project tools/ReportDiff.SampleGenerator -c Release -- out/readme-sample
```

[生成コード](https://github.com/zredjet/reportdiff/blob/v0.1.2/tools/ReportDiff.SampleGenerator/Program.cs)は実際の比較コアとReportWriter／HtmlReportWriterを呼ぶ。既定設定を変更せず、掲載用に生成日時を2026-09-21 12:00 JSTへ固定し、入力パスはファイル名だけにする。それ以外の比較結果・画像・HTMLは製品の出力。元データは毎回コードから描き、実帳票を参照しない。

CLIでも、入力画像を指定すれば同じ比較結果・PNGを生成できる。CLIの日時・入力パスは実際の実行値になる。

```sh
dotnet run --project src/ReportDiff.Cli -c Release -- compare \
  docs/samples/readme/inspection-a.png docs/samples/readme/inspection-b.png --out out/sample-cli
```

READMEの2枚は、実際のHTMLの重ね描き領域と差分一覧を撮影したもの。画素の描き足しや結果の加工は行わない。開発環境でPlaywrightとChromeを用意し、次を実行する（必要に応じて `NODE_PATH` を指定）。

```sh
node tools/capture-readme-sample.mjs out/readme-sample/result/report.html out/readme-screens
node tools/verify-html-report.mjs out/readme-sample/result/report.html out/readme-browser-check
```

この自作例の表示・再現確認は、未実施の実帳票評価T2-7やWindows実機確認の代わりにはしない。
