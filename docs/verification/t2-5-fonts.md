# T2-5 フォント非埋め込み警告の確認結果

確認日：2026-09-21。macOS Apple Silicon、.NET SDK 10.0.401 / Runtime 10.0.12。[採用した仕様](../planning/t2-5-fonts.md)と SPEC 11.5 節に従い、使用フォントの検査を追加した。

## 実装と判定の境界

- 選択された PDF の A/B 各ページで使用フォントを検査する。同一ページ、差分上限超過、片側だけのページ、PDF テキスト注釈の省略時にも警告を残す。画像入力と未選択ページは検査しない。
- 元のフォント辞書を参照し、非埋め込みを `NON_EMBEDDED_FONT`、元辞書不明・解析失敗・検査範囲外を `FONT_INSPECTION_INCOMPLETE` として日本語で報告する。入力・ページ・フォント名・参照・理由を既存の JSON / HTML の警告へ出す。
- Type0 の子フォント、Type1 / MMType1 / TrueType、Type3、サブセット、標準フォントを区別する。同名の別辞書をまとめず、同一辞書の別名や繰り返しはページ内で重複を防ぐ。
- 注釈・入力フォームの外観、リソース内のパターン、Type3 字形内の別フォント等については検査範囲の制限を明示する。図形だけの外観や未使用リソースにもこの「検査未完了」が出る場合がある。非埋め込みだと断定する警告とは区別する。
- フォントプログラムの所在を確認する機能であり、全字形の正しさ・許諾・OS 間の描画一致を保証しない。描画、比較コア、終了コード、設定値、依存と許諾構成は変更していない。
- テキスト注釈と現在の 1 ページの解析を共有する。新しい上限やしきい値は追加せず、既存の注釈件数上限でフォント警告の対象を切り捨てない。

## ビルドとテスト

`dotnet build` は **警告 0・エラー 0**。最終の `dotnet test` は **700 成功、失敗 0、スキップ 0**（約 82 秒）。T2-4a の 640 件に 60 件を追加し、既存 37 ゴールデンも維持した。

| 対象 | 確認内容 |
|---|---|
| フォント検査 46 件 | 標準フォント、Form XObject、継承リソース、未使用、空白・不可視文字、同名・別名、サブセット、直接辞書、埋め込みと欠落の混在、形式ごとのプログラム指定、Type0 の子辞書、不正・空・参照切れ・循環・未対応フィルタ、Type3 の別フォント、外観・パターンの制限、ページ解析失敗後の継続、注釈上限・回転、ファイルの破棄 |
| 実 CLI 14 件 | 相違なしでも A/B に警告、`--no-html`、相違・too_different と比較コアの一致、注釈上限時の警告、ページ選択と片側ページ、画像混在、複数ページ、外観だけのページ、同一 PDF の解析失敗、長いフォント名・HTML 特殊文字、循環する未使用 Form リソース |
| 既存テスト | ゴールデン、局所ずれ・分類・移動・全体補正、画像／PDF、PDF 注釈、設定・CLI・出力がすべて成功 |

フォントファイルや実帳票は追加していない。埋め込み済みの実例は、自作の三角形 1 字形を持つ TrueType と既存の自作 Type3。文字の取得と PDFium での描画も検証した。Type0 + CIDFontType2 からの同じ TrueType の使用も確認した。

Type1 / MMType1 / CFF 等の FontFile / FontFile3 を含む判定の一部は、合成辞書と短いストリームを使った所在・型・参照の検査である。それらを実用フォントの描画や全字形の検証が済んだ証拠とはしない。

初回のテスト追加時には、テストコードの API・設定キーの誤りを修正した。FontDescriptor が欠落した TrueType を PdfPig がページとして解析できないケースは、非埋め込みを断定せず検査未完了となる。同名の埋め込み／非埋め込み混在は、両方とも文字を取得できる合成 PDF で確認した。

