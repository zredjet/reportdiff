# T2-2 移動の注釈の確認

確認日：2026-09-21。ユーザーが [具体仕様案](../planning/t2-2-movement.md) の推奨案を承認し、実装・検証・ローカルコミットまで実施する範囲。正本は [SPEC 11.2](../SPEC.md#112-移動の注釈t2-2)。

## 実装した動作

既定では上下左右それぞれ 5mm 以内を CCoeffNormed で探索し、一致度 0.98 以上・最良と次点の差 0.02 以上を要求する。色と形は既存の一段目の比較でも確認し、逆方向に同じ対応が戻ること、移動元に別の差分が残っていないこと、関連クラスタの差分全体を説明できることを検証する。

移動に該当すれば `kind: moved`、A→B の `shift_px`（右・下が正）、他の関連クラスタの ID を `related_cluster_ids` に記録する。元と先が別クラスタなら同じベクトルを両方に付ける。単独なら関連 ID は空。既存フィールドの型は変えず、追加項目として schema_version=1 を維持する。

検出マスク・削除マスク・ページ状態・クラスタ数／番号／位置／画素数・吸収／ノイズ／警告は変えない。移動も相違のまま。コピー・曖昧な候補・色や形の変更・元位置の残存物・除外等の影響を分離できない場合は T2-1 の分類を残す。背景の 1mm の余白を含む検証領域がページ外に出る場合も保留する。

設定の `move.search_mm`（0〜20、0 は無効化）、`min_score`、`min_score_gap`（0 より大きく 1 以下）は Core・設定読み込み・JSON・HTML に接続済み。プロファイルで移動設定を上書きしない。HTML は方向・px 数・関連クラスタへのリンクを表示し、既存の緑／赤を維持する。新規依存はない。

## 自動検証

macOS 26.5.1 / Apple Silicon、.NET SDK 10.0.401 / Runtime 10.0.12。`dotnet build` は警告 0・エラー 0、`dotnet test` は **504 件成功**（T2-1 の 450 件に 54 件追加）。計測プロジェクトの Release ビルドも成功。

- Core の移動 31 ケース：上下左右・斜めと A/B 反転、通常／厳密・300/400dpi、5mm の px 換算境界と 1px 外、非連続 Mat、複数／単独クラスタ、厳密比較の 1px 移動と通常比較の吸収を確認。
- コピー、A/B 側の複数候補、色変更、濃淡変更、形状変更、元位置の残存物、移動なし、無地、長い単純線、競合、複合領域の一部移動を誤って moved にしないことを確認。
- 除外、クラスタ上限、落としたノイズ、画像端は保留。入れ子では外枠の変更に混ぜず内側の移動だけを注釈する。
- 既存 37 ゴールデンの判定・クラスタ・統計を維持。注釈あり／なしで生差分・輪郭・削除マスクが完全一致。最適化前後は移動量・関連 ID も一致する。
- 設定の追加 14 ケース：未知キー、非有限値、上下限、0 による無効化、全プロファイルから Core / Report への伝達を確認。
- HTML の追加 5 ケース：移動の日本語ラベル、方向と 0 軸の省略、関連リンクの対象、既存 JSON 埋め込み・エスケープを確認。
- PDF の追加 4 ケース：正逆 × 移動注釈の有効／無効。実 CLI プロセス・日本語／空白パスで、右／左 50px の移動を別々の 2 クラスタとして関連付け、25px の移動は 1 クラスタのまま記録する。合計 3 クラスタと終了コード 1 を維持し、JSON・緑／赤の PNG・HTML・埋め込み JSON を確認。PDF 結合は計 12 ケース。

T2-1 の分類だけを確認する混在テストでは移動注釈を無効にし、同じ図形の純粋な移動が moved に変わることと混同しない。検出必須・無視必須の参照期待値や比較の式は変更していない。

## ブラウザ

結合テストと同じ `PdfFixture.CreateMovementReport` から合成 PDF を生成し、実 CLI が出力した `out/t2-2-report/report.html` を file:// で確認した。Chrome 153.0.8010.52 / Playwright 1.62.1、幅 1280px / 390px。

分類・方向・関連 ID と JSON の対応、リンク先の存在・クリックと Enter による移動、移動先の強調、A/B/重ね描き切り替え、Tab・Space・フォーカス、狭い画面の表の横スクロール、画像読み込み、JavaScript 無効時の表示を確認。分類名のセル内収まり、文書全体の横はみ出しなし、ページ例外・CSP 違反・外部リクエスト 0。スクリーンショットは `out/t2-2-browser/`（Git 対象外）。

## 時間とメモリ

Release、合成 A4・300dpi・2480×3508px。画像生成・PDF 描画・ファイル I/O は対象外。各シナリオで測定前に 2 回の結果確認を行い、各サンプル前に GC を実行する。時間は中央値。最適化前後・移動注釈あり／なしで検出結果の一致も確認した。

既存の数字 600 文字の計測を 3 回ずつ再実行。最適化後は完全一致 4.6ms、1 文字変更 222.7ms、1 文字変更＋全体 1px ずれ 235.9ms。[全サンプル](t2-2-a4.json)。

最適化後に絞った別プロセスの比較：

| ケース | 回数 | 移動注釈なし | 移動注釈あり | 移動判定部分（全体中央値の回） |
|---|---:|---:|---:|---:|
| 完全一致 | 5 | 4.7ms | 4.9ms | 0ms |
| 1 文字変更 | 5 | 223.0ms | 223.5ms | 1.40ms |
| 変更＋全体 1px ずれ | 5 | 232.6ms | 235.1ms | 0.91ms |
| 120 図形を各 59px 移動 | 3 | 289.5ms | 476.3ms | 185.97ms |

全体の差には実行間の揺れがある。1 文字の内容変更は移動に誤分類されず、移動判定は約 0.9〜1.4ms。多数候補のケースは 10 列×12 行の非対称図形を各 59px 右へ動かし、移動元・先を含む **240 クラスタすべて**に移動注釈が付いた。クラスタ数は 240 のまま。いずれも合成ケースでの 2 秒以内を維持した。

[通常・注釈なし](t2-2-movement-off.json)、[通常・注釈あり](t2-2-movement-on.json)、[多数移動・注釈なし](t2-2-stress-off.json)、[多数移動・注釈あり](t2-2-stress-on.json)。

移動判定のスレッド別マネージド割当量は、1 文字変更で **61,528 bytes（約 60.1KiB）**、120 図形で **14,962,816 bytes（約 14.3MiB）**。一時的な画像・スコア用 Mat は処理中に破棄し、新たなページ全体のネイティブマスクは保持しない。これらの割当量は native の一時領域やプロセスのピークではない。

`/usr/bin/time -l` の最大 RSS：

| 計測プロセス | 移動注釈なし | 移動注釈あり | 差 |
|---|---:|---:|---:|
| 通常 3 シナリオ | 2,167,848,960 bytes | 2,169,602,048 bytes | 約 1.7MiB |
| 多数移動 2 シナリオ | 2,121,547,776 bytes | 2,142,011,392 bytes | 約 19.5MiB |

[メモリ記録](t2-2-memory.json)。各 1 プロセスのピークで、入力・検証用結果・GC・ネイティブ一時領域も含む。移動判定単体や実 CLI 1 ページの最大メモリとは異なる。最大設定の 20mm・1200dpi、500 クラスタ、巨大なテンプレートを組み合わせた最悪条件を、この数値で受け入れたとはしない。

## 再実行

```bash
dotnet build
dotnet test
dotnet build tools/ReportDiff.Benchmark -c Release
dotnet run --project tools/ReportDiff.Benchmark -c Release --no-build -- 3
/usr/bin/time -l dotnet tools/ReportDiff.Benchmark/bin/Release/net10.0/ReportDiff.Benchmark.dll 5 --movement=off
/usr/bin/time -l dotnet tools/ReportDiff.Benchmark/bin/Release/net10.0/ReportDiff.Benchmark.dll 5 --movement=on
/usr/bin/time -l dotnet tools/ReportDiff.Benchmark/bin/Release/net10.0/ReportDiff.Benchmark.dll 3 --movement=off --stress
/usr/bin/time -l dotnet tools/ReportDiff.Benchmark/bin/Release/net10.0/ReportDiff.Benchmark.dll 3 --movement=on --stress
# Playwright を利用できる環境で、生成済みのレポートを指定する
node tools/verify-html-report.mjs out/t2-2-report/report.html out/t2-2-browser
```

## 残る確認と停止位置

Windows の追加テスト・移動表示・リンク・実行性能は未確認で、TASKS の確認リストに残す。Windows の exe/ZIP は T1-12 時点のままで、今回の分類・移動機能を含めるには再発行が必要。Intel Mac は対象外。実帳票の評価は T2-7。

T2-2 の検証・ローカルコミットまでで停止し、T2-3 以降には進まない。push は行わない。
