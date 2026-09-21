# ReportDiff

業務帳票の PDF・画像を比較し、相違を「箇所」単位で報告する CLI ツール。細かな描画差や許容範囲内の位置ずれを吸収し、変更部分の画像と JSON・オフライン HTML を出力する。

実行対象は Windows x64。開発・検証環境は macOS Apple Silicon。Intel Mac は対象外。Phase 1 と T2-1〜T2-6（分類・移動・PDF テキスト注釈・任意の全体補正・YAML 設定・フォント警告・フォルダ比較）を実装済み。配布物は [GitHub Releases](https://github.com/zredjet/reportdiff/releases) から取得できる。実帳票による評価（T2-7）はサンプル未準備のためスキップしている。ユーザー実機での条件は [確認リスト](docs/TASKS.md#windows-確認リスト人が実機で行う)で管理する。

## Windows で使う

配布 ZIP を展開し、そのフォルダで PowerShell を開く。自己完結の `reportdiff.exe` に .NET ランタイムを含めているため、.NET を別途インストールする構成ではない。初回起動時はネイティブ DLL を一時領域に展開する。[動作条件と配布手順](docs/DISTRIBUTION.md)も参照。

```powershell
.\reportdiff.exe --version
.\reportdiff.exe compare "帳票 旧.pdf" "帳票 新.pdf" --out "比較結果"
$LASTEXITCODE
Start-Process ".\比較結果\report.html"
```

cmd でも `reportdiff.exe compare "帳票 旧.pdf" "帳票 新.pdf" --out "比較結果"` の形で実行できる。終了コードは、次の行で `echo %ERRORLEVEL%` を実行して確認する。

| 終了コード | 意味 |
|---|---|
| `0` | 相違なし |
| `1` | 相違あり。ページ数不一致、差分過多も含む |
| `2` | 引数・設定・読み込み・保存などのエラー。標準エラー出力を確認する |

比較結果は標準出力に日本語で 1 行表示する。`1` は比較が完了して相違が見つかったことを表す。

### 入力と出力

入力は PDF、PNG、JPEG、BMP、TIFF。拡張子だけでなくファイルの先頭バイトで形式を判定する。画像は 1 ページとして扱い、複数ページ TIFF は先頭ページだけを読む。PDF はページ順で対応付け、大きさが違う場合は左上を合わせて右・下を白で埋め、警告を記録する。画像の拡大縮小は行わない。

```text
比較結果/
  result.json                     # 判定・クラスタ・設定・吸収・除外・警告
  report.html                     # 外部通信不要のレポート
  pages/p002_a.png                 # A（基準）
  pages/p002_b.png                 # B（比較対象）
  pages/p002_overlay.png           # 差分・輪郭・番号は赤、除外領域は黄色
  crops/p002_c001_a.png            # クラスタ 1 の切り出し
  crops/p002_c001_b.png
  crops/p002_c001_diff.png
```

HTML では A / B / 重ね描きを切り替え、変更位置（mm）・分類・切り出しを確認できる。レポートを移動するときは出力フォルダごとコピーする。相違のないページの PNG は既定で保存しない。ただし全体補正を適用したページは、補正前後の証拠画像を保存する。

分類は A→B のインクの有無に基づく「追加（推定）」「削除（推定）」「色変更（推定）」「変更」。切り出しの緑は A だけのインク、赤はその他の差分を示す。背景や塗り、形状が明確でない差は「変更」に残すため、業務上の意味や文字の内容を断定する分類ではない。JSON の `kind` にも記録する。[分類の仕様](docs/SPEC.md#111-追加削除変更の分類t2-1)を参照。

近くへの移動を一意に確認できた箇所には「移動（推定）」を付け、A→B の方向・px 数・関連箇所へのリンクを表示する。移動元と移動先が別クラスタでも、番号・検出数はそのまま残る。コピーや反復、移動元に別の変更が残る場合などは、元の分類を維持する。移動も相違として扱う。

```yaml
move:
  search_mm: 5.0       # 各軸の探索距離（0〜20mm）。0 で移動の注釈を無効化
  min_score: 0.98
  min_score_gap: 0.02
```

JSON では `kind: moved`、`shift_px: {dx, dy}`（右・下が正）、`related_cluster_ids` に記録する。クラスタ数は移動操作の件数ではない。サブピクセル・回転・拡縮、複合箇所の一部だけの移動は対象外。画像端や単純な長い線、薄い塗りも推定されないことがある。[移動の仕様](docs/SPEC.md#112-移動の注釈t2-2)を参照。

PDF 入力には、差分矩形に重なる単語全文を A/B 別の「PDF テキスト」として添える。JSON の `text_a` / `text_b` にも記録する。OCR ではなく元 PDF の文字情報なので、不可視の文字や変更されていない部分を含むことがある。画像と併せて確認する。

除外領域に触れる単語は省き、本文は既定で片側 2,000 文字まで（text 設定で調整可能）。回転ページ・未対応の座標・抽出失敗は警告とともにその側の注釈だけを省略し、画像比較を続ける。「該当テキストなし」は抽出成功で対象の単語がない場合、「テキスト注釈なし」は画像入力・省略・失敗の場合を示す。詳しくは [テキスト注釈の仕様](docs/SPEC.md#113-pdf-テキスト層の注釈t2-3)を参照。

### 全体の位置補正

帳票全体の位置ずれを許容する場合は、次の YAML で全体補正を明示的に有効にする。既定では無効。候補が曖昧、情報が少ない、ページサイズが違う、補正で端の内容が切れる場合は元の画像で比較し、理由をレポートに示す。

```yaml
align:
  enabled: true         # 全体移動を許容する場合だけ有効化
  max_shift_mm: 5.0     # 上下左右それぞれの探索上限（0〜20mm）
  min_score: 0.98       # 一致度の下限
  min_score_gap: 0.02   # 次点候補との差の下限
  min_improvement: 0.05 # 補正なしからの改善量の下限
```

補正は B→A の方向で `global_shift_px` と `GLOBAL_SHIFT_APPLIED` に記録する。補正で相違なしになった場合も、HTML で「B · 補正前」と「B · 補正後」を切り替えられる。差分・除外・PDF テキストの座標は A／補正後 B にそろい、クラスタの移動量は補正後に残った A→B の移動となる。意図的な帳票全体の移動と出力環境のずれは画像から区別できず、有効化により判定や終了コードが変わる。[全体補正の仕様](docs/SPEC.md#114-全体のずれの推定補正t2-4)を参照。

### オプション

```powershell
.\reportdiff.exe compare old.pdf new.pdf --out result --config examples/settings.yaml --profile strict --dpi 300 --pages "1-2,5"
```

| オプション | 動作 |
|---|---|
| `--out dir` | 必須。出力先が未作成または空の場合に保存 |
| `--config file.yaml` | YAML 設定を読み込む |
| `--profile normal/strict/loose` | 位置ずれ・エッジ許容のプロファイルを適用 |
| `--dpi n` | PDF の描画 DPI（72〜1200）を指定 |
| `--pages "1-2,5"` | 1 始まりのページ番号を選択。省略時は全ページ |
| `--save-all-pages` | 相違なしのページ画像も保存 |
| `--no-html` | JSON と画像だけを保存 |
| `--quiet` | 標準出力の要約を抑止。エラーは表示 |
| `--force` | 指定した出力フォルダ全体を置き換える |
| `--version` | バージョンを表示 |

`--force` は出力フォルダ内の他のファイルも置き換えるため、結果専用のフォルダを指定する。単一ファイル比較の処理中の失敗では旧結果を保持するが、電源断やプロセスの強制終了からの自動復旧機能はない。入力・設定ファイル、作業ディレクトリ、実行ファイルの保存先を含む出力先は指定できない。

## フォルダを一括比較する

```powershell
.\reportdiff.exe compare-dir old new --out batch-result --config examples/settings.yaml --rules examples/batch/rules.yaml
```

サブフォルダ・隠しフォルダも探索し、PDF / PNG / JPEG / BMP / TIFF を、NFC 正規化・大小文字を区別しない相対パスで対応させる。片側だけのファイルは存在の差分として一覧に表示し、内容は検査しない。その他の拡張子は対象外一覧へ記録する。名前の衝突、リンク・ジャンクション、入力どうしの包含、入力と出力の重なりは開始前エラーになる。

`batch-result/index.html` が全体の一覧、`index.json` が機械処理用の集計。成功したファイル対は `files/f000001/` 等に従来と同じ個別レポートを保存する。`--no-html` は一覧・個別の両方に適用する。`--pages` も各ファイル対へ適用し、範囲外はその対のエラーとなる。

`--rules` は `compare-dir` 専用。[コメント付きの例](examples/batch/rules.yaml)から、帳票の相対パスの正規表現と設定 YAML を指定する。優先順位は **既定値 → 共通 --config → 一致した設定の指定キー → 明示 --profile → CLI**。複数規則に一致した対はエラーにし、ほかの対は続ける。[設定の重ね方](docs/CONFIGURATION.md#フォルダ比較の帳票別設定)を参照。

終了コードは、全一致が **0**、相違または片側のみが **1**、個別エラーまたは対象 0 件が **2**。破損入力等の個別エラーは一覧を保存して終了する。この場合、**`--force` はエラーを含む新しい一覧で旧結果全体を置き換える**。設定不正・列挙不能・出力障害では旧結果を保持する。共通設定・選択定義・すべての参照設定を含む出力先は指定できない。処理中は入力を更新しない。詳細は [フォルダ比較仕様](docs/SPEC.md#116-フォルダ一括比較t2-6)を参照。

## 設定とプロファイル

[設定リファレンス](docs/CONFIGURATION.md)に全キーの既定値・範囲・影響をまとめている。インク判定、読み順、移動の余白、PDF 注釈の上限、全体補正の探索・支持条件も同じ YAML で調整できる。[全項目の設定例](examples/settings.yaml)と[最小例](examples/minimal.yaml)、[厳密比較](examples/strict.yaml)、[JPEG／スキャン](examples/scan.yaml)、[全体補正](examples/align.yaml)を同梱する。入力 YAML は書き戻さず、コメントを保持する。

まずは設定なしの `normal`・300dpi で比較する。コピーして編集できる [設定例](examples/settings.yaml)は既定値と同じで、除外領域は空になっている。

外部定義には `#` のコメントを記載できる UTF-8 の YAML を使う。承認済みの調整パラメータを網羅し、残る固定値の設定化と説明付き設定例を整える作業は [T2-4a](docs/planning/t2-4a-configuration.md) として管理している。

日時などを除外する例：

```yaml
dpi: 300
exclude:
  - {page: all, x: 150, y: 8, w: 50, h: 6, note: 出力日時}
report:
  crop_margin_mm: 2.0
```

長さはすべて mm、座標はページ左上が原点。`page` は `all` または 1 始まりの番号。未知のキーや範囲外の値はエラーになる。実際に使った設定と除外領域は JSON・HTML で確認できる。

設定の優先順位は **既定値 → YAML → 明示した `--profile` → `--dpi`**。プロファイルが上書きするのは `diff.max_shift_mm` と `diff.edge_tolerance` で、`move`・`align` は変えない。`image_dpi` を省略すると最終的な `dpi` に追従する。画像入力では埋め込み DPI を使わず `image_dpi` で mm 換算するため、画像を作った解像度に合わせる。PDF と画像を混在させる場合は `dpi` と `image_dpi` をそろえる。不一致はエラーになる。

| プロファイル | 位置ずれの許容 | エッジの許容係数 | 選ぶ場面 |
|---|---:|---:|---|
| `normal`（既定） | 0.15mm | 0.3 | 通常の帳票比較 |
| `strict` | 0mm | 0 | 同じエンジン・同じ環境の回帰確認。微小な描画差も検出したい場合 |
| `loose` | 0.30mm | 0.3 | 現新比較など、位置の揺れが大きい場合 |

`strict` でも色のしきい値や小さなノイズの除去は残るため、ファイルのバイト単位の一致検査ではない。JPEG・スキャン画像では `diff.color_threshold` を 8〜12 に上げる設定を試せるが、小さな色差を見逃しやすくなる。設定の全項目は [SPEC 7 章](docs/SPEC.md#7-設定ファイル)を参照。

## 既知の限界

小さい文字が多い帳票は 400dpi を検討する。200dpi 以下では「未／末」「ば／ぱ」などを安定して検出できない。細い線の微妙な濃淡差や 1px 以内の長さの変化は通常設定で吸収される場合がある。行の挿入で下がずれると、それ以降がまとめて差分になる。[既知の限界の一覧](docs/SPEC.md#56-既知の限界仕様)を確認する。

フォントを埋め込んでいない PDF は OS の代替フォントにより描画が変わる。使用フォントの非埋め込みは `NON_EMBEDDED_FONT`、解析失敗・注釈や入力フォームの外観などの検査未完了は `FONT_INSPECTION_INCOMPLETE` として JSON / HTML に理由を表示する。警告だけでは比較結果と終了コードは変わらない。A と B は同じマシンの同じ実行で比較する。詳細は [フォント検査](docs/SPEC.md#115-フォント非埋め込みの警告t2-5)を参照。PDF のパスワード指定、文字認識は現在の対象外。[入力仕様](docs/SPEC.md#4-入力の正規化)と [今後の範囲](docs/SPEC.md#11-phase-2-以降の仕様の概要)を参照。

## ソースから実行・ビルドする

.NET 10 SDK を用意する。macOS Apple Silicon は初回に FFmpeg なしのローカルランタイムを生成する。詳細は [ネイティブ依存の準備](docs/NATIVE_RUNTIME.md)。

```bash
# macOS の初回だけ
python3 tools/build-macos-runtime.py

dotnet build
dotnet test
dotnet run --project src/ReportDiff.Cli -- compare old.pdf new.pdf --out out/result
```

Windows では macOS ランタイムの生成は不要。復元前にローカル NuGet ソースの空フォルダ `out/packages` を作る。Windows 用の自己完結 exe と配布 ZIP の生成手順は [配布手順](docs/DISTRIBUTION.md)を参照。

比較コアの計測は `dotnet run --project tools/ReportDiff.Benchmark -c Release -- 3`。37 件の参照結果との一致や合成 A4・300dpi の計測結果は [T1-4 の検証記録](docs/verification/t1-4-core.md)に記載している。

## 開発資料と確認状況

- [T2-6 の検証記録](docs/verification/t2-6-directory.md)：フォルダ比較・設定選択・一覧・時間とメモリ・Windows 配布物
- [SPEC](docs/SPEC.md)：アルゴリズム・設定・入出力の正本
- [TASKS](docs/TASKS.md)：完了範囲と Windows 実機確認リスト
- [T2-4 の検証記録](docs/verification/t2-4-alignment.md)：全体補正・元画像の保存・PDF 座標・性能・Windows 配布物
- [T2-4a の計画](docs/planning/t2-4a-configuration.md)：コメント付き YAML による承認済みパラメータの外部設定整備
- [T2-3 の検証記録](docs/verification/t2-3-text.md)：PDF テキスト・座標・失敗時の継続・長文表示・性能・Windows 配布物
- [T2-2 の検証記録](docs/verification/t2-2-movement.md)：移動量・関連 ID・誤判定防止・PDF 結合・多数候補の計測
- [T2-1 の検証記録](docs/verification/t2-1-classification.md)：分類・切り出し・PDF 結合・ブラウザ・時間とメモリ
- [T1-12 の検証記録](docs/verification/t1-12-distribution.md)：publish・バンドル・配布 ZIP の確認結果
- `CLAUDE.md`：開発ルール。`reference/prototype.py` と `reference/golden/`：比較結果の参照実装と 37 ケース
- [サードパーティ通知](THIRD_PARTY_NOTICES.md)：依存一覧と同梱ライセンス。配布時は `licenses/` を含めて保持する

[GitHub Actions](https://github.com/zredjet/reportdiff/actions/workflows/ci.yml) は Windows x64 / macOS Apple Silicon でビルドとテストを実行する。v0.1.0 の [CI](https://github.com/zredjet/reportdiff/actions/runs/35558470655) は Windows 766 件成功・6 件スキップ、macOS 772 件成功。各リリースの検証結果はリリースページに記載する。完成版 exe のユーザー実機での実行・Edge / Chrome 表示は未確認。

実帳票は `samples/private/` に置き、Git に含めない。テストには合成データだけを使う。

## ライセンス

ReportDiff 本体は [MIT License](LICENSE)（Copyright (c) 2026 zredjet）。依存ライブラリ・ネイティブ DLL・内蔵データはそれぞれのライセンスに従い、本体の MIT License では置き換えない。特に Intel IPP は独自の許諾条件を持つ。[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) と [licenses/](licenses/) を参照し、再配布時はこれらと本体の `LICENSE` を保持する。
