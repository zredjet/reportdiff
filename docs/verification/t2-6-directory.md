# T2-6 フォルダ比較の確認結果

確認日：2026-09-21。macOS Apple Silicon、.NET SDK 10.0.401 / Runtime 10.0.12。[承認仕様](../planning/t2-6-directory.md)、[SPEC 11.6](../SPEC.md#116-フォルダ一括比較t2-6)に従い、`compare-dir` を実装した。

## 実装と確認範囲

再帰探索と相対パスの対応付け、コメント付き YAML による帳票別の部分設定、一覧 JSON / HTML と個別レポートへのリンクを追加した。既存の単一比較を共通処理として呼び、ファイル・PDF を逐次処理する。比較式・既定値・個別の出力スキーマ・依存は変更していない。

片側のファイルは存在の差分として報告し、内容は未検査。破損入力・ページ範囲・複数規則一致等は個別エラーとして続行する。個別エラーを含む完了では終了コード 2 と一覧を保存し、`--force` なら旧結果を置き換える。列挙・設定・パスの開始前エラー、画像や結果の出力障害は全体を中止し、旧結果を保持する。

設定は既定値 → 共通 YAML → 選択 YAML → 明示 profile → CLI の順。ネストは指定キーを上書き、exclude 配列は全置換。image_dpi の明示・省略を維持する。全参照設定を開始前に検証し、内容を再読込せず、出典パス・SHA-256・CLI 値を一覧へ記録する。

## 自動テスト

`dotnet build` は **警告 0・エラー 0**。最終 `dotnet test` は **772 成功、失敗 0、スキップ 0**（約 86 秒）。T2-5 の 700 件に 72 件を追加し、既存 37 ゴールデンと単一比較の動作を維持した。

| 追加対象 | 件数 | 確認した内容 |
|---|---:|---|
| DirectoryPlanTests | 18 | 隠し項目、7 拡張子、日本語・大小文字・NFC と元表記、順序と A/B 反転、対象外・フォルダも含む衝突、出力包含、同一・入れ子の入力、ルート不在、リンクと親の別名 |
| DirectoryRulesTests | 32 | 厳密な YAML、未知・重複・型・版・未対応正規表現、0/1 一致、A/B 両側の評価、相対参照、未使用設定の検証、部分上書き・空 mapping・exclude の置換、image_dpi、profile / CLI、出典ハッシュ・読込後の編集、別 YAML の同名アンカー、同梱例 |
| DirectoryCliTests | 22 | 実プロセスの同一・相違・片側・破損・曖昧な規則、一覧と終了コード、各オプション、PDF 内容判定・選択ページ・フォント警告、混在 DPI、空、旧結果の保持と置換、不完全な子出力の削除、単一比較との JSON・PNG 一致、出力/列挙障害の注入、HTML エスケープ |

追加確認で、別々の YAML にある同名アンカーを合成後に取り違える不具合を発見した。各レイヤーで検証済みの値を複製し、アンカー名を合成先へ持ち越さないよう修正した。回帰テストを追加し、実 CLI でも dpi / image_dpi が 144、色しきい値 / インクコントラストが 8 のまま保たれることを確認した。修正後にビルド・全テスト・実 CLI・計測・ブラウザ・Windows 配布物の検証を行った。

単一比較との照合は生成時刻だけを除いた JSON 全体と PNG のバイト列を比較した。実際の後半 PDF ページの読込失敗では、最初のページに出力した PNG も含めて失敗した子ディレクトリを削除し、次の対の結果を保存できた。

macOS 上の実シンボリックリンクで、入力ルート・フォルダ・ファイル・切れたリンクの拒否と親リンクの解決を確認した。大小文字・Unicode の同一側衝突はファイルシステムによる作成制約を避け、列挙結果を与えるテストで確認した。列挙途中の失敗は IOException を注入し、個別レポートと一覧の出力障害は書込先をファイルで妨げて検証した。実際の権限拒否・ディスク満杯・Windows ジャンクションの実機試験ではない。

```bash
export DOTNET_CLI_HOME=/private/tmp/reportdiff-dotnet-cli
export NUGET_PACKAGES=/private/tmp/reportdiff-nuget/packages
export NUGET_HTTP_CACHE_PATH=/private/tmp/reportdiff-nuget/http-cache
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
dotnet build
dotnet test
```

## 実 CLI と HTML

`tools/verify-directory-cli.py` で 64×64 PNG と自作の 216pt PDF を生成し、相違・同一・フォント警告・破損・複数規則・片側だけ・対象外を混ぜた 7 項目を比較した。比較成功 3（same 1 / different 2）、片側各 1、error 2、対象外延べ 2。終了コード 2 で、すべての状態を一覧に記録できた。合成データのみを使用している。

Chrome 153.0.8010.52 の file:// で 1280px / 390px を検証。長い日本語・空白・& を含むファイル名、選択設定、複数一致の理由、対象外の開閉、要素ごとの幅、キーボードのフォーカスと個別結果への遷移、相対リンク先の存在を確認した。JavaScript 無効でも一覧が表示され、横のはみ出し・ページエラー・外部通信は 0。撮影画像で一覧と狭い幅のエラー欄も目視した。HTML 特殊文字は自動テストでもエスケープを検証した。

個別の PDF レポートは既存の `verify-html-report.mjs` でも検証済み。画像切替・フォント警告・設定欄・1280/390px・キーボード・外部通信なしを確認した。証拠は `out/t2-6-final/small-result/`、`browser/`、`individual-browser/` と各 browser-result.json。

```bash
# 出力先は未作成のフォルダを指定する。
python3 tools/verify-directory-cli.py src/ReportDiff.Cli/bin/Debug/net10.0/reportdiff.dll out/t2-6-final
node tools/verify-directory-report.mjs out/t2-6-final/small-result/index.html out/t2-6-final/browser
node tools/verify-html-report.mjs out/t2-6-final/small-result/files/f000007/report.html out/t2-6-final/individual-browser
```

## 時間とメモリ

独立した CLI プロセスを毎回起動し、予備 1 回を除いた 3 回の中央値。macOS `/usr/bin/time -l` の maximum resident set size（bytes）を使い、起動・列挙・比較・ファイル出力を含む。CLI は Debug ビルド。計測中に全テストや別の比較処理は実行していない。

| 入力 | 時間中央値 | 最大 RSS の中央値 |
|---|---:|---:|
| 状態混在 7 項目、PDF は 144dpi、HTML あり | 547.603ms | 131,596,288 bytes（125.5 MiB） |
| 同一 PNG 10 対、strict / no-html | 247.059ms | 76,791,808 bytes（73.2 MiB） |
| 同一 PNG 100 対、strict / no-html | 279.068ms | 79,757,312 bytes（76.1 MiB） |
| 同一 PNG 300 対、strict / no-html | 356.461ms | 83,378,176 bytes（79.5 MiB） |

[生の計測 JSON](t2-6-measurement.json)。一覧用メタデータとファイルパスは件数に応じて保持するが、入力画像・PDF はファイル対ごとに解放する。この小さな画像の結果を、大きな実帳票・多数ページ PDF・Windows の性能保証とはしない。

## Windows 配布物と残件

`dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t2-6-publish` が成功。exe は **156,547,141 bytes**、SHA-256 は `06fb9d567de84ab0e6b724b4074ddd6a885e7e01918fa3c7754a21196ad8bd21`。

[バンドル検査](t2-6-win-bundle.json)で、自己完結ランタイム・既存依存、3 種の必須 x64 ネイティブ DLL のハッシュ、FFmpeg・他 OS のネイティブ資産の不在を確認した。依存追加・更新はない。

`tools/package-win.py` で候補 ZIP の CRC、全内容物 SHA-256、依存構成、許諾原文を検証済み。最終ドキュメントを含む配布物は `out/dist/reportdiff-t2-6-win-x64.zip`。サイズ・SHA-256 は `out/t2-6-final/package-result.json`、各内容物の値は ZIP 内の manifest.json に記録する。

```bash
python3 tools/inspect-win-bundle.py out/t2-6-publish/reportdiff.exe
python3 tools/package-win.py out/t2-6-publish/reportdiff.exe out/dist/reportdiff-t2-6-win-x64.zip
```

同梱 YAML を下位フォルダまで収集するよう配布ツールを更新し、`examples/batch/rules.yaml` と既存 5 種の設定を一緒に配布する。相対参照先は読み込みテストでも確認した。

Windows 実機での実行、再解析ポイント・ジャンクション、権限拒否、Edge / Chrome の file:// 表示は未確認。[Windows 確認リスト](../TASKS.md#windows-確認リスト人が実機で行う)で継続管理する。T0-2 の Windows 動作 OK と今回の静的検証を、完成版の実機確認として扱わない。Intel Mac は対象外。T2-6 のローカルコミットで止め、T2-7・push・Release 公開へ進まない。
