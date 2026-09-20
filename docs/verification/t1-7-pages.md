# T1-7 正規化とページ対応の確認

確認日：2026-09-21、macOS / Apple Silicon、.NET SDK 10.0.401。

**T1-7 完了。** `dotnet build` は警告 0・エラー 0、`dotnet test` は既存 270 件と今回の 55 件の計 325 件が成功（失敗・スキップ 0）。

## API と動作

- `PageNormalizer.Normalize(a, b)` は BGR 8bit の 2 枚の `Mat` を受け取り、幅・高さそれぞれの最大値にそろえる。元の画素位置を維持し、右と下の不足分だけを白で埋める。
- 戻り値の `NormalizedPagePair` が独立した `A`・`B` を所有する。呼び出し側は `using` で破棄する。元画像は変更・破棄せず、同じサイズの場合も独立したコピーを返す。
- 元のサイズを `OriginalSizeA`・`OriginalSizeB` に保持し、サイズ差があれば `SizeMismatch = true` と `Warnings = ["SIZE_MISMATCH"]` を返す。白埋め後の画素比較が `same` でも、この情報は残る。結果出力側で `size_mismatch` と警告に反映する。
- `PageSelection.Parse(expression, pageCount)` は `1-2,5` のようなページ指定を解釈する。1 始まり、両端を含む範囲、カンマ区切り。区切りの周囲の空白は許容し、重複・重なりは除いて昇順にする。`null` は全ページ、空文字列はエラー。
- 構文不正、逆順の範囲、0 以下、整数の桁あふれ、上限超過は日本語の `PageSelectionException`。範囲の上限は展開前に調べる。
- `PagePairing.Create(pageCountA, pageCountB, pages)` は元のページ番号を保持した `PagePairingPlan` を返す。画像入力はページ数 1、PDF 入力は `PdfReader.PageCount` を渡す。
- `PagePair.CanCompare` が true のページだけを両側から読み込んで比較する。片側だけの場合は `HasA`・`HasB` と `UnpairedStatus`（`only_in_a` / `only_in_b`）で報告対象を判断する。欠けたページの白画像は作らない。
- ページ選択の上限は多い方のページ数とする。たとえば A が 2 ページ、B が 5 ページなら、`1-2,5` は共通の 1・2 ページと B だけの 5 ページを返す。6 ページはエラー。
- 元のページ数が異なる場合、計画に `PAGE_COUNT_MISMATCH` を保持する。共通ページだけを選択しても、入力のページ数が異なる事実は残す。

部分画像（ROI）の周囲を白埋めする際は、切り出し範囲外の画素を取り込まないように `BORDER_ISOLATED` を指定する。[OpenCV の copyMakeBorder](https://docs.opencv.org/4.13.0/d2/de8/group__core__array.html) の注意点を確認し、ROI 入力でも余白が白になるテストを入れた。

## テスト

| 確認項目 | 件数 |
|---|---:|
| 全ページ・範囲・単一指定・空白・重複・昇順 | 5 |
| 不正なページ指定の日本語エラー | 15 |
| 範囲外と巨大な不正範囲の拒否 | 4 |
| 最大整数の単一ページ指定 | 1 |
| 不正なページ数 | 2 |
| 同数・A が多い・B が多い場合のページ対応と警告 | 3 |
| 片側だけのページを含む選択、両側の上限超過 | 2 |
| 共通ページだけを選んだ場合のページ数警告 | 1 |
| 対応づけに渡すページ数の検証 | 3 |
| サイズ差の全方向、元画素の完全一致、白い余白、同サイズ | 6 |
| 正規化結果と入力の独立した所有権・破棄 | 2 |
| ROI の外側を取り込まない白埋め | 1 |
| 空・未対応画素形式・破棄済み入力の拒否 | 5 |
| PNG 読み込み → 正規化 → 比較、`same` の場合もサイズ警告保持 | 1 |
| PDF 読み込み → ページ対応 → 正規化 → 比較、片側ページの報告 | 4 |
| 合計 | 55 |

画像は合成の画素パターン、PDF は SkiaSharp で生成した図形だけを使う。PDF の確認は A/B の入れ替えとページ選択の有無を組み合わせ、元のページ番号・白埋め・`only_in_a` / `only_in_b` が保たれることを確かめる。

## 残る範囲

結果出力と形式の混在警告 `MIXED_INPUT_TYPES` は [T1-8 確認結果](t1-8-output.md) を参照。HTML は [T1-9 確認結果](t1-9-html.md) を参照。T1-10 の CLI 接続は未着手。`--pages` の値を解釈する API は実装済みだが、比較コマンド自体はまだ実行できない。Windows の追加テストは未実施で、[Windows 確認リスト](../TASKS.md) に残している。

再実行はリポジトリのルートで `dotnet build` と `dotnet test`。macOS の初回準備は [ネイティブ依存の準備](../NATIVE_RUNTIME.md) を参照。
