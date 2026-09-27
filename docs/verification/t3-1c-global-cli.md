# T3-1c 縦方向の全体補正と送りの製品接続

2026-09-23。承認された[接続案](../planning/t3-1c-global-composition.md#製品接続へ進むための案)をCore・CLIへ実装した。全選択ページが対応し、採用された全体補正が縦方向のみの場合に、送りの証明・内容比較・文書集約を行う。既定の採用条件・しきい値・設定キーは変更していない。数値・入力／ソースSHA256は[検証記録](t3-1c-global-cli.json)に保存した。

## 実装した契約

- 元画像Oの識別子・寸法・BGR SHA256は `PageFlowOriginalIdentity`、補正後Gの本文・ラスタ行記述は `PageFlowPageDescriptor` に分離。Oの本文と行ハッシュは重複保持しない。補正したBの追加識別情報・写像を512Bとして既存64MiBの記述予算へ加算する。
- 推定したGの候補をOへ逆写像し、全幅・同じ高さ・クリップなしを確認する。Oの全画素一致と元ページへの帯置換・両順序比較を従来どおり行う。除外は証明に使用しない。`Links` はGの論理端点、同じ順序の `OriginalLinks` はOの証明端点。
- Cの比較と採用改善率、行の相対移動量、因果集約はGで扱う。Dの実構造IDからGの集約範囲を作り、レポート用のOの元矩形と混同しない。構造／内容クラスタの元矩形、送り帯の座標／PNGはO。
- CLIは事前検証した補正を最終比較でも使用する。元画像再読込のSHA、Gの比較画像、終了前の入力PDFのSHAを検査する。変更や保存失敗は処理エラーとし旧出力を保持する。
- 横補正、補正と片側ページの組合せは文書全体を `global_alignment_applied` で見送る。補正なしの送り・片側ページは維持。文字はO→G→Dを一度ずつ通し、除外／領域へBの補正を重ねない。raw A/B・raw overlayはOのまま。

製品99ファイルのうち変更は8ファイル。比較コアの判定・全体補正の推定アルゴリズム・ネイティブ描画・依存ライブラリは変更していない。

## 実PDFとCLIによる検証

`create-global-pdfs.py` で自作Type3の既存入力に上下余白と固定模様、入力の移動を**ベクターPDF命令**として加えた26PDFを固定した。画像へ変形した前段の事前検証とは異なり、実CLIがPDFを描画し、文字を抽出し、全体補正を既定条件で推定してから送りを採用する。正解座標・移動量を製品へ渡していない。実描画は999×1762px、本文の行間隔は100px。固定模様で全体補正の支持を確保した合成対照であり、通常の実帳票やA4の受け入れではない。

実CLIの17条件・旧新68プロセスを確認した。

| 条件 | 送り | ページ別内訳 → 集約後 |
|---|---|---|
| ゼロ補正、下4px、上6px、ページ別4/-6px、0/4px混在、A/B逆方向 | 採用 | 5 → 1 |
| 3ページ連鎖、入力dy=4/-6/3px | 採用 | 8 → 1 |
| 本文の10×10px灰色変更、同変更と厳密領域 | 採用、内容クラスタを保持 | 6 → 2 |
| 本文変更のA基準除外 | 採用 | 5 → 1 |
| 横補正、両軸補正、補正と片側ページ | 全体見送り | 既存件数を維持 |
| 帯変更、帯変更と除外 | 全体見送り | `nonidentical_band_not_proven` |
| 端の254画素、半ピクセル移動 | 全体見送り | 既存の全体補正採用条件と固定部分の条件を維持 |

採用10／見送り7。採用21ページのC/D画像を、保存した元画像OとJSON写像から独立に再構成した。直接O→C/DとO→G→C/Dが全画素一致し、Dは元の非白画素数を保持した。53構造と内容クラスタ断片の元矩形を監査し、26枚の送り帯PNGを元画像の切り出しと照合した。採用された11帯組は左右の元画素が一致する。既定条件のCの17ページはPython参照で生差分数・クラスタ数が一致した。除外・領域の2条件は別の座標監査とCLIテストで確認した。PDFの元描画サンプルも画像で確認済み。

新旧バイナリで、送り無効の全17条件、補正量0と横／両軸／片側の従来見送り4条件のJSON・PNG・HTML計916ファイルが生成日時を除き一致した。全17条件のraw画像108組は送り有効／無効で全バイト一致。新たに帯検証まで進む見送り例では、`page_flow` に検証理由・証拠帯が追加される一方、ページ結果は旧製品および送り無効時と同一だった。

既存76ケース・有効／無効152プロセスも成功し、採用24／見送り52、単一原因の集約18／見送り58を維持した。

## 回帰と失敗時の保護

新規23件を含む全1,829テストが成功（失敗・スキップ0）。ビルドは警告・エラー0。新規回帰は実PDFの正負／混在／逆転／連鎖、元帯PNGと構造参照、行相対dy、文字注釈、本文変更と除外／領域、全体見送り、compare-dir、入力PDF変更と保存失敗の旧出力保護を含む。

CoreではOをGの比較画像として渡す、Gを元画像として再読込する、元画像を変更する負例を拒否。Oへ戻した端点のクリップも拒否し、追加識別情報の予算計上を確認した。入力変更・保存失敗時には部分結果やステージングを残さない。破損PNG試験のネイティブログは従来どおり出るが、テストはすべて成功した。

## 全工程の計測

macOS arm64、.NET 10.0.12、300dpi。raw overlay・PNG・JSON・HTMLを含む製品18プロセス、各条件3反復の中央値。RSSは `wait4.ru_maxrss` の最大値。別の計測コピー6プロセスで段階時間・描画回数を採取し、製品と生成日時以外の全出力が一致した。製品コードへ計測を残していない。暖機なし、OSキャッシュが冷たいとの主張はしない。

| 入力 | 送り | 中央秒 | 最大RSS MiB | PDF描画回数 | 全体補正推定回数 |
|---|---|---:|---:|---:|---:|
| 2ページ | 無効 | 1.099 | 290.2 | 4 | 2 |
| 2ページ | 有効・採用 | 1.447 | 320.0 | 18 | 2 |
| 3ページ連鎖 | 無効 | 1.370 | 297.1 | 6 | 3 |
| 3ページ連鎖 | 有効・採用 | 1.892 | 352.3 | 28 | 3 |
| 帯変更 | 無効 | 1.110 | 288.4 | 4 | 2 |
| 帯変更 | 有効・見送り | 1.633 | 304.5 | 14 | 4 |

採用時は最終比較で全体補正を再推定しない。見送り時は既存の最終比較を維持するので再推定を行う。2ページの事前検証は計測コピーで約1.007秒、3ページは約1.384秒。階層タイマーはinclusive値のため親子を加算しない。記述予算64MiBはRSS上限ではない。

計測コピーの初回復元はオフラインのNuGet脆弱性情報取得に失敗したため、そのコピーのビルドだけ `-p:NuGetAudit=false` でキャッシュ済み依存を使用した。製品の設定・依存は変更していない。

## 再現と残件

```sh
dotnet build tests/ReportDiff.Tests/ReportDiff.Tests.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll -result-xml out/t3-1c-global-cli/all-tests.xml
python3 tools/ReportDiff.PageFlowProbe/check-connected.py out/new-legacy-connected
# pypdfで固定PDFを再生成、Pillow/NumPyでCLIの座標を独立監査する
python3 tools/ReportDiff.PageFlowProbe/create-global-pdfs.py out/new-global-inputs
python3 tools/ReportDiff.PageFlowProbe/check-global-cli.py out/new-global-check out/t3-1c-global-cli/baseline-cli/reportdiff.dll
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-global-cli-reference.py out/new-global-check
python3 tools/ReportDiff.PageFlowProbe/instrument-scale.py out/new-global-instrumented
dotnet build out/new-global-instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj -c Release --disable-build-servers -m:1 --ignore-failed-sources -p:NuGetAudit=false
python3 tools/ReportDiff.PageFlowProbe/measure-global-cli.py out/new-global-perf out/new-global-instrumented/src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll
```

現証拠は `out/t3-1c-global-cli/`、固定PDFは `tests/ReportDiff.Tests/Fixtures/page-flow-global/`。旧バイナリの比較は同ディレクトリに退避した変更前のCLIを使用する。

[A4未成立9条件](t3-1c-scale-measurement.md)、横補正と補正あり片側ページ、複数原因・数値変更の因果集約、Windows・実帳票・大規模性能の受け入れは残す。利用者が保留したブラウザー／JavaScript確認は再開していない。次の検討候補は複数原因の因果集約の設計・独立検証。T3-1c全体は未完了、コミット・プッシュ・リリースは行っていない。