追加レビューでは、未使用 Form のリソースが自己参照すると辞書を繰り返し取得するケースを発見した。PdfPig は同じ PDF 参照から新しい辞書インスタンスを返す場合があるため、オブジェクトの同一性だけでなく PDF の参照番号で訪問済みを判別するよう修正した。実 CLI のタイムアウト付き回帰テストを追加し、修正後に全 700 件を再実行した。

```bash
export DOTNET_CLI_HOME=/private/tmp/reportdiff-dotnet-cli
export NUGET_PACKAGES=/private/tmp/reportdiff-nuget/packages
export NUGET_HTTP_CACHE_PATH=/private/tmp/reportdiff-nuget/http-cache
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
dotnet build
dotnet test
```

## 実 CLI とブラウザ

長い名前に `<script>` と `&` を含む非埋め込みフォント、自作の埋め込みフォント、フォームの外観、図形の変更を持つ合成 PDF を日本語・空白パスから 144dpi で比較した。結果は終了コード 1、1 ページ・1 クラスタ。A/B それぞれに非埋め込みと検査未完了の計 4 警告が出た。

macOS Chrome **153.0.8010.52** の `file://` で 1280px / 390px を確認。警告の名前・理由・コード、折り返し、画像読み込み・切り替え、キーボード、設定欄、JavaScript 無効時の表示を検証した。スクリプトの実行、横のはみ出し、ページエラー、外部リクエストは 0。警告欄の撮影画像も目視し、長い名前やコードがカード内に収まることを確認した。

確認物は `out/t2-5/report/report.html`、`out/t2-5/browser/`、`out/t2-5/browser-result.json`。再確認は `node tools/verify-html-report.mjs <report.html> <画像保存先>`。同ツールに警告項目の幅と撮影を追加した。

## 時間とメモリ

自作 Type3 の A4・600 単語（3,600 文字要素）、300dpi 相当の座標を使用。初回を除く 9 回の中央値を記録した。ファイルはローカルの同じ合成 PDF を使った。

| 処理 | 中央値 | その処理中の managed allocation の中央値 |
|---|---:|---:|
| ファイルを開いてフォント検査 | 7.329ms | 2,248,872 bytes |
| ファイルを開いて 1 クラスタへテキスト注釈 | 20.740ms | 4,165,872 bytes |
| 注釈の解析結果を使った追加のフォント検査 | 0.212ms | 11,624 bytes |

生の結果は [計測 JSON](t2-5-measurement.json)。PDFium の描画・画像比較・ファイル出力はこの表に含まない。メモリは `GC.GetAllocatedBytesForCurrentThread` による確保量であり、保持量・プロセス全体のピークではない。この環境の .NET API ではピーク working set を取得できなかったため null とした。多数のページ・実帳票・Windows での値は未測定。

## Windows 配布物

`dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t2-5-publish` が成功。exe は **156,490,309 bytes**、SHA-256 は `51a4494ff6ada9e13ae23901ef0ccc94f0703bbfe72895077d2b055f3907711c`。

[バンドル検査](t2-5-win-bundle.json)で自己完結ランタイムと既存依存、3 種類の必須 x64 ネイティブ DLL の SHA-256、FFmpeg・他 OS のネイティブ資産の不在を確認した。依存追加・更新はない。

`tools/package-win.py` で配布 ZIP の CRC・全内容物の SHA-256・依存と原文通知を検証する。最終のドキュメントと5種類の YAML を含む出力は `out/dist/reportdiff-t2-5-win-x64.zip`。ZIP のサイズ・SHA-256 はローカルの `out/t2-5/package-result.json`、内容物の値は ZIP 内の manifest.json に記録する。

```bash
python3 tools/inspect-win-bundle.py out/t2-5-publish/reportdiff.exe
python3 tools/package-win.py out/t2-5-publish/reportdiff.exe out/dist/reportdiff-t2-5-win-x64.zip
```

Windows 実機での実行と Edge / Chrome の表示は未確認。[Windows 確認リスト](../TASKS.md#windows-確認リスト人が実機で行う)に追加 60 件・フォント警告・日本語パスと終了コードの確認を残した。Intel Mac は対象外。T2-5 のローカルコミットで停止し、T2-6・push・Release 公開へは進まない。
