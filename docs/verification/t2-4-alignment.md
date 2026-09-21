# T2-4 全体補正の確認結果

確認日：2026-09-21。対象は [承認済み計画](../planning/t2-4-alignment.md) と SPEC 11.4 節。開始点は `b549c2a`（T2-3）。macOS Apple Silicon で実装・検証。Windows は配布物の静的確認と実行確認を分けて記録する。

## 実装した動作

- `align.enabled: false` を既定にし、明示的に有効な場合だけ縮小探索と元解像度での確認を行う。探索距離 5mm、一致度 0.98、候補差 0.02、改善量 0.05 はコメント付き YAML で変更できる。
- B に加える整数 px の補正量を B→A として記録する。複数区画の支持がない、候補が曖昧、元サイズが違う、端の内容を失う場合などは元の画像で比較する。
- 補正後の画像を既存コアへ渡す。除外領域・クラスタ矩形・PDF テキスト・切り出しを補正後の座標にそろえ、局所の移動注釈は残存する A→B の移動量として維持する。
- JSON / HTML に実効設定、推定候補、スコア、支持区画数、未適用理由を記録する。適用時は `GLOBAL_SHIFT_APPLIED` と `global_shift_px` を記録し、相違なしでも A・補正後 B・補正前 B・重ね描きを保存する。
- 既存の検出式・既定値・ゴールデン期待値と依存は変更していない。

追加依頼の外部設定整備は **T2-4a** として [計画・受け入れ条件](../planning/t2-4a-configuration.md)を追加した。既存の YAML / `--config` を継続し、残る固定パラメータの台帳・設定化・コメント付き設定例・範囲と優先順位・実効設定の網羅を別タスクで行う。T2-4a 自体を実装完了とはしていない。

## 自動検証

`dotnet build` は警告 0・エラー 0、`dotnet test` は **全 589 件成功・失敗 0・スキップ 0**（約 76 秒）。既存 37 ゴールデンも成功。追加は 51 件：補正コア 27、PDF / CLI 接続 7、設定 14、HTML 3。既存の JSON 契約テストは、承認済みの追加項目を明示して検査対象を増やした。

- 全方向、72 / 100 / 300 / 400dpi、探索範囲の切り捨てと上限・上限の 1px 外側、元画像不変、非連続 Mat を検証。
- 数値変更を含む画像を補正し、元の「ずらしていない変更画像」と画素単位で一致すること、その後の生差分・クラスタ・分類も一致することを通常／厳密で確認。
- 無効、探索距離ゼロ、サイズ不一致、白紙、情報の少ない単独図形、繰り返し罫線、半 px のずれ、全域除外、除外内変更、各方向の端の 1 画素の非白を検証。
- 合成 PDF の変更あり・なし、A/B 反転、PDF / PNG 混在、quiet / no-html、元サイズ差、片側ページを CLI から確認。CLI を使う 6 テストで実プロセス 7 回、別に PDF 単語の補正後座標と除外のテスト 1 件。
- 自作 Type3 フォントを埋め込んだ合成 PDF を使用。144dpi の B の右 6px・上 4px の移動に対し、補正量は左 6px・下 4px。移動のみは終了コード 0、数値 11123→11128 の変更併存は 1 クラスタ・終了コード 1。テキスト注釈も各側の正しい値を保持。
- 相違なしでも 4 枚の証拠画像があり、補正前 B の PNG と元 PDF の描画結果が完全一致。無効・探索距離ゼロでは元の比較結果を維持し、補正量と元 B の追加画像は null。
- コメント付き YAML の部分指定、型・範囲・未知／重複キー、プロファイル・DPI の優先順位を検証。HTML の方向と候補／適用量の区別、元 B の外部 URL 拒否も確認。

## HTML

macOS Chrome **153.0.8010.52** / Playwright で、数値変更あり・補正後の相違なしの両レポートを `file://` から確認した。各 1280 / 390px 幅で成功。

- A／補正後 B／補正前 B／重ね描きの切り替え、ラベル・リンク先・aria-pressed の同期
- 補正前 B ボタンへのキーボードフォーカスと Enter、既存の Tab / Space 操作
- 補正量・推定候補・スコアを含め、画面や要素が横にはみ出さないこと
- JSON 埋め込みと result.json の一致、画像の読み込み、変更テキストの表示
- JavaScript エラー 0、CSP 違反 0、外部リクエスト 0。JavaScript 無効時も基本レポートを表示

