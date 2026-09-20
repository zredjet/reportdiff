# 作業リスト

上から順に進める。各タスクは受け入れ条件をすべて満たしたらチェックを付け、1 タスク 1 コミットにする。
仕様は `docs/SPEC.md`、ルールは `CLAUDE.md`。

## 人が行う準備（Claude Code の作業ではない）

- macOS に .NET 10 SDK を入れる。参照実装を動かすなら Python 3 と `pip install opencv-python-headless numpy pillow`
- このキットを空のディレクトリに展開し、`git init` する
- 実帳票のサンプルは `samples/private/` に置く（コミットしない）
- 確認用の Windows（x64）実機を用意する

---

## Phase 0：土台

### [x] T0-1 ソリューションの雛形

- `ReportDiff.sln` と 5 プロジェクト（CLAUDE.md の構成どおり）。参照の向きは Cli → Report → Pdf → Core、Tests → 全部
- `Directory.Build.props`：`net10.0`、`Nullable` 有効、`TreatWarningsAsErrors`、`LangVersion` latest、`InvariantGlobalization` は false
- `.gitignore`（`bin/`、`obj/`、`out/`、`samples/private/`、`__pycache__/`）、`.gitattributes`（`* text=auto eol=lf`、`*.png binary`、`*.pdf binary`）、`.editorconfig`、`THIRD_PARTY_NOTICES.md`（空の表）

受け入れ条件：`dotnet build` が警告ゼロ。`dotnet test` がテスト 0 件で成功。

確認済み（2026-09-20、macOS arm64、SDK 10.0.401）：`dotnet build --disable-build-servers -m:1` は警告 0・エラー 0、`dotnet test --disable-build-servers -m:1` は終了コード 0。テストケースとテストランナーの導入は T0-2 で行うため、この時点のテストは 0 件。実行環境の制約に合わせて並列ビルドとビルドサーバーを無効にした。

### [ ] T0-2 依存の疎通確認（最大のリスクなので最初に潰す）

- NuGet で実在と最新の安定版を確認してから追加：PDFtoImage、OpenCvSharp4、その Windows 用ランタイム、macOS 用ランタイム（Apple Silicon と Intel）。YamlDotNet、xUnit
- テスト 1：SkiaSharp の `SKDocument.CreatePdf` で、白地に黒い矩形と赤い線だけの 1 ページ PDF をメモリ上に作る → PDFtoImage で 300dpi にラスタライズ → `Mat`（BGR 8bit）に変換 → 大きさと、矩形の中心・余白の画素の色を検証
- テスト 2：OpenCvSharp で float32 の Lab 変換、`Blur`、`Erode` / `Dilate`、`ConnectedComponents`、`WarpAffine` が動くことを小さな配列で検証
- `dotnet publish -r win-x64`（CLAUDE.md のコマンド）が macOS 上で成功することを確認

受け入れ条件：macOS で 2 つのテストが成功。publish が成功し、出力サイズと含まれるネイティブライブラリ（pdfium、OpenCvSharpExtern、libSkiaSharp の Windows 版）を報告する。
止まる条件：macOS 用の OpenCvSharp ランタイムが解決できない、または publish に Windows 用ネイティブが入らない場合は、作業を止めて状況と選択肢を報告する。

**T0-2 が終わったら、結果を報告して一度止まる。**

### [ ] T0-3 CI（任意）

GitHub Actions で `windows-latest` と `macos-latest` の両方で `dotnet build` と `dotnet test`。リポジトリが GitHub にない場合は省略。

---

## Phase 1：MVP

### [ ] T1-1 単位と設定（SPEC 7 章）

- `Units`（mm↔px）、設定のレコード型、既定値、YAML の読み込み、未知のキーはエラー、値の範囲の検証、プロファイル（normal / strict / loose）
- 優先順位：既定値 < 設定ファイル < `--profile` < `--dpi`

受け入れ条件：既定値が SPEC 7.1 と一致するテスト。未知のキー・範囲外の値が日本語のメッセージ付きでエラーになるテスト。プロファイルと優先順位のテスト。

### [ ] T1-2 比較コア：候補の抽出と位置ずれの吸収（SPEC 5.1〜5.3）

- `reference/prototype.py` の `tolerant_diff` を素直に移植する。この時点では最適化しない（全ページで全ずれを計算してよい）
- 出力：生の差分マスク、`absorbed_groups`、`max_shift_px`

受け入れ条件：T1-4 の一致テストのうち、`raw_pixels`・`absorbed_groups`・`status` の一致。

### [ ] T1-3 比較コア：除外領域とクラスタ化（SPEC 5.4〜5.5）

受け入れ条件：T1-4 の一致テストのクラスタ数と矩形の一致。`too_different`、`CLUSTER_LIMIT`、`noise_dropped` の単体テスト。

### [ ] T1-4 ゴールデンテストと最適化（SPEC 9 章）

