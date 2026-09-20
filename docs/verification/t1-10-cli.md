# T1-10 CLI の確認

確認日：2026-09-21、macOS / Apple Silicon、.NET SDK 10.0.401。

**T1-10 完了。** `dotnet build` は警告 0・エラー 0、`dotnet test` は既存 359 件と追加 58 件の計 417 件が成功（失敗・スキップ 0）。

## コマンドと処理

```bash
dotnet run --project src/ReportDiff.Cli -- compare old.pdf new.pdf --out out/result
dotnet run --project src/ReportDiff.Cli -- compare old.png new.png --out out/result \
  --config settings.yaml --profile strict --dpi 300 --pages 1 --save-all-pages
dotnet run --project src/ReportDiff.Cli -- --version
```

`CliApplication.Run(args, output, error)` が引数を解析し、設定 → 形式判定 → ページ選択 → 読み込み・正規化・比較 → JSON・PNG・HTML の生成を接続する。エントリーポイントは `Console.OutputEncoding` を BOM なし UTF-8 に設定し、返された終了コードをそのままプロセスの終了コードにする。

| 指定 | 動作 |
|---|---|
| `compare A B --out dir` | 内容の先頭バイトで PDF・PNG・JPEG・BMP・TIFF を識別し、ページ順で比較 |
| `--config file.yaml` | YAML を読み込み、未知キー・値の範囲等を既存の設定ローダーで検証 |
| `--profile normal/strict/loose` | 明示した場合に YAML より後で適用。省略時は YAML の値を保持 |
| `--dpi n` | 最後に適用。`image_dpi` が設定にない場合はこちらにも反映 |
| `--pages 1-3,5` | 元のページ番号で選択。共通ページのみでも元のページ数不一致を報告 |
| `--save-all-pages` | 相違なしのページの画像も保存 |
| `--no-html` | JSON・必要な PNG を保存し、HTML を作らない。要約のリンク先は JSON |
| `--quiet` | 成功・相違の要約を抑止。エラーは抑止しない |
| `--force` | 出力先全体を置き換える。古い画像・HTML やその他の旧ファイルは残さない |
| `--version` | `reportdiff 0.1.0` を 1 行出力。レポートと同じバージョン定義を使用 |

値を取るオプションは値を別引数で渡す。オプションは入力ファイルの前後に置ける。`--` 以降は入力パスとして扱う。未知オプション、重複、値の不足、入力ファイル数の誤りは終了コード 2。標準出力は 1 行の日本語要約、エラーは日本語で標準エラー出力へ出す。スタックトレースは出さない。

PDF は逐次描画し、画像・正規化結果・比較マスクはページごとに破棄する。全ページのラスタ画像を保持しない。比較・mm 換算・出力には、PDF なら `dpi`、画像なら `image_dpi` を使う。PDF と画像を混在させる場合は両値が同じことを要求し、不一致は設定をそろえるよう案内して終了コード 2 にする。この条件はユーザーの選択を受け、SPEC 4.1・12 章に記録した。比較アルゴリズム・既定値・依存パッケージは変更していない。

## 出力先と失敗時の扱い

出力先が未作成または空なら保存できる。空でない場合は `--force` が必要。実際の生成は出力先と同じ親の `.reportdiff-stage-<ID>` 内で行い、JSON・画像・HTML が揃ってから出力先へ移す。置き換える旧出力は一時的に `.reportdiff-backup-<ID>` に退避する。

- 読み込み・設定・ページ指定・比較・レポート生成に失敗しても旧出力は保持し、作業ディレクトリを片付ける。
- 旧出力の退避後に新出力の移動が失敗した場合は、旧出力を戻す。
- 復元まで失敗した場合、または新出力の保存後に旧出力を削除できなかった場合は、終了コード 2 と残った旧出力の場所を通知する。
- 出力先に入力・設定ファイル、現在の作業ディレクトリ、実行ファイルの保存先を含める指定は拒否する。入力や親ディレクトリのリンク先も確認する。
- 出力先自体のリンク・ジャンクションは拒否する。親の通常のリンクは利用できる。旧出力内のリンクを削除してもリンク先のデータは削除しない。

