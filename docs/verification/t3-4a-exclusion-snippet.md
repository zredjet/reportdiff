# T3-4a 除外 YAML の取り出し

T3-4a 完了。確認日：2026-09-22、macOS Apple Silicon、.NET SDK 10.0.401 / Runtime 10.0.12、Chrome 153.0.8010.52。[実装計画](../planning/t3-4a-exclusion-snippet.md)に従い、HTML の各相違行に「除外 YAML」を追加した。

## 動作と互換性

各行の位置欄で詳細を開き、読み取り専用欄から一行をコピーする。「YAML を選択」はフォーカスと全選択の補助であり、クリップボードの権限を要求しない。JavaScript なしでも手動・キーボードで選択できる。設定の `exclude:` の下へインデントして貼り付け、同じ条件で再比較する。

`report.snippet_margin_mm` は既定 1mm、有限値 0〜20mm。四方へ余白を加え、0.5mm 単位で外側へ丸め、ページ境界でクリップする。切り出し画像の `crop_margin_mm` は従来どおり独立。全体補正時は A／補正後 B の座標を使う。対象のページ番号と空の `note` を出力する。

変更前 `e02a6fd` を別ディレクトリでビルドし、42 ゴールデンすべてを新旧 CLI に渡して、各ケースの保存済み設定と `--save-all-pages` で比較した。終了コード・JSON の既存項目・全 219 PNG が一致。除いた項目は実行日時と新規の `config.report.snippet_margin_mm` だけ。[照合記録](t3-4a-compatibility.json)を参照。比較コア・PNG・既存の設定値・`schema_version=1` を維持する。

## 追加の自動検証

専用 24 テストが成功した。

全体ビルドは警告 0・エラー 0。42 ゴールデンを含む C# 全 1,192 件が成功、失敗・スキップ 0（313.959 秒）。サンドボックスの `dotnet test` のローカル IPC 制約を避けるため、同じテスト DLL を直接起動した。

- 余白 0 / 1 / 20mm、0.5mm 単位の外向き丸め、フランス語カルチャで小数点がカンマにならないこと。
- 72 / 300 / 1200dpi、四隅・ページ端のクリップと、生成 YAML による端の全差分画素の除外。画素境界の検証には strict を用い、通常設定でページ端の小図形が位置ずれとして吸収される影響を分離する。
- 型・負数・上限超過・非有限値の拒否、既定値、切り出し余白との独立、設定の部分上書きと出力への伝搬。
- PDF / 画像の実効 DPI、全体補正後の座標、クラスタなしのページに欄を出さないこと。
- 実 CLI の画像比較で、余白だけを変えても JSON の既存値・全 PNG が一致し、HTML の YAML を貼ると終了コード 0・クラスタなしになること。
- 2 ページの合成 PDF で、2 ページ目からコピーした YAML がそのページだけを除外し、1 ページ目の差分と終了コード 1 を保持すること。
- 全体補正を適用した PDF の欄をコピーして再比較し、補正量 (−6, +4)px を保持したまま該当差分が消えること。

## ブラウザ

[専用検証スクリプト](../../tools/verify-exclusion-snippet.mjs)で 1280 / 390px、JavaScript 有効／無効の 4 条件を確認した。各 2 クラスタについて、Enter での展開、Tab でのフォーカス、キーボード全選択、選択ボタン、読み取り専用、セル内での表示、ページ全体の横はみ出しなしを検証。スクリーンショットも目視確認した。狭い画面の一覧は従来どおり横スクロールする。[機械可読記録](t3-4a-browser.json)を参照。

既存の HTML 検証でも、画像切り替え・設定表示・注釈・埋め込み JSON・キーボード・JavaScript なし・狭い画面を確認した。両スクリプトともページエラー 0、外部リクエスト 0。閉じた details の子要素に矩形が残る場合があるため、既存スクリプトの画面内判定は表示中の要素を対象にするよう修正した。

## A4 計測と配布確認

既存の合成 A4 ベンチマークを Release・300dpi・2,480 × 3,508px・600 文字、分類と移動注釈ありで実行した。各シナリオで結果の照合 2 回、計測 3 回の中央値は完全一致 5.150ms、1 か所変更 90.501ms、変更＋全体 1px ずれ 106.366ms。これは比較部分の時間で、PDF 読み込み・HTML / PNG 保存を含まない。今回、比較コアの変更や性能改善は行っていない。[計測 JSON](t3-4a-a4.json)を参照。

Windows x64 の Release・自己完結・単一 exe の publish と静的バンドル検査が成功。exe は 156,582,469 bytes、SHA-256 は `f3005e10c73d86d8139b174236cef3d2ce6b0e5f1c065f3b9b9c27056acc73e3`。.NET 10.0.12、既存依存、必須ネイティブ DLL 3 件、許諾文のハッシュを確認し、FFmpeg や他 OS のネイティブ資産を含まない。更新した設定例・文書を含む ZIP の作成、CRC とマニフェスト対象ファイルのハッシュ照合も成功した。

検査出力は `out/t3-4a-win-bundle.json`、最終 ZIP と梱包記録は `out/t3-4a-win-final.zip` / `out/t3-4a-win-package.json` に保存する。GitHub への push・リリース公開は行わない。

## 再実行

```bash
dotnet build --no-restore --disable-build-servers -m:1
dotnet tests/ReportDiff.Tests/bin/Debug/net10.0/ReportDiff.Tests.dll
dotnet src/ReportDiff.Cli/bin/Debug/net10.0/reportdiff.dll compare \
  reference/golden/D11_a.png reference/golden/D11_b.png --out out/t3-4a-rerun
# 比較の終了コード 1 は、この合成入力に差分があることを示す。
node tools/verify-exclusion-snippet.mjs out/t3-4a-rerun/report.html out/t3-4a-rerun-browser
node tools/verify-html-report.mjs out/t3-4a-rerun/report.html out/t3-4a-rerun-html
dotnet run --project tools/ReportDiff.Benchmark -c Release -- 3 --classification=on
dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t3-4a-win-rerun
python3 tools/package-win.py out/t3-4a-win-rerun/reportdiff.exe out/t3-4a-win-rerun.zip
```

ブラウザ検証は既存の Playwright と Chrome を使用する。必要なら `NODE_PATH` と `REPORTDIFF_BROWSER` を指定する。新たな製品依存はない。スクリーンショットと詳細ログは Git 管理外の `out/t3-4a-*` に保存する。

Windows の exe 実行・Edge / Chrome 操作は未確認で、[Windows 確認リスト](../TASKS.md#windows-確認リスト人が実機で行う)に残す。T2-8 の保留候補と T3-6 は本タスクの受け入れに含めない。
