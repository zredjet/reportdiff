# ReportDiff

業務帳票の PDF・画像を比較し、相違を「箇所」単位で報告する CLI ツール。細かな描画差や許容範囲内の位置ずれを吸収し、変更部分の画像と JSON・オフライン HTML を出力する。

実行対象は Windows x64。開発・検証環境は macOS Apple Silicon。Intel Mac は対象外。Phase 1 の実装・macOS の全 423 テスト・Windows 向け publish は完了している。完成版の Windows CI・実機確認は [確認リスト](docs/TASKS.md#windows-確認リスト人が実機で行う)に残る。

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

HTML では A / B / 重ね描きを切り替え、変更位置（mm）と切り出しを確認できる。レポートを移動するときは出力フォルダごとコピーする。相違のないページの PNG は既定で保存しない。

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

`--force` は出力フォルダ内の他のファイルも置き換えるため、結果専用のフォルダを指定する。処理中の失敗では旧結果を保持するが、電源断やプロセスの強制終了からの自動復旧機能はない。入力・設定ファイル、作業ディレクトリ、実行ファイルの保存先を含む出力先は指定できない。

## 設定とプロファイル

まずは設定なしの `normal`・300dpi で比較する。コピーして編集できる [設定例](examples/settings.yaml)は既定値と同じで、除外領域は空になっている。

日時などを除外する例：

```yaml
dpi: 300
exclude:
  - {page: all, x: 150, y: 8, w: 50, h: 6, note: 出力日時}
report:
  crop_margin_mm: 2.0
```

長さはすべて mm、座標はページ左上が原点。`page` は `all` または 1 始まりの番号。未知のキーや範囲外の値はエラーになる。実際に使った設定と除外領域は JSON・HTML で確認できる。

設定の優先順位は **既定値 → YAML → 明示した `--profile` → `--dpi`**。プロファイルが上書きするのは `max_shift_mm` と `edge_tolerance`。`image_dpi` を省略すると最終的な `dpi` に追従する。画像入力では埋め込み DPI を使わず `image_dpi` で mm 換算するため、画像を作った解像度に合わせる。PDF と画像を混在させる場合は `dpi` と `image_dpi` をそろえる。不一致はエラーになる。

| プロファイル | 位置ずれの許容 | エッジの許容係数 | 選ぶ場面 |
|---|---:|---:|---|
| `normal`（既定） | 0.15mm | 0.3 | 通常の帳票比較 |
| `strict` | 0mm | 0 | 同じエンジン・同じ環境の回帰確認。微小な描画差も検出したい場合 |
| `loose` | 0.30mm | 0.3 | 現新比較など、位置の揺れが大きい場合 |

`strict` でも色のしきい値や小さなノイズの除去は残るため、ファイルのバイト単位の一致検査ではない。JPEG・スキャン画像では `diff.color_threshold` を 8〜12 に上げる設定を試せるが、小さな色差を見逃しやすくなる。設定の全項目は [SPEC 7 章](docs/SPEC.md#7-設定ファイル)を参照。

## 既知の限界

小さい文字が多い帳票は 400dpi を検討する。200dpi 以下では「未／末」「ば／ぱ」などを安定して検出できない。細い線の微妙な濃淡差や 1px 以内の長さの変化は通常設定で吸収される場合がある。行の挿入で下がずれると、それ以降がまとめて差分になる。[既知の限界の一覧](docs/SPEC.md#56-既知の限界仕様)を確認する。

フォントを埋め込んでいない PDF は OS の代替フォントにより描画が変わる。A と B は同じマシンの同じ実行で比較する。PDF のパスワード指定、文字認識、差分の追加／削除分類、移動判定、全体の位置補正、フォルダ一括比較は現在の対象外。[入力仕様](docs/SPEC.md#4-入力の正規化)と [今後の範囲](docs/SPEC.md#11-phase-2-以降の仕様の概要)を参照。

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

- [SPEC](docs/SPEC.md)：アルゴリズム・設定・入出力の正本
- [TASKS](docs/TASKS.md)：完了範囲と Windows 実機確認リスト
- [T1-12 の検証記録](docs/verification/t1-12-distribution.md)：publish・バンドル・配布 ZIP の確認結果
- `CLAUDE.md`：開発ルール。`reference/prototype.py` と `reference/golden/`：比較結果の参照実装と 37 ケース
- [サードパーティ通知](THIRD_PARTY_NOTICES.md)：依存一覧と同梱ライセンス。配布時は `licenses/` を含めて保持する

[GitHub Actions](https://github.com/zredjet/reportdiff/actions/workflows/ci.yml) は Windows x64 / macOS Apple Silicon でビルドとテストを実行する。リモートで確認済みなのは [T0-3](docs/verification/t0-3-ci.md) 時点の疎通 2 件。Phase 1 の全 423 件はローカル macOS で検証済みで、Windows の追加テスト・完成版 exe の実行・Edge / Chrome 表示は未確認。

実帳票は `samples/private/` に置き、Git に含めない。テストには合成データだけを使う。
