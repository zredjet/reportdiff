# T2-4a 外部設定整備の確認結果

確認日：2026-09-21。環境は macOS Apple Silicon、.NET SDK 10.0.401 / Runtime 10.0.12。承認された[設定追加案](../planning/t2-4a-parameter-catalog.md)に従い、14 項目を任意の YAML 設定として追加した。[設定リファレンス](../CONFIGURATION.md)と[全項目の設定例](../../examples/settings.yaml)に既定値・範囲・適用箇所・影響を記載する。

## 実装した範囲

- インク定義 2 項目、クラスタの読み順 1 項目、移動テンプレートの余白 1 項目、PDF テキストの上限・行判定 4 項目、全体補正の探索格子・支持条件 6 項目。既定値と固定構造は維持した。
- `ink` は局所ずれ吸収・分類・移動で共有。`text` は PDF 注釈だけに適用する。長さは mm、面積は mm² を Units で換算し、計算格子の要素数は物理的な長さと区別する。
- 部分設定の省略値はオプション型の既定値から補い、全 33 スカラー設定値と除外領域を JSON / HTML に記録する。入力 YAML は書き戻さない。
- 重複キーも `ink.background_radius_mm` や `exclude[0].x` のように該当キーを含む日本語エラーにした。未知キー、型、有限値、範囲、最終 DPI での探索候補数・結合カーネルの整数限界、除外座標と寸法の加算オーバーフローを検証する。
- 全項目の例に加え、minimal / strict / scan / align の用途別 YAML を追加。Windows パッケージに examples の YAML 一式を含める。

## ビルド・テスト

`dotnet build` は警告 0・エラー 0。`dotnet test` は **640 成功、失敗 0、スキップ 0**（約 72 秒）。T2-4 の 589 件に 51 件を追加した。

| 対象 | 結果 |
|---|---|
| 設定テスト 36 件追加 | 新規全14項目の上下限・型・非有限値、重複／未知キー、派生値の上限、コメント・部分指定・空セクション・省略、プロファイル／CLI優先順位、新旧 YAML / JSON、全設定例と既定値の一致 |
| 動作・実 CLI テスト 15 件追加 | インクによる分類、帯幅による番号・同画素数での上限選択、余白と共有インクによる移動推定、非既定の縮小・復元探索、区画数・行列数・面積、支持条件を緩めた場合の局所図形、Unicode本文上限、行の重なり、PDF抽出上限、実プロセスと入力コメントの保持 |
| 既存 37 ゴールデン | C# の比較結果が従来の期待を満たす。期待値への変更は ink の2値と reading_band_mm の params 追加だけ。既存の判定・数値・クラスタ・許容差を変更していない |
| その他の既存テスト | 位置ずれの最適化前後、画像／PDF、分類・移動・全体補正、注釈、出力・CLI等がすべて成功 |

HTML テストの初回は「mm²」の文字参照を考慮していないアサーションで失敗した。表示テキストとしてデコードして検証するよう修正し、全件成功を確認した。製品側の HTML エスケープは維持している。

再実行：

```bash
export DOTNET_CLI_HOME=/private/tmp/reportdiff-dotnet-cli
export NUGET_PACKAGES=/private/tmp/reportdiff-nuget/packages
export NUGET_HTTP_CACHE_PATH=/private/tmp/reportdiff-nuget/http-cache
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
dotnet build
dotnet test
```

## Python 参照実装

インク半径・しきい値と読み順の定数を Params に移し、SPEC 5 / 7 / 11 / 12、C# のゴールデン読み込み、expected.json の params と同期した。保存済み PNG をそのまま使い、フォントによる入力の再生成は行っていない。

OpenCV 4.13.0（opencv-python-headless 4.13.0.92）・NumPy 2.5.3 の検証環境で、37 ケースの status / raw_pixels / noise_dropped / absorbed_groups / max_shift_px と、各クラスタの全フィールドが **完全一致**。既知の限界ケースも保存済み結果のまま。機械可読な結果は [参照照合](t2-4a-reference.json)。Python の環境は開発時の検証用で、製品・Windows ZIP の依存には含まない。

```bash
python3 -m venv out/reference-check-env
out/reference-check-env/bin/python -m pip install opencv-python-headless==4.13.0.92 numpy==2.5.3
out/reference-check-env/bin/python tools/verify-reference-golden.py
```

## 実 CLI・ブラウザ

合成の新旧 PDF を 144dpi で比較し、14 項目をすべて非既定値にした設定を指定。全体補正も有効化した。終了コード 1、1 ページ・1 箇所の差分、全体補正の採用、本文上限 4 文字による A/B それぞれの TEXT_ANNOTATION_TRUNCATED を確認した。JSON の config と HTML の表示に指定値が反映された。

macOS Chrome **153.0.8010.52** の `file://` で 1280px / 390px を確認。全 33 設定値、項目名・値の幅、PDF 注釈、補正前後の画像、キーボード操作、画像読み込み、JavaScript 無効時の表示を確認し、ページエラー・外部リクエストは 0。設定欄を開いた画像も目視し、数値とラベルの対応、狭い画面での縦配置を確認した。

ローカル確認物は `out/t2-4a/report/report.html`、`out/t2-4a/settings.yaml`、`out/t2-4a/browser/`、`out/t2-4a/browser-result.json`。再確認は `node tools/verify-html-report.mjs <report.html> <画像保存先>`。同スクリプトには設定値数・横のはみ出し・設定欄の撮影も加えた。

## Windows 配布物

`dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t2-4a-publish` が成功。exe は **156,481,605 bytes**、SHA-256 は `349479c7cf01bbbf0024ee840f23233a6dedc6b52da4f876a648a1c76f11ca1e`。

[バンドル検査](t2-4a-win-bundle.json)で既存依存・自己完結ランタイムと許諾台帳の一致、必須の x64 ネイティブ DLL のハッシュ一致、FFmpeg・他 OS のネイティブ資産の不在を確認。製品の依存パッケージは追加していない。

`tools/package-win.py` で ZIP の CRC・全ファイルの SHA-256・依存と原文通知を検証した。全 128 ファイルに、settings / minimal / strict / scan / align の 5 種類の YAML と docs/CONFIGURATION.md が含まれ、元ファイルとバイト単位で一致することも確認。検証用 Python 環境や wheel は含めない。

配布用の出力先は `out/dist/reportdiff-t2-4a-win-x64.zip`。ZIP のサイズと SHA-256 はローカルの `out/t2-4a/package-result.json` に保存する。ZIP 内の manifest.json は各内容物のサイズと SHA-256 を記録する。

```bash
python3 tools/inspect-win-bundle.py out/t2-4a-publish/reportdiff.exe
python3 tools/package-win.py out/t2-4a-publish/reportdiff.exe out/dist/reportdiff-t2-4a-win-x64.zip
```

Windows での実行と Edge / Chrome の表示は未確認。[Windows 確認リスト](../TASKS.md#windows-確認リスト人が実機で行う)に追加 51 件・14 項目の調整・設定例の確認を残す。Intel Mac は対象外。T2-5・push・Release 公開は実施しない。
