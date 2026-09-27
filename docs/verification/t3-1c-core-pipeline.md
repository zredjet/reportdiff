# T3-1c Core処理の移行検証

2026-09-22、macOS arm64／.NET 10.0.12／OpenCV 4.13.0。**Coreの候補推定・元帯検証・C/D構築・単一原因の因果集約を実装し、固定76実行で既存結果と一致した。設定・CLI・レポートには未接続。** T3-1c全体の受け入れ・コミットは未完了。[処理の契約](../planning/t3-1c-core-pipeline.md)、[機械可読記録](t3-1c-core-pipeline.json)を参照。

## 照合結果

| 対象 | 結果 |
|---|---|
| 新規回帰テスト | 27件成功 |
| 全テスト | 1,773件成功、失敗・省略0、152.378秒 |
| Releaseビルド | Tests／PageFlowProbeとも警告・エラー0 |
| 固定入力 | 76実行、332元画像の記述を再生成。PDFの保存ハッシュを確認 |
| 候補 | 帯・本文・支持数・検証状態・理由が一致 |
| 文書単位の範囲 | 成立24実行、基準経路52実行。各ページの選択が一致 |
| 元ページ上の帯置換 | 528比較で生差分0を確認 |
| 内容比較C | 180比較の生差分、比較面画像、C/D写像が一致 |
| 表示投影D | 52ページの生差分、実構造ID・矩形、内容／表示クラスタが一致 |
| 基準経路 | 106ページの選択済み生差分と件数が一致 |
| 片側ページ | 16ページの帯外残余の判定が一致。網羅性falseを維持 |
| 因果集約 | 集約18実行、見送り58実行。原因・所属・補助帯・収支・件数・網羅性が一致 |
| 集約の安全検査 | 1,770条件成功。列挙順228、根拠欠落等の見送り1,496、false網羅性維持46 |

既存の独立推定器・構造帯構築器・因果集約器を残し、Core側の入力には元PNG、実PDFから抽出したテキスト、選択ページだけを渡した。保存された正解候補・写像・構造IDは照合にのみ使用した。集約の欠落検査は独立側の入力に変更を加え、Coreへ型変換して評価する。

今回はPythonの参照式を変更せず、新たな参照比較も実行していない。以前にPythonとの一致を確認した固定生差分と今回のCore生差分を照合した。比較式・しきい値・検出期待の変更はない。旧集約入力に不正な側や構造参照が入った場合の追加検査は、安全側の見送りとなる。

## 維持した検出と限界

- R10は5→1、R11は6→1、3ページ連鎖は8→1。R11の片側ページを含む網羅性falseを引き上げない。
- 対応本文や挿入境界の色変更は1内容クラスタを維持し、6→2となる。
- 第二の挿入は7→7、第二の挿入＋色変更は8→8で集約を見送る。
- 数値555→556の変更は全体の送りを見送り、基準経路の9件を維持する。
- 送り帯の実差、1px長い本文端、反復文字、固定部分の不足、非隣接の選択ページは保守的に見送る。未成立の入力を成功扱いに変更していない。

新規テストでは、A/B交換、2→2／2→3／3→3ページ、実DPI・最大移動量・支持数、rows無効時の再読込禁止、除外や許容設定で255→254の帯差を隠さないこと、後半ページの失敗による全体復帰、記述収集後／範囲確定後の元画像変更、ROIの行ストライド、選択外ページを読まないこと、候補129件目での全破棄、実際の内容変更と領域・除外のC/D転写、欠落・重複・除外・不正参照の集約見送りを確認した。

候補数上限のテストは、128ページの両方向に80pxずつの候補が出るよう設計した。72dpiでは最大20mmの探索範囲を超えて候補にならなかったため、範囲内になる150dpiで上限到達を確認した。探索上限や期待値を緩めていない。

## 再現用の入力

以下はいずれも`out/t3-1c-probe/`からの相対パス。結果は`out/t3-1c-core/replay-final/<組名>.json`、安全検査は同じ場所の`<組名>-audit.json`。

| 組名 | PDF入力 | 独立候補 | 独立集約 | 実行数 |
|---|---|---|---|---:|
| fixed | type3-final | gate-fixed | aggregate-fixed | 24 |
| additional | candidate-fixtures-final | gate-additional | aggregate-additional | 12 |
| skia | skia-fixtures | skia-candidates | aggregate-skia | 28 |
| skia-local | skia-local-fixtures | skia-local-candidates | aggregate-skia-local | 6 |
| causal | causal-fixtures-final | causal-candidates | aggregate-causal | 6 |

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet build tests/ReportDiff.Tests/ReportDiff.Tests.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll -class ReportDiff.Tests.PageFlowCoreTests -result-xml out/t3-1c-core/reproduce-core-tests.xml
dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll -result-xml out/t3-1c-core/reproduce-all-tests.xml
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --core-replay out/t3-1c-probe/type3-final out/t3-1c-probe/gate-fixed out/t3-1c-probe/aggregate-fixed out/t3-1c-core/reproduce/fixed.json
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --core-aggregation-audit out/t3-1c-probe/aggregate-fixed out/t3-1c-core/reproduce/fixed-audit.json
```

残る4組も表の対応で指定する。結果ファイルが既に存在するときは別の出力先を使う。元PDF・候補JSON・集約JSON、最終照合ファイル、テストXML、今回のCoreソースのSHA256は機械可読記録に保存した。元PNGは既存の独立候補の保存物を使うため、今回はPDFの再描画・視覚確認をやり直していない。

## 変更範囲と再開位置

前段階の81製品ファイルは`t3-1b-acceptance.json`のハッシュと一致。前回のCore基盤3ファイルに加え、今回6ファイルを追加した。CLI／Pdf／ReportからのPageFlow参照はまだない。独立検証ツールにCore照合用のモード2つを追加し、TASKS・SPEC・計画を更新した。

次は、既存RowOptionsの採用条件と送り経路の対応付け、全体補正前後の座標合成を整理してから、設定・CLI・レポートへ接続する。`rows.carry_enabled`の読込、無効時の既存経路維持、全体事前検証後の最終比較／保存、出力失敗時の旧結果保護、件数と終了コード、raw evidenceの独立性を検証する。

今回の確認はCoreと固定入力の範囲。CLI全工程のピークRSS・再描画・保存時間、Windows、保留中のブラウザー確認は受け入れていない。複数原因の分離と数値変更を含む集約も未成立のまま残す。
