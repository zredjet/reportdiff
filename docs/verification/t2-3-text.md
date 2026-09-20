# T2-3 PDF テキスト注釈の検証

確認日：2026-09-21。承認済みの [具体仕様](../planning/t2-3-text.md)を実装。正本は [SPEC 11.3](../SPEC.md#113-pdf-テキスト層の注釈t2-3)。開始点は `f6b03a2`（T2-2）。

## 実装した動作

採用した差分クラスタの矩形と重なる PDF の単語全文を、A/B 別の `text_a` / `text_b` に添える。PdfPig 0.1.16 が補正済みの CropBox 原点を二重に引かず、各側の元の描画サイズに対応付ける。右・下の白埋めや移動量で文字位置を変えない。画像入力は null、PDF の抽出に成功して該当単語がなければ空文字列。

除外領域に重なる単語は全体を省き、幾何的な行順と単語順で表示する。同一文字列・同一矩形の重複を除く。回転・未対応の座標・上限超過・抽出失敗は A/B とページを含む警告にし、その側の注釈だけを省略する。本文は片側 2,000 Unicode スカラー値まで。画像の検出・分類・移動・終了コードは維持する。

HTML の A/B 切り出しの下に、エスケープした本文と注釈の状態を表示する。改行を保持し、長文は横に折り返し、キーボード操作できる縦スクロール領域にする。PDF 文字情報には不可視文字・未変更部分・不正確な情報があり得るため、画像確認の補助と説明する。OCR・テキストによる差分判定は行わない。

## ビルド・テスト

macOS 26.5.1 / Apple Silicon、.NET SDK 10.0.401 / Runtime 10.0.12。

- `dotnet build`：警告 0、エラー 0。
- `dotnet test`：**538 件成功、失敗・スキップ 0**。T2-2 の 504 件に 34 件追加。
- PDF テキスト 23 件：72/300/400dpi、日本語・絵文字、非ゼロ／負の原点、CropBox と MediaBox の交差、回転 90/180/270 度と継承、UserUnit、描画サイズ不一致、文字なし、抽出失敗、幾何的な順序、重なり・境界・重複・除外、制御文字・全角保持、破棄を検証。
- 上限は実 PDF でも検証：100,001 文字要素、20,001 単語は注釈を省略。2,001 文字の単語は打ち切る。Unicode の 1,999/2,000/2,001 文字の境界ではサロゲートペアを壊さない。
- 実 CLI 結合 8 件：正逆の `123` / `128`、PDF と PNG の混在・`--no-html`、回転、壊れた ToUnicode の抽出失敗、サイズ違い、単語の一部の除外を確認。警告付きでも画像比較の結果と終了コード 1 を維持し、Core のクラスタ・統計・分類・移動量・関連 ID と一致する。
- HTML 3 件追加：null・空文字列・日本語と改行の表示を確認。既存の攻撃的文字列の検証も text_a / text_b に拡張し、HTML / JSON のエスケープを確認。
- 既存 37 ゴールデン、最適化前後、分類・移動、従来の PDF 結合 12 件を維持。比較コアの式・既定値・期待値は変更していない。

PDF テストは文字の形と Unicode 対応を自作した Type3 フォントを PDF 内に持つため、OS のフォントや代替フォントに依存しない。実フォント・実帳票のあらゆる文字抽出品質を受け入れたものではない。

## ブラウザ

実 CLI のレポート `out/t2-3-report/report.html` を Chrome 153.0.8010.52 / Playwright 1.62.1 で file:// から確認。

幅 1280px / 390px で JSON と本文表示が一致し、日本語、`<script>`・`&` 等が文字として表示される。2,000 文字へ省略した長文と警告、改行・折り返し、長文のフォーカスと矢印キー、狭い画面での表の横スクロールを確認。狭幅ではテキスト列までスクロールした表示も撮影した。既存の A/B/重ね描き切り替え・画像・Tab/Enter/Space・JavaScript 無効時の表示を維持。ページエラー・CSP 違反・外部リクエストは 0。

スクリーンショットと機械検証結果は `out/t2-3-browser/`、`out/t2-3-browser-result.json`（Git 対象外）。

## 時間・メモリ

Release、合成 A4 595.276×841.89pt、300dpi、実描画 2480×3507px、600 単語。各条件 5 回、中央値。事前に各クラスタの文字列が期待どおりであることを検証し、測定前に GC を実行する。

| クラスタ数 | オープン・注釈・破棄 | 開いたドキュメントを再利用 | 前者のマネージド割当量 |
|---|---:|---:|---:|
| 1 | 18.69ms | 17.20ms | 3,968,792 bytes |
| 120 | 18.78ms | 18.45ms | 4,143,488 bytes |
| 500 | 21.22ms | 21.68ms | 4,707,312 bytes |

[全サンプル](t2-3-text-benchmark.json)。PDF **片側**のテキスト注釈で、比較コア・画像出力・PDF 描画・プロセス起動は計時外。再利用でも毎回ページを解析する。JIT 等を含む初回起動の保証値ではない。

`/usr/bin/time -l` のプロセス最大 RSS は **164,970,496 bytes（約 157.3MiB）**、peak memory footprint は 126,550,952 bytes。[記録](t2-3-text-memory.json)。合成 PDF の生成・描画・検証・GC も含む全プロセスのピークで、注釈単体の保持量やアプリ全体の最大値ではない。大規模な実 PDF の解析時間・メモリには一般化しない。件数上限はパーサー自体の強制制限ではない。

## 依存・Windows 配布物

正式 NuGet パッケージ [PdfPig 0.1.16](https://www.nuget.org/packages/PdfPig/0.1.16) を固定し、.NET 10 が選択した net9.0 アセットを利用。新しい推移的パッケージ・ネイティブ資産はない。Apache-2.0 原文と NOTICES、内蔵 Adobe Glyph List の通知、AFM の固有許諾と著作権表示、計 19 ファイルを追加し、出典とハッシュを台帳へ記録した。

Windows x64 / Release / 自己完結 / 単一ファイルの publish は成功。exe は **156,440,645 bytes（約 149.2MiB）**、SHA-256 は `14a6bf1957f0f632094fdd300b7c5d25ea576a9a6d2b60f4c532822d703baa9b`。バンドルは 192 エントリ、.NET Runtime は 10.0.12。[バンドル記録](t2-3-win-bundle.json)。

PdfPig の管理 DLL 7 件をバンドルから読み取り、NuGet の DLL と全 SHA-256 が一致。必須ネイティブ DLL 3 件の x64 と従来のハッシュを維持し、FFmpeg・他 OS の資産・テスト用依存は含まれない。管理 DLL が欠けた場合に検証が拒否することも確認した。

配布 ZIP は `out/dist/reportdiff-t2-3-win-x64.zip`。依存・ランタイム・通知ハッシュを照合し、ZIP の CRC と全ファイルの SHA-256 を確認した。梱包結果のサイズ・SHA-256 は `out/t2-3-package-result.json`、内包物は ZIP 内の manifest.json が示す。ZIP 自体のハッシュを同梱文書には埋め込まない。

## 再実行

```bash
dotnet build
dotnet test
dotnet build tools/ReportDiff.TextBenchmark -c Release
/usr/bin/time -l dotnet tools/ReportDiff.TextBenchmark/bin/Release/net10.0/ReportDiff.TextBenchmark.dll 5
dotnet tools/ReportDiff.TextBenchmark/bin/Release/net10.0/ReportDiff.TextBenchmark.dll --fixture out/t2-3-fixture
dotnet run --project src/ReportDiff.Cli -- compare 'out/t2-3-fixture/旧 文字.pdf' 'out/t2-3-fixture/新 文字.pdf' --out out/t2-3-report --force
# 相違の終了コード 1 が正常。Playwright が使える環境で続ける。
node tools/verify-html-report.mjs out/t2-3-report/report.html out/t2-3-browser
dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t2-3-publish
python3 tools/inspect-win-bundle.py out/t2-3-publish/reportdiff.exe
# ZIP が存在する場合は別のファイル名を指定する。
python3 tools/package-win.py out/t2-3-publish/reportdiff.exe out/dist/reportdiff-t2-3-win-x64.zip
```

## 残件と停止位置

Windows の完成版 exe の実行・追加 34 テスト・Edge/Chrome の表示は未確認。Mac のブラウザ結果・Windows バンドルの静的検証と区別して TASKS に残した。Intel Mac は対象外、実帳票の評価は T2-7。

T2-3 の実装・検証・1 ローカルコミットまでで停止。T2-4 以降、push、Release 公開には進まない。