1. 一致テスト：`reference/golden/expected.json` の全ケース（37 件）を読み、各ケースの `params` で実行。`status` とクラスタ数が一致、矩形 ±1px、`raw_pixels` ±2%。`expect` が `limit` のケースも結果が一致すること
2. 生成テスト：参照実装の `Scene` を C# に移植し、I 系・D 系・S 系を生成して期待（無視／検出、クラスタ数、位置）を検証。J 系は PNG を使う
3. 最適化：SPEC 5.3 の「実装上の注意」のとおり、グループの外接矩形に切り出して評価する。A4・300dpi の合成ページ（文字 600 個、1 か所変更、全体 1px ずれ）で時間を測る簡単な計測手段を用意する

受け入れ条件：1 と 2 が全件成功。最適化の前後で 1 の結果が変わらない。計測結果（完全一致、1 か所変更、1 か所変更＋全体 1px ずれ）を報告する。目標は 2 秒以内、届かない場合は内訳を報告する。

### [ ] T1-5 画像の読み込み（SPEC 4.1、4.3）

受け入れ条件：PNG・JPEG・BMP・TIFF、アルファ付き、グレースケールを BGR 8bit にするテスト。先頭バイトでの形式判定のテスト。日本語と空白を含むパスで読めるテスト。

### [ ] T1-6 PDF の読み込み（SPEC 4.2）

受け入れ条件：SkiaSharp で作った 3 ページの PDF から、指定ページを指定 DPI で取り出せる。ページの大きさ（px）が「pt / 72 × dpi」と ±1px で一致。16000px を超えるとエラー。日本語パスで読める。

### [ ] T1-7 正規化とページの対応（SPEC 4.4、4.5、`--pages`）

受け入れ条件：大きさの違う 2 枚が余白埋めで同サイズになり `SIZE_MISMATCH`。ページ数が違うと `only_in_a` / `only_in_b` と `PAGE_COUNT_MISMATCH`。`--pages "1-2,5"` の解釈と範囲外のエラー。

### [ ] T1-8 出力：result.json、重ね描き、切り出し（SPEC 8.1、8.2）

受け入れ条件：スキーマどおりの JSON（テストで逆シリアライズして検証）。相対パスが `/` 区切り。差分のないページの画像は既定で保存されず、`--save-all-pages` で保存される。重ね描きに除外領域（黄色）とクラスタ番号が描かれる。枠の内側のクラスタにも輪郭が描かれる（`RETR_LIST`）。

### [ ] T1-9 HTML レポート（SPEC 8.3）

受け入れ条件：生成した HTML に `http://` と `https://` が含まれない（テストで検査）。A / B / 重ね描きの切り替えが動く。クラスタ一覧に番号、位置（mm）、画素数、3 つの切り出しが並ぶ。警告と吸収数が表示される。`<script type="application/json" id="result">` がある。

### [ ] T1-10 CLI（SPEC 6 章）

受け入れ条件：終了コード 0 / 1 / 2 のテスト。`--out` が空でないとエラー、`--force` で上書き。エラーは日本語で標準エラー出力。`--quiet`、`--no-html`、`--version`。`Console.OutputEncoding` が UTF-8。

### [ ] T1-11 結合テスト

SkiaSharp で 2 ページの PDF を新旧 2 つ作る（1 ページ目は同一、2 ページ目は 1 か所変更＋除外領域の中だけ別の変更）。CLI を実行する。

受け入れ条件：終了コード 1。`summary.clusters` が 1。1 ページ目は `same`。出力ファイルがそろっている。同一の PDF 同士では終了コード 0。

### [ ] T1-12 配布物

- `README.md`（使い方、設定例、プロファイルの選び方、既知の限界へのリンク）
- `THIRD_PARTY_NOTICES.md` を完成させる
- win-x64 の単一 exe を作る手順を確認

受け入れ条件：publish が成功。下の「Windows 確認リスト」を最新にする。

---

## Windows 確認リスト（人が実機で行う）

- [ ] .NET を入れていない PC で exe が単体で動く
- [ ] 日本語・空白を含むパス、長いパスで動く
- [ ] cmd と PowerShell で日本語のメッセージが化けない
- [ ] フォント埋め込み済みの同じ PDF で、macOS と同じ `result.json`（クラスタ数と位置）になる
- [ ] フォントが埋め込まれていない帳票での見た目と結果
- [ ] 実帳票（A4・300dpi）での処理時間
- [ ] SmartScreen とウイルス対策ソフトの反応
- [ ] Windows Server で動かす場合：Media Foundation 機能の有無
- [ ] `report.html` が Edge で `file://` から開ける

---

## Phase 2（着手時に詳細化。SPEC 11 章）

- [ ] T2-1 追加／削除／変更の判定と、切り出し diff の塗り分け
- [ ] T2-2 移動の判定（`matchTemplate`）
- [ ] T2-3 PDF テキスト層による注釈（PdfPig）
- [ ] T2-4 全体のずれの推定・補正・報告
- [ ] T2-5 フォント非埋め込みの警告
- [ ] T2-6 フォルダ一括比較と一覧レポート、帳票別設定の自動選択
- [ ] T2-7 実帳票での評価を受けたパラメータとゴールデンケースの見直し

## Phase 3（検討項目）

- [ ] T3-1 行の挿入に対する帯の整列
- [ ] T3-2 確認結果（確認済・仕様変更・要確認・不具合）の保存と再実行時の引き継ぎ
- [ ] T3-3 GUI（Avalonia）
