# T1-6 PDF 読み込みの確認

確認日：2026-09-21、macOS / Apple Silicon、.NET SDK 10.0.401。

**T1-6 完了。** `dotnet build` は警告 0・エラー 0、`dotnet test` は既存 234 件と PDF 入力 36 件の計 270 件が成功（失敗・スキップ 0）。

## API と動作

- `using var reader = PdfReader.Open(path)` で .NET のファイルストリームを開き、先頭バイトで PDF と判定してページ数を取得する。拡張子を使わず、ネイティブにパスを渡さない。
- `reader.PageCount` がページ数。`reader.ReadPage(pageNumber, dpi)` で指定ページだけを BGR 8bit・3 チャンネルにする。ページ番号は 1 始まり、DPI は既定 300、範囲 72〜1200。
- 戻り値の `LoadedImage` は画素と DPI を持ち、呼び出し側が `using` で破棄する。画素は `SKBitmap` から独立しており、別ページを描画した後や `PdfReader` を閉じた後も使える。
- アンチエイリアス、注釈、フォームの描画を有効にし、背景を白にする。画素変換では `SKBitmap` のストライドを保持する。
- 選択したページの寸法（pt）を描画前に取得し、`pt / 72 × dpi` が 1 辺 16000px を超える場合は、画像確保前に DPI を下げるよう日本語で案内する。16000px ちょうどは許容する。選択していない巨大ページは描画しない。
- PDFium の呼び出しはクラス共通のロックで保護し、複数の `PdfReader` から呼んでも逐次実行する。内部の PDF ドキュメントは各ライブラリ呼び出しで閉じ、ファイルストリームは `PdfReader.Dispose` で閉じる。
- 形式違い、破損、読み込み失敗、ページ範囲外、DPI 範囲外は日本語の `PdfReadException` にする。

PDFtoImage 5.4.0 の [ストリームを用いた描画処理](https://github.com/sungaila/PDFtoImage/blob/v5.4.0/src/PDFtoImage/Conversion.cs)と [DPI・ビットマップ寸法の計算](https://github.com/sungaila/PDFtoImage/blob/v5.4.0/src/PDFtoImage/Internals/PdfDocument.cs)を確認した。依存の追加・バージョン変更はない。

## テスト

SkiaSharp で色とページ寸法の異なる 3 ページの PDF をメモリ上に生成し、日本語・空白を含む一時パスに書いて読み込む。拡張子は意図的に `.png` とし、内容で判定できることも確認する。実帳票や OS のフォントは使わない。

| 確認項目 | 件数 |
|---|---:|
| 3 ページ × 72 / 150 / 300 / 1200dpi、BGR 画素、白背景、半透明色、寸法誤差 ±1px | 12 |
| ページ順を変えた反復読み込み、既定 DPI、ファイル解放、画素の独立性 | 1 |
| 小数 pt の MediaBox を持つページの寸法誤差 ±1px | 3 |
| 横または縦の 16000px 超過と DPI を下げる案内 | 4 |
| 横または縦が 16000px ちょうどの描画 | 2 |
| 巨大ページ以外の選択と、DPI を下げた再読み込み | 1 |
| 注釈とフォームの描画（無効時に白になることも確認） | 1 |
| 斜線のアンチエイリアス | 1 |
| ページ範囲外 | 4 |
| DPI 範囲外 | 2 |
| 空・破損・PDF 以外の入力と失敗時のファイル解放 | 3 |
| 存在しないファイル | 1 |
| 複数の reader への並行要求で正しいページを取得 | 1 |
| 合計 | 36 |

SkiaSharp は生成時にページ寸法を整数 pt に丸めるため、小数 pt の寸法と注釈・フォームは最小限の PDF 構造を .NET で生成して検証する。画素の比較は基本的に完全一致、半透明の色だけは描画の丸めを考慮して ±1 とする。

## 残る範囲

サイズ・ページ対応と `--pages` の解釈は [T1-7 確認結果](t1-7-pages.md)、結果出力は [T1-8 確認結果](t1-8-output.md) を参照。HTML と CLI への接続は未着手。Windows の追加テストと PDF の日本語パス実機確認は未実施で、[Windows 確認リスト](../TASKS.md) に残している。

再実行はリポジトリのルートで `dotnet build` と `dotnet test`。macOS の初回準備は [ネイティブ依存の準備](../NATIVE_RUNTIME.md) を参照。