`tools/verify-html-report.mjs` に補正前 B と補正情報の検査を追加した。画像は `out/t2-4-browser-changed/`、`out/t2-4-browser-same/`。390px の補正情報と 1280px の補正済みページを目視でも確認した。これは macOS の Chrome の結果で、Windows の Edge / Chrome の実行確認ではない。

## 時間とメモリ

A4（2480×3508px）、300dpi、合成 600 文字、Release。1 回の予熱後、各 3 回の中央値。[計測結果](t2-4-alignment-benchmark.json)。補正の時間には探索と補正画像作成を含み、全体時間は既存コアの比較も含む。PDF 描画・テキスト・レポートの時間は含まない。

| 条件 | 補正処理の中央値 | 比較までの全体中央値 | 結果 |
|---|---:|---:|---|
| 無効・内容変更のみ | 0.01ms 未満 | 221.3ms | 1 クラスタ |
| 有効・全体移動のみ | 338.3ms | 344.6ms | 相違なし、補正量 (-29,-19) |
| 有効・全体移動と数値変更 | 329.6ms | 550.9ms | 1 クラスタ、補正量 (-29,-19) |
| 有効・白紙 | 9.6ms | 15.2ms | 補正を省略、相違なし |

macOS の `/usr/bin/time -l` による同じ計測プロセスの最大 RSS は **1,741,914,112 bytes（約 1.62GiB）**、peak memory footprint は **1,712,162,952 bytes**。[メモリ記録](t2-4-alignment-memory.json)。入力生成・予熱・既存コア・ネイティブ一時領域も含む 4 シナリオのプロセス全体で、補正単体や実 CLI 1 ページの最大値ではない。.NET の PeakWorkingSet64 はこの Mac では値を取得できず、JSON は null とする。最大設定 20mm・1200dpi や巨大画像の最悪条件をこの計測で受け入れたとはしない。

## Windows 配布物

Windows x64 / Release / 自己完結 / 単一 exe の publish が成功。exe は **156,463,173 bytes**、SHA-256 は `e5a0bf59becd64e51b065e487e454a96545e40655999ca1d2c7eee830cdeb959`。[バンドル記録](t2-4-win-bundle.json)。pdfium / OpenCvSharpExtern / SkiaSharp は x64 で、T2-3 と同じハッシュ。PdfPig の管理 DLL 7 件と依存・.NET Runtime 10.0.12 も維持し、FFmpeg・他 OS 資産は含まれない。新規依存や許諾文の変更はない。

配布 ZIP は `out/dist/reportdiff-t2-4-win-x64.zip`。依存・ランタイム・通知ハッシュ、ZIP の CRC と全ファイルの SHA-256 を検証した。更新した手順・設定例・検証文書を含めて梱包する。梱包結果は `out/t2-4-package-result.json`、内包物は ZIP 内の manifest.json が示す。ZIP 自体のハッシュを同梱文書へ埋め込まない。

Windows での exe 実行・追加 51 テスト・Edge / Chrome 表示は未確認。macOS の結果とバンドルの静的確認で実機の受け入れを代用せず、TASKS に残す。

## 再実行

```bash
dotnet build
dotnet test
dotnet build tools/ReportDiff.AlignmentBenchmark -c Release
/usr/bin/time -l dotnet tools/ReportDiff.AlignmentBenchmark/bin/Release/net10.0/ReportDiff.AlignmentBenchmark.dll 3
# レポート出力先が未作成のディレクトリを指定する。
dotnet tools/ReportDiff.AlignmentBenchmark/bin/Release/net10.0/ReportDiff.AlignmentBenchmark.dll --fixture out/t2-4-fixture
node tools/verify-html-report.mjs out/t2-4-fixture/changed/report.html out/t2-4-browser-changed
node tools/verify-html-report.mjs out/t2-4-fixture/same/report.html out/t2-4-browser-same
dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t2-4-publish
python3 tools/inspect-win-bundle.py out/t2-4-publish/reportdiff.exe
python3 tools/package-win.py out/t2-4-publish/reportdiff.exe out/dist/reportdiff-t2-4-win-x64.zip
```

T2-4 の実装・検証・1 ローカルコミットまで。T2-4a はタスク化・文書化までで、実装や T2-5 以降、push、Release 公開は行わない。