リンクの扱いは [.NET の ResolveLinkTarget](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.resolvelinktarget?view=net-10.0) と [Directory.Delete](https://learn.microsoft.com/ja-jp/dotnet/api/system.io.directory.delete?view=net-10.0) を確認した。プロセスの強制終了や電源断に対する復旧機能ではなく、その場合は一時ディレクトリが残り得る。

## 検証

CLI のテストには一時ディレクトリ内の合成画像・合成 PDF だけを使う。実プロセスの確認は `dotnet reportdiff.dll` を起動し、標準出力・標準エラーを厳密な UTF-8 で読み、終了コードを検証する。

| 確認項目 | 件数 |
|---|---:|
| 実プロセスの終了コード 0/1/2、UTF-8、バージョン、quiet、no-html、force と古い出力の除去 | 5 |
| 不正なコマンド・オプション・値・引数数 | 20 |
| 相違なしの画像保存有無・空の出力先 | 2 |
| 設定の優先順位・image_dpi の mm 換算・除外適用・差分過多の終了コード | 3 |
| PDF のページ数不一致と共通ページのみの選択、A/B 反転 | 4 |
| PDF と画像の混在、DPI 一致・不一致、A/B 反転 | 4 |
| 入力・設定・ページ指定等のエラーでも force が旧出力を保持 | 6 |
| 途中の巨大 PDF ページで失敗した場合の部分出力除去と旧結果保持 | 2 |
| ファイルへの出力拒否、入力・設定の保護、共通接頭辞と親子関係の区別 | 3 |
| 置き換え失敗の復元、生成中に増えたファイルの保護、作業先・実行先・ルートの保護 | 3 |
| 出力先リンク（既存・不在）、入力・親リンクの包含判定、リンク先保護、親リンク経由の出力 | 5 |
| macOS で合成・分解された濁点を含む同一パスの入力保護 | 1 |
| 追加合計 | 58 |

ブラウザ確認用には CLI から合成ゴールデン D11 を既定設定で比較した。終了コード 1、相違 2 箇所、ページ PNG 3 枚、切り出し PNG 6 枚、JSON と HTML が生成された。

```text
相違あり: 1 ページ中 1 ページ、2 箇所 → out/t1-10-cli/日本語の 比較結果/report.html
```

この HTML を Chrome 153.0.8010.52 の `file://` で開き、幅 1280px / 390px、全画像の読み込み、A/B/重ね描きとキーボード操作、埋め込み JSON の一致を確認した。ページエラー・CSP 違反・外部リクエストは 0 件。

## 再実行

```bash
dotnet build
dotnet test
dotnet test -- --filter-class ReportDiff.Tests.CliTests
dotnet test -- --filter-class ReportDiff.Tests.OutputWorkspaceTests
```

ブラウザは [T1-9 の検証スクリプト](t1-9-html.md#再実行)で生成済み `report.html` を確認できる。

## 残る範囲

指定の「2 ページ PDF・1 箇所の変更と除外領域内の変更」の結合シナリオは [T1-11 確認結果](t1-11-integration.md)、配布準備は [T1-12 確認結果](t1-12-distribution.md) を参照。今回追加したコードの Windows CI・実機動作、cmd / PowerShell の日本語表示、Windows Edge / Chrome、実際のディスク容量不足は未確認。

リンクに関する 5 テストと macOS 固有の Unicode 正規化の 1 テストは macOS で実施。Windows ではこの 6 件をスキップし、リンク作成権限・ジャンクションを別途確認する。復元そのものの失敗・退避済み旧出力の削除失敗・電源断は自動テストの受け入れ対象に含めていない。Intel Mac は対象外。
