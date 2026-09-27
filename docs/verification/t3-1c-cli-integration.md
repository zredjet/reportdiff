# T3-1c 設定・CLI・レポート接続の検証

2026-09-22、macOS arm64／.NET 10.0.12／OpenCV 4.13.0。**限定したページ送りを設定・実CLI・JSON／HTML・compare-dirへ接続し、固定76ケースの結果とraw evidenceを確認した。** T3-1c全体の受け入れ・コミットは未完了。[接続設計](../planning/t3-1c-cli-integration.md)、[機械可読記録](t3-1c-cli-integration.json)、[利用設定](../CONFIGURATION.md#隣接ページへの送りと文書集約)を参照。

## 結果

| 対象 | 結果 |
|---|---|
| 新規テスト | 設定6件・CLI27件、計33件成功 |
| 全テスト | 1,806件成功、失敗・省略0、163.133秒 |
| Releaseビルド | Tests／PageFlowProbeとも警告・エラー0 |
| 実CLI | 固定76ケース×送り無効／有効、計152プロセス成功 |
| 送り採用 | 24ケース採用／52ケース見送り。前段階の全体採否と一致 |
| 原因集約 | 18ケース集約／58ケース見送り。独立結果の件数・網羅性と一致 |
| 無効時 | 既存の生差分・クラスタ・件数・状態と一致。送り用の追加画像なし |
| 見送り時 | 有効／無効で全ページDTOが一致 |
| raw evidence | 全76ケースで元A/B・確認用オーバーレイPNGがバイト単位で一致 |
| 入力固定 | 保存済みの68PDFと観測／候補／独立集約JSONのSHA256を確認 |
| 出力整合 | 元帯画像の寸法・元座標、実構造IDへの参照、HTMLの対応アンカーを確認 |
| 全工程の計測 | 4条件×有効／無効×3回、24子プロセス |

最初の全テストでは、既存のJSONキー一覧の検査に新しい `carry_enabled` が未反映で1件失敗した。許可するキー一覧へ追加し、無効時の値false・集約項目nullの検査も加えて全件再実行した。画像の検出期待や数値しきい値は変更していない。破損画像の負例で出るOpenCVの読込エラーは意図した入力拒否であり、最終テストの失敗は0件。

固定ケースはType3元24・追加12・Skia絶対座標28・Skia行内座標6・複数原因等6の計76。各組の入力先は[前段階の表](t3-1c-core-pipeline.md#再現用の入力)を維持した。生成時の正解帯・保存写像・構造IDを製品へ渡さず、実PDFと設定・ページ選択だけで実行した。今回は参照式を変更せず、既存の独立結果と実CLIを照合した。

## 検出・集約・見送り

- R10は5→1、R11は6→1、3ページ連鎖は8→1。A/B逆順も成立する。R11の片側ページは未比較・網羅性falseのまま。
- 対応本文・挿入境界の色変更は内容クラスタを残して6→2。
- 第二の挿入は7→7、第二の挿入＋色変更は8→8。送り帯を確認できても複数原因を一つへまとめない。
- 数値555→556は送りを見送り、従来の9件を維持する。
- 反復行、固定部分不足、送り帯の画素差、絶対座標罫線の実差、本文端の1px超過、非隣接の選択は従来の見送りを維持する。

新規テストでは `max_segments`、`min_support_ink_mm2`、`min_score_gap`、`min_support_bands`、`min_improvement` を厳しくして、範囲成立後にも文書全体を見送ることを確認した。採用条件の共有によって既存の行整列の結果を変えないことは、全回帰とCLIの無効時照合で確認した。

ページ2にだけ比較領域を指定する例では、送り先の実効条件を含めて元ページ置換比較が12→16となり、JSONに両端の設定を保存する。`--no-regions` でもcarryの値は維持する。共通YAMLのrows有効化と帳票別YAMLのcarry有効化は継承でき、最終設定でrowsが無効ならエラーになる。

全体補正の採用例では送りを `global_alignment_applied` で見送り、補正量(-6, 4)px・相違3件・全ページDTO・raw overlayは送り無効時と一致した。これは補正と送りを同時採用する座標合成の合格ではない。

画像入力、文字数上限、サイズ差、ページ選択、compare-dirも確認した。最終ページの送り帯PNG保存先へ障害を注入し、終了コード2・旧結果保持・一時出力削除を確認した。元画素変更は既存のCore回帰で処理エラーを確認し、CLIは最終保存前に入力ファイル全体のハッシュも再確認する。

## 時間・メモリ・出力量

固定の999×1250px、300dpi、2〜3ページの合成PDF。送り有効／無効を交互に各3回、新しいdotnet子プロセスで実行した。raw overlayとHTMLを有効にし、起動・読込・描画・比較・保存をすべて含む。計測中にテストや別の比較を同時実行していない。時間は中央値、RSSは3回の最大値、出力量は中央値。RSSはmacOSの `wait4.ru_maxrss`。

| ケース | 時間 秒（無効→有効） | 最大RSS MiB（無効→有効） | 出力 KiB（無効→有効） |
|---|---:|---:|---:|
| R10 | 0.657→0.868 | 263.5→283.6 | 499.4→816.3 |
| R11 | 0.682→0.923 | 243.9→278.7 | 591.3→945.9 |
| 3ページ連鎖 | 0.727→1.012 | 266.0→272.9 | 757.2→1,248.9 |
| 数値変更で見送り | 0.666→0.792 | 246.6→240.5 | 499.5→517.8 |

読込箇所と固定ケースの経路から数えた描画呼出し回数は、R10が4→18、R11が5→20、連鎖が6→28、数値変更の見送りが4→14。これは計測用カウンターの実測ではなく、記述収集・候補両端・写像構築・採用評価・最終比較の呼出し数を足した値。実描画の内部キャッシュ効果は評価していない。

全体事前検証のため有効時は再描画と比較が増え、C/D画像・元帯・詳細JSONの保存量も増える。RSSはプロセスの最大値で、記述の保持量や常駐量と同義ではない。小さな合成PDFの結果であり、A4実帳票やページ数上限の全工程の性能受け入れには使わない。

## 再現と成果物

`tests/ReportDiff.Tests/Fixtures/page-flow` の6組12PDFをテストへ固定し、SHA256を毎回検証する。残る固定コーパスは従来どおり `out/t3-1c-probe` にあり、Gitには含めない。

```sh
dotnet build tests/ReportDiff.Tests/ReportDiff.Tests.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll -result-xml out/t3-1c-cli/reproduce-tests.xml
python3 tools/ReportDiff.PageFlowProbe/check-connected.py out/t3-1c-cli/reproduce-matrix
python3 tools/ReportDiff.PageFlowProbe/measure-connected.py out/t3-1c-cli/reproduce-measure
dotnet src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll compare tests/ReportDiff.Tests/Fixtures/page-flow/R10/a.pdf tests/ReportDiff.Tests/Fixtures/page-flow/R10/b.pdf --config examples/page-flow.yaml --out out/t3-1c-cli/reproduce-R10
```

各出力先は未使用の名前にする。差がある比較の正常終了コードは1。今回の成果物は `out/t3-1c-cli/matrix-final/connected.json` と個別JSON／HTML／PNG、`measure-final/summary.json`、`all-tests-final.xml`。機械可読記録に入力・コード・検証成果物のハッシュと集計を保存した。

## 残件と再開位置

設定・CLI・Reportを変更したため、以前の「81製品ファイル不変」は現在の主張にしない。過去の検証記録・ハッシュは保存したまま、今回の接続範囲を別記録にした。READMEと設定ガイド・SPEC 11.8・TASKSに現在の限定対応を反映した。

次はA4・300dpiとページ数／密度を増やした入力で、CLI全工程の追加描画・時間・RSS・保存量を確認する。全体補正と送りの座標合成、複数原因の分離、数値変更を含む因果集約は別の未成立項目として残す。Windows実機・実帳票、保留中の画面サイズ／JavaScript確認は未実施。HTMLの静的検査をブラウザー操作確認として扱わない。T3-1c全体は未完了・未コミット。
