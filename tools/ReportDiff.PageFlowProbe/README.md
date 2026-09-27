# ページ送りの事前検証

T3-1cのページ送りを段階別に検証するツール。事前検証の独立実装、Coreとの照合、接続後の実CLI検証を含む。[仕様](../../docs/planning/t3-1c-page-flow.md)と[観測結果](../../docs/verification/t3-1c-page-flow-probe.md)を参照。

## 元ページ付き内容面のCore実装（A2）

`AnchoredComparisonTests` の6方向から画像と集計を保存し、`verify-anchored-core.py` でPython比較コア・独立座標表と照合する。C/D画像48枚と生差分24マスクが対象。元の2方向は固定PDF、色変更と文脈差分の4方向はメモリ上の画像対照であり、別PDFの受け入れへ流用しない。

```sh
dotnet build -c Release --no-restore --disable-build-servers -m:1
REPORTDIFF_ANCHORED_EVIDENCE="$PWD/out/new-anchored-core/evidence" \
  dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll \
  -parallelMode none -class '*AnchoredComparisonTests*' \
  -result-xml out/new-anchored-core/tests.xml
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/verify-anchored-core.py \
  out/new-anchored-core/evidence
```

画像出力には絶対パスを指定する。CLIへの接続、JSON／HTML、保存失敗時の公開制御はA3。[Coreの検証記録](../../docs/verification/t3-1c-anchored-comparison.md)を参照。

## 末尾の片側ページへ続く共有原因の診断

`--unpaired-shared-diagnosis`は別合成14ケース28PDFを作り、旧入力の支持不足と、支持を満たす別入力の曖昧候補を分ける。診断用の候補推定と製品計画を別々に実行し、各段階の成立を記録する。C/Dと採用には既存関数を使い、原因集約は `shared_cause_model.evaluate(input, terminal=True)` で独立に検査する。既定のモデルは従来どおり全ページ対応を要求する。

```sh
dotnet build tools/ReportDiff.PageFlowProbe -c Release --no-restore --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll \
  --unpaired-shared-diagnosis out/new-unpaired-shared/diagnosis
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-unpaired-shared.py \
  out/new-unpaired-shared/diagnosis
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll \
  --unpaired-shared-inference-replay out/t3-1c-shared-diagnosis/complete/diagnosis.json \
  out/new-unpaired-shared/inference-replay.json
```

出力は新規パスを使う。Python照合は28方向の実CLI56プロセス、C/Dの再構成と参照マスク、残余、モデルの破損監査を行う。限定接続前の製品CLIは新規6方向を10／16件のまま見送っていた。接続後は固定された診断の8→2／11→2／9→3へ照合する。`unpaired_shared_audit.py <診断ディレクトリ>`は境界推定と別対照の3,570px／30px残余、既存モデル114入力を追加照合する。この追加監査には、親ディレクトリの `shared_cause_model_before.py`（変更前に保存したモデル）が必要で、現モデルのコピーを変更前の証拠にしない。

結果と成立範囲は[診断記録](../../docs/verification/t3-1c-unpaired-shared-diagnosis.md)、製品接続は[接続記録](../../docs/verification/t3-1c-unpaired-shared-integration.md)と[証拠・出力・予算契約](../../docs/planning/t3-1c-unpaired-shared-diagnosis.md)を参照。旧入力の未成立2方向、Windows等の未検証を保持する。

### 同じ連鎖の共有原因と末尾ページの製品接続

`--unpaired-shared-product-audit`は固定6方向を製品APIだけで実行し、診断の全実構造・内容件数・採用条件と予約量を検査する。`PageFlowPlan`の生成には反射を使わない。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll \
  --unpaired-shared-product-audit tests/ReportDiff.Tests/Fixtures/page-flow-terminal-shared \
  out/new-unpaired-shared-product-audit.json
```

`verify-unpaired-shared-integration.py <記録ディレクトリ> fixed|legacy|directory`は実CLIを実行する。`fixed`は固定28方向を独立モデルの原因・移動・送り・収支・補助端点と照合し、接続前の `out/t3-1c-unpaired-shared-diagnosis/diagnosis` の保存結果から6方向だけが変わることを確認する。`legacy`は前工程の `out/t3-1c-unpaired-integration/fixed` にある旧32方向の有効／無効出力と日時以外の一致を検査する。`directory`は5対の一覧・子レポート・静的HTMLリンクを照合する。

既存の他機能58方向は `verify-unpaired-integration.py <記録ディレクトリ> compat` を再利用する。接続前の実行ファイル一式を `<記録ディレクトリ>/baseline-cli/` に保存しておく。`measure-unpaired-shared-integration.py <記録ディレクトリ>`は同じ旧CLIを使って6方向・3モード・3反復の54プロセスを逐次計測し、元画像と反復結果の一致も検査する。重い検証が終わってから実行する。保存済み結果・旧CLIを現製品で置き換えて互換の代用にしない。

## 末尾の片側ページと独立原因の診断

`--unpaired-components`は16ケース32PDFを別生成し、片側ページの残余・非送り境界・独立原因を両方向で検証する。接続前の製品は新規8方向を19→19のまま見送っていた。限定接続後は、診断上の11→2／12→3と文書に結合した製品計画の `product_aggregation` を別に保存する。証拠を持たない汎用集約APIの `legacy` は従来どおり見送る。`check-unpaired-components.py`はPython参照・元画素からのC/D再構成・全非白残余を確認し、送り有効／無効の実CLI64プロセスを製品計画へ照合する。

```sh
dotnet build tools/ReportDiff.PageFlowProbe -c Release --no-restore --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll \
  --unpaired-components out/new-unpaired-components/diagnosis
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-unpaired-components.py \
  out/new-unpaired-components/diagnosis
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll \
  --unpaired-audit out/new-unpaired-components/diagnosis/unpaired-components.json \
  out/new-unpaired-components/audit.json
```

出力先は空の新規ディレクトリ。再読込監査のJSONも新規パスを指定する。診断でのみ反射を使って既存写像のゲートを再評価するため、このツールを製品の証拠型・予算・保存検証の代用にしない。[結果](../../docs/verification/t3-1c-unpaired-components.md)と[限定接続案](../../docs/planning/t3-1c-unpaired-components.md)を参照。

### 限定した末尾ページの製品接続

`--unpaired-product-audit`は固定PDFから製品計画・比較・採用・集約を実行し、8方向の全グループを診断の期待へ照合する。文書・非送り・成分の記述予約量と実生差分も記録する。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll \
  --unpaired-product-audit tests/ReportDiff.Tests/Fixtures/page-flow-unpaired \
  out/new-unpaired-product-audit.json
dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll \
  -parallelMode none -method '*Terminal*'
```

`verify-unpaired-integration.py <記録ディレクトリ> fixed|compat|directory`は実CLI出力を検査する。`fixed`は接続前の `out/t3-1c-unpaired-components/diagnosis` の入力／保存結果、`compat`は記録ディレクトリ内の `baseline-cli/` に保存した接続前CLI一式が必要。固定入力32方向の追加採用8方向以外の互換、既存58方向の全出力、フォルダー比較3対を別々に確認する。旧実行ファイルを現バイナリで代用しない。

`measure-unpaired-integration.py <記録ディレクトリ>`も同じ旧CLI一式を使い、旧有効／新無効／新有効を8方向・3反復で計測する。`measure/`等の各出力先は新規とし、重い検証と同時に実行しない。記述予約とRSSの違い、成立範囲と残件は[製品接続の検証記録](../../docs/verification/t3-1c-unpaired-integration.md)を参照。

## 実行

リポジトリのルートで実行する。各出力先は空であること。既存の成果物は上書きしない。

```sh
dotnet restore tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll out/t3-1c-probe/reproduce
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-reference.py out/t3-1c-probe/reproduce
python3 tools/ReportDiff.PageFlowProbe/check-cli.py out/t3-1c-probe/reproduce out/t3-1c-probe/reproduce-cli
```

Python参照照合には既存のOpenCV・NumPy環境を使う。上の`out/t2-4a/reference-env/bin/python`は検証機にある環境で、配布物には含まない。別環境では同じ依存を持つPythonのパスに置き換える。製品の依存追加はない。

## 入力と観測

- 自作Type3フォントを埋め込んだ決定的なPDF。OSフォントは使わない。字形は文字コードから作る試験用の図形で、一般の英文の読みやすさを評価する入力ではない。
- R10（2→2ページ）、R11（2→3ページ）、3ページ連鎖、送り帯の文字変更／同文の色変更／帯端の濃淡変更／帯の外側の変更、新ページの余分な内容、反復行、フッターなしの10組。
- 既知の送り帯は、入力生成時の独立した行IDから与える**正解位置**。自動検出したリンクではない。文字層は実PDFから抽出して照合する。
- 各帯をnormal／strict、周辺0／20／40px、A/Bの両順序で比較する。生差分全体と帯内の差分を分けて保存する。除外・白塗り・設定の緩和は行わない。
- `SurfaceProbe`も正解IDを与える対照実験。既存の内容比較面Cが構築できるかを確認し、帯を外した後の内容を比較する。送り帯そのものの一致や構造認定の証明には使わない。変更のある帯でもCから外せてしまうため、Cの差分0だけで送りを確定してはならない。
- CLI照合は現行のrows無効／有効、両方向の基準出力を記録する。終了コード・件数・未比較・raw evidenceを確認する。この初期照合時点では自動送り／文書集約は未接続だった。接続後の検証は末尾の「製品CLI接続」を参照。

`observations.json`は文字・座標・写像・差分、`reference.json`はPythonとの照合、CLI出力先の`cli.json`は40実行の要約。PDF・元画像・切り出し・生差分マスク・C画像は各ケースのディレクトリに保存する。

## 元ページの周辺を保持する追加検証

既存の`observations.json`とPDFを読み込み、入力のSHA-256と既知の帯座標を固定して試行する。追加のPDFは生成しない。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --context out/t3-1c-probe/reproduce out/t3-1c-probe/reproduce-context
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-context.py out/t3-1c-probe/reproduce-context
```

送り元／先の元ページをそれぞれ複製し、候補帯だけを相手の元画素で置換する。両端・比較順交換・normal／strict／looseの全ページ比較と、切り出した帯だけの対照を保存する。長い罫線・境界の濃淡／薄色／色変更・単独の2px移動・1画素差も加え、計37入力・750比較となる。

四比較が0だけでは安全ではない。境界をまたぐ薄い塗りは、分割前に44pxの差分があっても個別では0になる。`exact_band_evidence`は帯の**全画素一致**も要求する限定案であり、自動的な`carried`ではない。微小移動が通常比較で吸収されても、この案では送りは未確定のままにする。[補足案](../../docs/planning/t3-1c-context-verification.md)と[追加検証](../../docs/verification/t3-1c-context-verification.md)を参照。

`context.json`に全測定と限定案の結果、`context-reference.json`に参照照合を出す。検証用置換画像はraw evidenceとは異なる。元画像は変更せず、C#／Pythonの両方で置換帯の内外を検証する。

## 文字候補と構造帯の自動推定

`--candidates`は文字層・画素から帯を推定し、生成時の正解位置を読み込まない。正解は後続のPython検査でのみ照合する。`--candidate-fixtures`は元20PDFの再生成ハッシュが不変であることを確認したうえで、本文位置／間隔の変更、1→2ページ、対応本文／挿入境界の変更を含む追加12PDFを作る。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --candidates out/t3-1c-probe/reproduce out/t3-1c-probe/reproduce-candidates
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-candidates.py out/t3-1c-probe/reproduce out/t3-1c-probe/reproduce-candidates
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --candidate-fixtures out/t3-1c-probe/reproduce out/t3-1c-probe/reproduce-candidate-fixtures
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --candidates out/t3-1c-probe/reproduce-candidate-fixtures out/t3-1c-probe/reproduce-candidate-additional
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-candidates.py out/t3-1c-probe/reproduce-candidate-fixtures out/t3-1c-probe/reproduce-candidate-additional
```

`candidates.json`は入力から推定した本文・送り帯・D/Cの範囲、帯画像と構造範囲の個別の証拠、未比較ページの残る非白画素を記録する。元ページPNG・比較面・生差分マスクも保存する。`candidate-reference.json`はPythonで再構成した写像、元画素被覆、参照比較の照合結果。両方向32実行と選択ページ4実行、文書単位の確定に使う基準36比較も含め計516参照比較となる。独立ツールの入力上限は各16ページで、超過時は描画前に終了する。製品の上限ではない。

`band_verified`は全画素一致の画像条件だけ。`range_correspondence`の両端が確認できないリンクは構造認定されていない。125px間隔の一部末尾では罫線が1px長く描画され、元画素を切らず比較面を見送る。新しい境界変更60pxの検出、反復行の見送り、片側ページの帯外3,570px、飛び飛びの選択を保存した。[限定範囲の検証結果](../../docs/verification/t3-1c-candidate-inference.md)を参照。自動送り・集約の製品への接続ではない。

## Skia入力と文書単位の確定

自作TrueTypeをSkiaSharpで埋め込む。OSフォント・追加パッケージは使わない。ページの絶対座標で描く24PDFと、行内座標を共通にした対照6PDFを別々に生成する。前者の罫線差を後者へ置き換えて消さない。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --skia-fixtures out/t3-1c-probe/reproduce-skia
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --skia-local-fixtures out/t3-1c-probe/reproduce-skia-local
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --candidates out/t3-1c-probe/reproduce-skia out/t3-1c-probe/reproduce-skia-candidates
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --candidates out/t3-1c-probe/reproduce-skia-local out/t3-1c-probe/reproduce-skia-local-candidates
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-candidates.py out/t3-1c-probe/reproduce-skia out/t3-1c-probe/reproduce-skia-candidates
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-candidates.py out/t3-1c-probe/reproduce-skia-local out/t3-1c-probe/reproduce-skia-local-candidates
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --skia-diagnostics out/t3-1c-probe/reproduce-skia-candidates out/t3-1c-probe/reproduce-skia-diagnostics
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-skia-diagnostics.py out/t3-1c-probe/reproduce-skia-diagnostics
```

すべての`--candidates`実行で、`gate`と`selected_masks`も保存する。レイアウト・候補・全対応ページの写像・両端範囲のどれかが欠ければ、文書全体を基準比較へ戻す。成功した比較面も診断用に残すが、選択リンクは0件になる。`gate.ready`は範囲の確定であり、因果集約や製品の採用ではない。

候補とページの列挙順、根拠を一つ失った場合の採否も検査できる。上記の4種類の候補出力（Type3元入力、Type3追加、Skia絶対座標、Skia行内座標）それぞれに実行する。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --gate-audit out/t3-1c-probe/reproduce-candidates
```

合計70実行・878参照比較・210順序条件・160根拠欠落条件。[結果と制約](../../docs/verification/t3-1c-skia-document-gate.md)を参照。Skiaの画素が異なる罫線は通常比較でも検出され、全画素一致の条件を緩めていない。入力のPDFハッシュをmanifestへ保存し、再実行中の変更を拒否する。

## 単一原因の収支と実構造IDの集約

`--aggregate`は保存済み候補のD/Cと元画像を読み、既存コアの実構造ID・内容クラスタを得た後、文書全体の単一原因を検査する。生成時のケース名・正解IDは集約器へ渡さない。製品内部の比較／表示投影を呼ぶリフレクションはこの独立ツールだけで使う。既存のCLI出力や製品APIの変更ではない。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --causal-fixtures out/t3-1c-probe/reproduce-causal-fixtures
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --candidates out/t3-1c-probe/reproduce-causal-fixtures out/t3-1c-probe/reproduce-causal-candidates
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-candidates.py out/t3-1c-probe/reproduce-causal-fixtures out/t3-1c-probe/reproduce-causal-candidates
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --aggregate out/t3-1c-probe/reproduce-causal-candidates out/t3-1c-probe/reproduce-aggregate-causal
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --aggregation-audit out/t3-1c-probe/reproduce-aggregate-causal
```

既存4種類の候補出力（`reproduce-candidates`、`reproduce-candidate-additional`、`reproduce-skia-candidates`、`reproduce-skia-local-candidates`）にも`--aggregate`と`--aggregation-audit`を実行し、それぞれの出力先を`reproduce-aggregate-fixed`、`reproduce-aggregate-additional`、`reproduce-aggregate-skia`、`reproduce-aggregate-skia-local`とする。その後、全76実行をまとめて照合する。

```sh
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-aggregation.py out/t3-1c-probe/reproduce-aggregate-fixed out/t3-1c-probe/reproduce-aggregate-additional out/t3-1c-probe/reproduce-aggregate-skia out/t3-1c-probe/reproduce-aggregate-skia-local out/t3-1c-probe/reproduce-aggregate-causal
```

`aggregation.json`に集約器の入力、実構造／投影クラスタ、有効／無効の結果を保存し、`pN-display-raw.png`にD生差分を出す。`aggregation-audit.json`は列挙順・欠落・重複・除外・端点や移動量の変更・網羅性の検査結果。最後の出力先の`aggregation-reference.json`はPythonによる編集列、固定件数、参照集合、C→Dマスクの独立照合。76実行・52表示面・1,770監査条件を確認した。[結果と未成立の範囲](../../docs/verification/t3-1c-causal-aggregation.md)を参照。

## Core接続基盤と内部上限

`--foundation`は保存候補の元PNGと固定PDFの文字を読み、Coreの`PageFlowCollector`が保持する本文・座標・非白行・行ハッシュを独立ツールと照合する。Coreの範囲ゲートについて、従来の文書採否・選択リンク・ページごとの経路を比較する。これは候補生成や画像検証、因果集約をCoreへ移したことを意味しない。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --foundation out/t3-1c-probe/reproduce out/t3-1c-probe/reproduce-candidates out/t3-1c-foundation/reproduce-fixed.json
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --foundation out/t3-1c-probe/reproduce-candidate-fixtures out/t3-1c-probe/reproduce-candidate-additional out/t3-1c-foundation/reproduce-additional.json
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --foundation out/t3-1c-probe/reproduce-skia out/t3-1c-probe/reproduce-skia-candidates out/t3-1c-foundation/reproduce-skia.json
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --foundation out/t3-1c-probe/reproduce-skia-local out/t3-1c-probe/reproduce-skia-local-candidates out/t3-1c-foundation/reproduce-skia-local.json
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --foundation out/t3-1c-probe/reproduce-causal-fixtures out/t3-1c-probe/reproduce-causal-candidates out/t3-1c-foundation/reproduce-causal.json
python3 tools/ReportDiff.PageFlowProbe/measure-foundation.py out/t3-1c-foundation/reproduce-measure
```

計測は12条件×3子プロセス。`measure-foundation.py`はmacOS/Linuxの`wait4`から各プロセスの最大RSSを取得する。C#側の時間は記述処理または候補ゲートだけで、PDF描画・画像比較・保存を含まない。単独条件は`--foundation-measure a4-2 <output.json>`などで実行できる。[上限・型・後続接続の設計](../../docs/planning/t3-1c-integration-foundation.md)と[全76実行の照合結果](../../docs/verification/t3-1c-integration-foundation.md)を参照。

## 独立ツール段階で未実施だった範囲

今回の一定行間隔以外の境界、ページ両端で反復する本文と固定部分の一般的な区別、複数原因のグループ分割、文字変更を含む集約、領域・補正・CLI選択範囲・製品上限、保存失敗時の復旧、性能、ブラウザー、Windowsはこの独立試行の合格範囲に含まない。後続で確認した範囲は下記のCLI接続記録を参照。

## Core移行の照合

`--core-replay`は固定PDFのハッシュを確認し、元PNGとPDFのテキストからCoreの記述収集・候補推定・画像検証・全体範囲確定を実行する。正解帯、保存写像、構造IDはCoreへの入力にしない。結果を独立ツールの候補・C/D・実構造・集約と照合する。`--core-aggregation-audit`は保存された因果入力を一つずつ欠落・重複させ、Coreの集約器で同じ保守的な見送りを検査する。既存の独立推定器と集約器は比較対象として残す。

```sh
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --core-replay out/t3-1c-probe/type3-final out/t3-1c-probe/gate-fixed out/t3-1c-probe/aggregate-fixed out/t3-1c-core/reproduce/fixed.json
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --core-aggregation-audit out/t3-1c-probe/aggregate-fixed out/t3-1c-core/reproduce/fixed-audit.json
```

上記に加え、追加Type3・Skia絶対座標・Skia行内座標・複数原因の5組で76実行を照合する。入力先と対応する保存先は[Core移行の検証記録](../../docs/verification/t3-1c-core-pipeline.md)を参照。今回の移行では新しいPDF入力やPython参照式を作らず、既存の参照一致済み生差分を比較する。CLIへの接続・利用者向け設定の有効化ではない。

## 製品CLI接続

`check-connected.py` は固定76ケースを実CLIで送り無効／有効の計152プロセスに渡す。Core移行時に使ったPDF・候補・独立集約の保存先をそのまま使うため、先に [再現入力表](../../docs/verification/t3-1c-core-pipeline.md#再現用の入力) の成果物が必要。PDFを再生成した場合は、既存の参照照合・ハッシュ確認を先に行う。

```sh
dotnet build tests/ReportDiff.Tests/ReportDiff.Tests.csproj --no-restore -c Release --disable-build-servers -m:1
python3 tools/ReportDiff.PageFlowProbe/check-connected.py out/t3-1c-cli/reproduce-matrix
python3 tools/ReportDiff.PageFlowProbe/measure-connected.py out/t3-1c-cli/reproduce-measure
```

候補・全体採否・集約件数と網羅性を独立結果と比較し、無効時の生差分・件数、見送り時の全ページDTO、元A/B・raw overlayのPNGバイト一致、実構造参照、HTMLの件数表示も確認する。成立済みケースが新たな採用条件で見送られた場合も失敗とし、期待を下げない。

`measure-connected.py` はリポジトリ内の固定PDF4組×送り有無×3回の24プロセスで、描画・比較・保存・HTMLを含む時間、`wait4`のピークRSS、出力バイト数を測る。テスト・別の計測と同時実行しない。999×1250pxの合成入力の測定であり、A4実帳票の性能受け入れではない。失敗時は既存出力を再使用せず新しい出力先へ実行する。

[接続後の検証結果と残件](../../docs/verification/t3-1c-cli-integration.md)を参照。接続時にCLI・Report・設定を変更したため、過去の「製品81ファイル不変」検査はこの段階の適合基準にしない。過去の記録とハッシュは変更せず残す。

### V-02：既存Skia入力の現行版再検証

`verify-skia-regression.py <未使用の出力先>` は固定コーパスのSkia絶対座標28条件・行内座標6条件を、送り有効／無効の計68プロセスで再実行する。入力30PDFを再生成せず、初回接続記録のPDF・候補・独立集約のSHA-256を照合する。旧 `out/t3-1c-cli/matrix-final/` の保存結果が必要で、現在の出力を旧結果の代用にしない。

```sh
dotnet build -c Release --no-restore --disable-build-servers -m:1
python3 tools/ReportDiff.PageFlowProbe/verify-skia-regression.py out/new-skia-revalidation
```

JSON／HTMLは生成日時だけを正規化し、全出力ファイルの集合・内容を旧出力へ照合する。元の採否・集約件数・網羅性の独立期待、送り有効／無効のraw画像、見送り時の全ページ結果、元帯画像と実構造参照も検査する。比較対象ページ内の既存件数の網羅性と、選択外を含む文書集約の網羅性を区別する。A4用紙9条件、ブラウザー操作、Windows受け入れは含まない。途中失敗で既存期待を変更せず、`verification.json`の`completed: true`だけを完了の証拠とする。

## A4・多ページの全工程計測

`ScaleFixtures` と `measure-scale.py` で、A4指定のSkia入力2／8／16／30／31ページ、本文48行、本文／送り帯の濃淡変更を測る。通常のA4入力で送りが成立するという期待と、実際の採否を別に保存する。未成立を成功扱いへ変更しない。描画端・分数寸法・整数pt寸法の対照、1ページ6行の疎な対照も別入力として保存する。[計測範囲](../../docs/planning/t3-1c-scale-measurement.md)を参照。

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --scale-fixtures out/t3-1c-scale/reproduce/inputs
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --scale-control-fixtures out/t3-1c-scale/reproduce/inputs-control
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --scale-grid-fixtures out/t3-1c-scale/reproduce/inputs-grid
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --scale-integer-fixtures out/t3-1c-scale/reproduce/inputs-integer
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --scale-sparse-fixtures out/t3-1c-scale/reproduce/inputs-sparse
python3 tools/ReportDiff.PageFlowProbe/instrument-scale.py out/t3-1c-scale/reproduce/instrumented
dotnet restore out/t3-1c-scale/reproduce/instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj --source out/packages -p:RestoreFallbackFolders="$HOME/.nuget/packages" -p:NuGetAudit=false --disable-parallel --disable-build-servers -m:1
dotnet build out/t3-1c-scale/reproduce/instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj --no-restore -c Release --disable-build-servers -m:1
python3 tools/ReportDiff.PageFlowProbe/measure-scale.py out/t3-1c-scale/reproduce out/t3-1c-scale/reproduce/measurements --instrumented out/t3-1c-scale/reproduce/instrumented/src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --scale-verify-legacy out/t3-1c-probe/type3-final
```

すべて未使用の出力先へ実行する。最終コマンドは既存Type3の20PDFをメモリ上で再生成し、保存済みSHA256と照合する。新しいFlowLayoutの省略値で過去の固定入力を変更していないことを確認する。

計測時にビルド・テスト・別の比較を同時実行しない。製品は原則3回、30／31ページと補助対照は1回。計測コピーは各条件1回で、製品の時間中央値へ混ぜない。準備実行やキャッシュ消去は行わず、初回も集計に含める。1回だけの値は中央値の一般化に使わない。

`verification.json` は全生データ、時間・RSS・出力量、採用期待の未成立、元画像／raw overlayの一致、生成日時以外の全出力一致を記録する。`checkpoint.json` は完了した各プロセスの記録。途中エラーでは新しい出力先で再実行する。計測コピーの段階時間には親子IDがあるため、親子のinclusive時間を合計しない。描画回数もコピーで実測する。製品にタイマーやファイル出力を追加しない。

計測後のPDF寸法・本文順・原画像の行画素・変更矩形は `audit-scale.py` で確認できる。検証環境のpypdf／Pillow／NumPyを使用し、製品の依存には追加しない。

```sh
python3 tools/ReportDiff.PageFlowProbe/audit-scale.py out/t3-1c-scale/reproduce
```

この監査は計測完了後に実行する。出力は `audit.json`。送りの採用期待を満たせない入力は、元PDF・元画像・実差分・理由を保存して残す。濃淡変更を含む2例は、変更矩形の中心を実クラスタが覆うことも検査する。

今回の[実測値と未成立の入力](../../docs/verification/t3-1c-scale-measurement.md)を参照。通常のA4と疎な対照を別に集計し、最大RSSは記述64MiBの上限保証として扱わない。

### R-02：固定A4入力の現行版での再現確認

`verify-a4-regression.py`は、上記計測の元入力・元期待をそのまま使い、未成立だった9条件と対照4条件を送り無効／有効で各1回、計26プロセスで再実行する。PDFは再生成しない。対照は疎な2／16ページ、31ページの画素上限、送り帯の濃淡変更であり、元の9条件の代わりの合格にはしない。

```sh
dotnet build -c Release --no-restore --disable-build-servers -m:1
python3 tools/ReportDiff.PageFlowProbe/verify-a4-regression.py out/new-a4-revalidation/measurements
python3 tools/ReportDiff.PageFlowProbe/audit-scale.py out/new-a4-revalidation
```

事前に`out/t3-1c-scale/`の固定26PDF・5種類のmanifest・過去の計測記録・元出力と、`out/t3-1c-unpaired-shared-integration/source-tested.json`を必要とする。旧記録のSHA-256と、全回帰時の製品・テスト・固定入力544ファイルを照合してから開始する。出力先は未使用のディレクトリとする。日時だけを正規化した旧新の全出力比較、元の採用・件数期待、送り有無のraw画像と見送り時のページ結果を保存する。

`completed: true`は再実行手順の完走を表す。R-02の解消は`target_expectations_met`／`target_expectations_unmet`を別に確認し、未成立の期待を成功へ書き換えない。JSON／HTML／PNGの旧新差分も別に保存する。時間・RSSは各条件1回の観測で、性能改善や性能目標の合格には使わない。後続の`audit-scale.py`はpypdf／Pillow／NumPyを備えたPythonで実行し、元PDFの寸法・本文順、原画像の行画素、濃淡変更の検出、疎な対照の構造と元帯を独立に照合する。[2026-09-27の再現結果](../../docs/verification/t3-1c-a4-revalidation.md)を参照。

## A4の描画差とC/Dの診断

前工程で固定した `out/t3-1c-scale/inputs*` と `measurements` を使う。PDFを再生成せず、未使用の診断出力先を指定する。診断用の寸法・アンチエイリアス対照と、採用前のC/Dを製品レポートへ流用しない。

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --raster-diagnostics out/t3-1c-scale out/t3-1c-raster-diagnosis/reproduce
python3 tools/ReportDiff.PageFlowProbe/audit-raster.py out/t3-1c-scale out/t3-1c-raster-diagnosis/reproduce
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-raster-reference.py out/t3-1c-raster-diagnosis/reproduce
```

`audit-raster.py` はpypdf／Pillow／NumPyを持つPythonで実行する。参照比較は既存のOpenCV環境を使う。製品依存は追加しない。既定描画・切り上げ・切り捨て・パスのアンチエイリアス無効を同じPDFで比較し、直接APIと製品読み取りの元画素を照合する。PDFiumは逐次実行する。

`diagnostics.json` に元画像・本文境界・送り候補・C/D写像・採用評価を保存する。範囲未成立の入力にはCを作らない。独立監査の `audit.json` は元入力／前工程PNGのハッシュ、配置を除いた命令・描画フォント、全対応行の画素差、Cの再構成とDの投影を検査する。SkiaのToUnicodeは追加文字で異なるため、同じCIDの埋込字形と幅を確認する。`reference.json` は4ページのCを既存Pythonで比較した結果。監査失敗時の既存JSONを成功記録に使わず、出力ログの終了コードも確認する。

今回の保存先は `out/t3-1c-raster-diagnosis/final/`。[診断結果と未成立の範囲](../../docs/verification/t3-1c-raster-diagnosis.md)を参照。

## 全体補正と送りの座標合成の事前検証

固定したType3のR10／3ページ連鎖PDFと、同じ場所の保存済み元PNGを使う。合成ラスタと文字座標を作る検証であり、元PDFを変更・再生成しない。全体補正と送りの既定値を維持し、未使用の出力先を指定する。

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --global-flow-preflight out/t3-1c-probe/type3-final out/t3-1c-global-composition/reproduce
python3 tools/ReportDiff.PageFlowProbe/audit-global-flow.py out/t3-1c-probe/type3-final out/t3-1c-global-composition/reproduce
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-global-reference.py out/t3-1c-global-composition/reproduce
```

独立監査はPillow／NumPyのあるPython、参照比較は既存のOpenCV環境を使う。製品の依存追加はない。

`global-flow.json` は18条件の進捗も兼ねるため、ファイルの存在だけを完了と判断しない。ツール・監査の終了コード0と全18条件の記録を確認する。`diagnostic_adoptable` は元帯・範囲・採用条件がすべて成立した独立ツール内の状態で、現CLIの動作ではない。`candidate_aggregation` は未採用の候補にも保存するため、それだけを採用結果として集計しない。`expectation_met` がfalseなら期待を下げずに調べる。

各条件の `O` は入力移動後の元画像、`G` は採用された全体補正後、`C/D` は候補の比較・表示面。元PDF描画を上下の余白と固定模様で拡張した対照は、元のR10等と別に扱う。全体補正を見送った入力に既知の移動量を適用しない。

`audit-global-flow.py` は元PNGからの入力生成、O→G、二通りのC/D描画、非白画素被覆、文字の一度だけの補正、元帯の画素、構造・クラスタの元部分矩形、実IDの参照と集約を検査する。元座標との混同、再読込変更、二重補正、変更矩形の検出／除外も確認する。通常設定のCは `check-global-reference.py` でPython参照と照合し、除外／領域のページはその参照比較へ含めない。

今回の保存先は `out/t3-1c-global-composition/final-verified/`。[結果と製品接続の境界](../../docs/verification/t3-1c-global-composition.md)を参照。横補正・元帯変更等の見送り、A4未成立9条件、ブラウザー保留を維持する。

## 複数原因の独立検証（製品未接続）

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --multiple-causes out/new-multiple
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-multiple-causes.py out/new-multiple
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --multiple-causes-replay out/t3-1c-probe out/new-multiple-legacy.json
```

`MultipleCauseProbe` は14ケース28PDFを作り、両方向を実描画・文字抽出・元帯検証・採用条件で確認する。期待は生成器側だけに置き、境界判定や集約器へ渡さない。`IndependentBoundary` は候補帯の全行が同じページ内に対応済みで、共通行が境界を跨がない場合だけ送りでないことを証明する。画素一致ではなく、対応する帯はCの比較へ残す。

`IndependentCauseAggregation` は送り連鎖の連結成分ごとに既存の単一原因集約を適用する。成分を一つでも説明できなければ文書全体を未集約に戻す。`MultipleCauseAudit` と `MultipleBoundaryAudit` が欠落・重複・所属・件数・列挙順等を変更して検査する。Python側は共通行が跨いだページから独立に成分を作り、編集列・実構造参照とC/D・比較参照マスクを照合する。

成果物の `multiple-causes.json` は独立ツールの採否、`*-ab/cli/`・`*-ba/cli/` は現製品の結果。前者の候補C/D件数と、後者の見送り後の基準件数を混同しない。`fixtures.json` に入力PDFのハッシュ・期待本文を保存する。固定の証拠は `out/t3-1c-multiple/verified/`、設計と限界は [計画](../../docs/planning/t3-1c-multiple-causes.md)・[検証記録](../../docs/verification/t3-1c-multiple-causes.md)を参照。領域・補正・片側ページを含む新機能の製品受け入れや性能検証にはしない。

## 複数原因の製品接続検証

2026-09-23の接続後は `check-multiple-cli.py` が、接続前に保存した独立証拠と現在の実CLIを比較する。固定28方向の採否・C/D・件数・構造を変えず、非送り候補の元IDと対応先証拠、無効時の全出力、元A/B・raw overlayを検証する。比較対象の旧CLIはビルド前に依存DLL・ランタイムごと別ディレクトリへ保存する。

```bash
# Pythonは既存のOpenCV / Pillow / NumPy環境を使う
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-multiple-cli.py out/new-multiple-cli out/before-bin/reportdiff.dll
python3 tools/ReportDiff.PageFlowProbe/check-connected.py out/new-multiple-legacy
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-global-cli.py out/new-multiple-global out/before-bin/reportdiff.dll --multiple
# pypdf環境で追加の補正PDFを再生成（既存の既定13ケースとは別の6ケース）
python3 tools/ReportDiff.PageFlowProbe/create-global-pdfs.py out/new-multiple-global-fixtures --multiple
# 製品集約器へ直接、固定メタデータの欠落・重複・所属等の監査を適用
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --multiple-product-audit tests/ReportDiff.Tests/Fixtures/page-flow-multiple/aggregation.json out/new-multiple-audit.json
# 現製品の元帯・C/Dを再収集し、Pythonの編集列と比較参照で独立に照合
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --multiple-causes out/new-multiple-current
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-multiple-causes.py out/new-multiple-current
```

`--multiple-causes` は製品接続済みの範囲では製品Planをそのまま使う。現在の `legacy` 欄も現製品集約器の結果であり、接続前の製品結果ではない。接続前の固定証拠は `out/t3-1c-multiple/verified/` に保存したまま使う。製品とは独立した判定は、保存された事前期待とPythonの共通行の跨ぎ・編集列・参照マスクによる。診断ツールに残る反射処理は製品から呼ばない。

```bash
python3 tools/ReportDiff.PageFlowProbe/instrument-scale.py out/new-multiple-instrumented
dotnet restore out/new-multiple-instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj --source out/packages -p:RestoreFallbackFolders="$HOME/.nuget/packages" -p:NuGetAudit=false --disable-parallel --disable-build-servers -m:1
dotnet build out/new-multiple-instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj --no-restore -c Release --disable-build-servers -m:1
python3 tools/ReportDiff.PageFlowProbe/measure-multiple-cli.py out/new-multiple-performance out/new-multiple-instrumented/src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll out/before-bin/reportdiff.dll
```

計測は他の重い検証が終わってから行う。3条件・無効／有効・3反復の製品18プロセス、接続前有効9プロセス、計測コピー6プロセス。日時を除く製品と計測コピーの全出力一致も確認する。[成立範囲と残件](../../docs/verification/t3-1c-multiple-cli.md)を参照。

## 数値変更を含む行対応の独立検証（製品未接続）

```bash
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --numeric-correspondence out/new-numeric
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-numeric-correspondence.py out/new-numeric
```

`NumericFixtures` は25ケース50PDFを生成し、うち複数原因の4PDFは `tests/ReportDiff.Tests/Fixtures/page-flow-multiple/` の固定入力とハッシュ・本文期待を使う。各PDFを両方向で検証する。生成時の正解は推定器へ渡さない。`IndependentNumericRows` は同一ページ・一意な非数値ラベル・直近二行の完全一致と相対位置を要求する。送り帯の全画素一致と実画像は変更しない。

診断内では対応IDで写像を作るが、採用評価には元本文を戻す。`numeric.json` の `adoptions` が元本文による評価、`alias_adoptions` は変更行を一致行として誤算入した場合の対照であり、後者を採用判定に使わない。薄い文字と罫線を持つ `weak-actual-support` は前者だけが支持不足で見送ることを要求する。`candidate` の件数は未採用のCにも保存するため、`input.gate_ready` と併せて読む。現製品の結果は各方向の `cli/` に別保存する。

`NumericAudit` は列挙順・根拠・選択・上限を変更して対応証拠を監査する。Python側は元本文と数値トークン、編集列・成分、実構造、C/Dの再構成、数値行の全画素被覆、比較参照マスク、raw overlayを独立照合する。最終証拠は `out/t3-1c-numeric/final-verified/`、[成立範囲と残件](../../docs/verification/t3-1c-numeric-correspondence.md)を参照。製品接続や性能・Windows・ブラウザーの受け入れにはしない。

## 数値行対応の製品接続検証

製品接続後は、上記の固定した独立証拠を参照して実CLIを検証する。接続前CLIは変更前にランタイム・DLLごと別フォルダーへ保存する。固定PDFは `tests/ReportDiff.Tests/Fixtures/page-flow-numeric/` にあり、`expected.json` の採否・件数は事前の独立結果から固定した。元帯の変更や薄い支持行の見送りを、成立例へ置き換えない。

```bash
# 固定50方向。旧版無効・新版無効・新版有効の150プロセス。
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-numeric-cli.py out/new-numeric-cli out/before-bin/reportdiff.dll
# 既存76条件と複数原因28方向の旧新比較。数値変更6方向だけに承認済み期待を適用。
python3 tools/ReportDiff.PageFlowProbe/check-numeric-legacy.py out/new-numeric-legacy out/before-bin/reportdiff.dll
# 最終ソースで送り有効50方向を再生成し、照合済みの全出力と日時以外を比較する。
python3 tools/ReportDiff.PageFlowProbe/replay-numeric-output.py out/new-numeric-replay out/new-numeric-cli
# 追加の縦補正対照はpypdf環境で生成する。通常・複数原因の既存入力を変更しない。
python3 tools/ReportDiff.PageFlowProbe/create-global-pdfs.py out/new-numeric-global-fixtures --numeric
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-global-cli.py out/new-numeric-global out/before-bin/reportdiff.dll --numeric
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-global-cli.py out/new-global-legacy out/before-bin/reportdiff.dll
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-global-cli.py out/new-global-multiple out/before-bin/reportdiff.dll --multiple
```

`check-numeric-cli.py` は、固定された元本文だけの支持評価、C/Dの全画素、構造ID、数値の内容クラスタ、raw画像、新旧の送り無効出力を照合する。見送り時は候補Cの件数を採用せず、通常比較のページ結果全体を比較する。`check-numeric-legacy.py` の98方向は日時以外の全出力を維持し、数値変更の6方向だけは独立証拠の6→2／11→3に接続する。`check-global-cli.py --numeric` は元のO帯・補正後G・C/Dの二通りの描画、構造／クラスタの元部分矩形、数値行・根拠行のO座標と参照クラスタを確認する。

```bash
python3 tools/ReportDiff.PageFlowProbe/instrument-scale.py out/new-numeric-instrumented
dotnet restore out/new-numeric-instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj --source out/packages -p:RestoreFallbackFolders="$HOME/.nuget/packages" -p:NuGetAudit=false --disable-parallel --disable-build-servers -m:1
dotnet build out/new-numeric-instrumented/src/ReportDiff.Cli/ReportDiff.Cli.csproj --no-restore -c Release --disable-build-servers -m:1
python3 tools/ReportDiff.PageFlowProbe/measure-multiple-cli.py out/new-numeric-performance out/new-numeric-instrumented/src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll out/before-bin/reportdiff.dll --numeric
```

計測は他の検証が終了してから実行する。通常の数値変更、独立二原因との組合せ、支持不足の3条件について、実CLI18・旧CLI9・計測コピー6プロセスを使う。計測コピーと製品の全出力一致を要求する。[製品接続の結果と残件](../../docs/verification/t3-1c-numeric-cli.md)を参照。ブラウザー／JavaScriptの保留をこの自動検証で合格にしない。

## 同じ送り連鎖の複数原因（独立検証）

`--shared-causes` は13ケース26PDF・66物理ページを生成し、現製品の元帯検証・採用・C/D・実構造と、実CLIの現在の結果を保存する。製品の集約器は変更しない。`shared_cause_model.py` は同符号の原因配列と共有構造を成分単位で説明し、`check-shared-causes.py` が別の編集列・行順位計算、Python参照マスク、全画素、破損証拠、既存104入力を照合する。

```bash
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --shared-causes out/new-shared-causes
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-shared-causes.py out/new-shared-causes out/t3-1c-probe
```

出力先は空のディレクトリを使う。`fixtures.json` はPDFハッシュと生成時の本文、`shared-causes.json` は実CLI・実コアの証拠、`shared-check.json` は新規26方向と監査集計、`legacy-shared-check.json` は保存済み104入力の再生結果。後者には既存の `aggregate-{fixed,additional,skia,skia-local,causal}/aggregation.json` が必要で、上の因果集約の手順から再生成する。再生のソースハッシュも保存する。

`hypothesis_met: false` の6方向は前段の推定・写像で未成立。検証スクリプトの正常終了は、これらの機能成立を意味しない。最終記録は `out/t3-1c-shared/verified/`、初期入力・監査エラーは同階層の `first/` とログへ保持する。[設計と製品接続案](../../docs/planning/t3-1c-shared-causes.md)・[結果と残件](../../docs/verification/t3-1c-shared-causes.md)を参照。

## 同じ送り連鎖の共有原因を製品へ接続する検証

接続前バイナリは `out/t3-1c-shared-cli/before-bin/`、独立期待は `out/t3-1c-shared/verified/` に保存する。新規出力先で実行し、過去の検証結果を上書きしない。

```bash
python3 tools/ReportDiff.PageFlowProbe/check-shared-product.py out/new-shared-core
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-shared-cli.py out/new-shared-fixed out/t3-1c-shared-cli/before-bin/reportdiff.dll
python3 tools/ReportDiff.PageFlowProbe/check-shared-legacy.py out/new-shared-legacy out/t3-1c-shared-cli/before-bin/reportdiff.dll
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-shared-numeric.py out/new-shared-numeric out/t3-1c-shared-cli/before-bin/reportdiff.dll
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-global-cli.py out/new-shared-global out/t3-1c-shared-cli/before-bin/reportdiff.dll --shared
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/replay-numeric-output.py out/new-shared-final out/t3-1c-shared-cli/fixed --shared
```

`check-shared-product.py` は保存130入力の採否・件数・原因集合・累積量・収支を照合する。全ページ対応の成功入力だけに複数原因用の破損監査を適用し、片側ページは採否・件数の再生だけとする。片側ページへ内容クラスタを人工追加しても単一原因の成功を要求する、といった適用外の監査はしない。`check-shared-legacy.py` は104方向の実CLIを旧新実行し、承認された共有原因8方向以外の全出力を日時以外で比較する。旧 `check-connected.py` は共有原因未集約時の期待を含むため、この接続の互換性確認には使わない。

固定PDFの追加生成:

```bash
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --shared-combination-fixtures out/new-shared-combinations
# pypdfのある環境で実行
python3 tools/ReportDiff.PageFlowProbe/create-global-pdfs.py out/new-shared-global-fixtures --shared
python3 tools/ReportDiff.PageFlowProbe/create-global-pdfs.py out/new-shared-strong-fixtures --shared-strong
```

共有原因と縦補正の最初の14方向は全体位置合わせの低スコア／横補正で見送りを維持する。`--shared-strong` は固定部分だけを増やす別の4方向で、既存の14方向やしきい値を置換しない。数値差分と二原因は両方向、独立原因との併存は順方向が成立し、逆方向はA側固定部が異なるため見送る。

既存数値50方向は `check-numeric-cli.py`、既存補正17条件・独立原因12方向・数値12方向は `check-global-cli.py` の既定／`--multiple`／`--numeric` で再確認する。最終ソースの再照合には `replay-numeric-output.py` を使う。

計測は上記の `instrument-scale.py` で作ったコピーをビルドし、他の重い処理終了後に次を実行する。

```bash
python3 tools/ReportDiff.PageFlowProbe/measure-multiple-cli.py out/new-shared-performance out/new-shared-instrumented/src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll out/t3-1c-shared-cli/before-bin/reportdiff.dll --shared
```

製品18・接続前9・計測コピー6プロセス。二原因2ページ、独立原因と併存4ページ、未成立の共有三ページ連鎖を各3反復する。前段の描画・支持・採用・raw evidenceを維持し、共有集約段階の時間も記録する。詳細は[検証記録](../../docs/verification/t3-1c-shared-cli.md)を参照。

## 共有原因の未成立6方向の前段診断（製品未接続）

固定26方向のPDFを読み直し、同一ページの変位別支持、文書全体の共通行順序、各境界を跨ぐ全行を取得する。推定器は変更せず、診断内だけで末尾／先頭帯を組み立てて、製品の元帯検証・C/D・採用・集約へ渡す。選択した帯の全文・全画素一致を、元本文による変位の支持へ代用しない。

```bash
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --shared-inference-diagnosis . out/new-shared-diagnosis
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-shared-inference.py out/new-shared-diagnosis out/t3-1c-shared-cli/fixed-final
```

後半引数は、共有原因接続後の製品CLI26方向の固定出力。診断では同じCLIを再実行し、日時以外のJSON／HTML／PNGの不変を確認する。Pythonは元画像→C/D転写、C生差分マスク、D投影、raw overlay、原因集合・累積量・収支を別に照合する。

`experiment.inferred` は製品の新しい採用結果ではない。支持不足の候補を後段の不足調査に使っても `displacement_support: false` のまま最終採用を拒否する。共通行が跨がない境界では、全ての適格変位から作った仮端点に既存の `PageFlowNonflow` 証明を要求し、結果を `nonflow_alternatives` と `nonflow` に残す。生画像を一致へ置き換える処理や新しいPDF生成はない。詳しくは[診断記録](../../docs/verification/t3-1c-shared-inference-diagnosis.md)と[次の接続案](../../docs/planning/t3-1c-shared-inference-diagnosis.md#診断結果と次の接続案)を参照。

## 複数ページへの表示投影・設定・採否の事前検証

元の300dpi固定PDFと前工程のPNGを使い、内容面Cの画素所属IDを実物理ページのDへ転写する。既存Coreの支持検査・比較・集約を診断から呼び、製品コード・CLI・HTMLには接続しない。

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/anchored-projection.py prepare out/new-cross-page-support out/new-projection-inputs
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --anchored-projection . out/new-projection-inputs/manifest.json out/new-projection-result
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/anchored-projection.py check out/new-projection-inputs out/new-projection-result
```

最初の引数は[跨ページ支持](#末尾原因の跨ページ支持の事前検証)の記録フォルダー。既存の最終記録を使う場合は `out/t3-1c-cross-page-support/complete-final` に置き換える。NumPy/OpenCVが必要。新しいPDFは作らず、前工程の12証拠候補に周辺変更2、改善率不足2、未対応設定14、写像と証拠の不一致6を追加した36条件を検証する。

`AnchoredProjection` はCoreのクラスタ所属IDを保持し、同じD座標のA/B参照を一つにまとめる。別ページでは同じ内容IDへの参照にする。小断片の対照は確定済みの4pxクラスタと1pxノイズを与える幾何学検査で、PDFや検出の正例ではない。`AnchoredAdoption` は `RowGroupValidator` / `RowSupport` を変更せず反射で呼び、各ページの改善率を確認する。`RowSupport` に渡す診断用の内部レイアウトにはCを設定せず、既存の `RowComparisonSurface` を偽造しない。`AnchoredEvidenceBinding` は元本文からCの行対応と原因帯を再構成し、画素被覆だけでは見つからない相手行の交換も拒否する。

候補の診断画像は見送り条件でも保存するが、`published_pages` は全文書採用時だけ `[1,2]` になる。この値は採否モデルの検査で、製品の保存トランザクションの証拠ではない。領域・除外の転写は座標モデルまでで、該当設定を含む文書はこの候補では全体を見送る。入力の指紋を残しPNG再読込変更を確認するが、製品用の参照結合・確保前の予算は未接続。詳細は[検証記録](../../docs/verification/t3-1c-anchored-projection.md)を参照。

## 元ページを保持する内容比較面の事前検証

`AnchoredContentSurface` は、片側の物理ページ全体をそのまま保持し、反対側の共通行を実際の元ページから参照する診断専用の表現である。現行の `RowComparisonSurface` を変更せず、送り行を内容比較に残す。意味上の行対応・原因の推定器ではなく、固定した証拠を使う内容面の実験であり、製品の採用・集約を実行しない。

まず後述の跨ページ支持プローブで固定30方向を含む記録を作る。以下の `out/new-cross-page-support` はその出力先。既存の最終記録は `out/t3-1c-cross-page-support/complete-final` にある。

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/anchored-content.py prepare out/new-cross-page-support out/new-anchored-inputs
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --anchored-content out/new-anchored-inputs/manifest.json out/new-anchored-result
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/anchored-content.py check out/new-anchored-inputs out/new-anchored-result
```

PythonにはNumPy/OpenCVが必要。新しいPDFは生成しない。固定4方向、メモリ上の対照22方向、写像破損32条件の計58条件を作る。元PDF・前工程の記録と元PNG・既存周期ゴールデンをSHA256で固定し、元走査行の一度だけの被覆、純白保持、物理ページの連続性、元座標への投影、通常座標の正解画像とPython参照コアを照合する。周期帯は既存I10/I11/I12/D20/D21の画像を白い周辺ごと配置し、描き直さない。

`check` の正常終了は観測結果の一致を意味し、全検出期待の達成ではない。薄色1px線2方向は通常座標でも0となり、検出期待を維持して `unmet_detection: 2` として保存する。内容色変更3,807pxは元の6行ケースの値で、別の8行対照の1,614pxと混同しない。全レコードの `adoption` / `aggregation` は `not_evaluated`。詳細と次工程は[検証記録](../../docs/verification/t3-1c-anchored-content.md)を参照。

## 末尾原因の跨ページ支持の事前検証

製品未接続の診断。固定30方向にメモリ上の反例14方向を加え、送り端点外の次ページ支持を評価する。元PDFは編集・再生成しない。候補の元ページ内支持0と次ページの独立支持を別に保持する。

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --cross-page-support . out/new-cross-page-support
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-cross-page-support.py out/new-cross-page-support
dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll -parallelMode none -class '*RowComparisonSurfaceTests*' -result-xml out/new-cross-page-support/common-surface-tests.xml
```

チェッカーは前工程の `out/t3-1c-same-page-support/complete/` と固定30方向のCore結果・元画像・raw overlayを照合する。この出力がない場合は、下記の支持境界プローブを先に同じ場所へ生成する。

`CrossPageSupportSurface` は診断用の写像手順で、末尾原因だけを構造帯として渡す。内容比較面の連続性検査は既存コードのまま実行する。元の2方向は200px・4行の支持と元帯一致が成立しても `unanchored_join` でC/D作成を拒否し、10→10を維持する。薄色・平坦化の支持評価は既存検証器へ直接渡す独立診断であり、全文書の採用評価への到達とは区別する。

証拠フィールドの差し替えは元配置から再計算して照合する。これは診断用の方法で、製品用の文書・候補IDへの参照結合と確保前の予算予約は実装していない。事前期待が未成立でも書き換えずに記録する。詳細と次工程は[検証記録](../../docs/verification/t3-1c-cross-page-support.md)を参照。

## 同一ページ二原因の支持境界

既存の `same-page-two` の支持0・未成立期待を維持し、容量・挿入位置・支持のインク・送り帯色・反復・順序を変えた15ケース30方向を検証する。製品条件を変更するツールではない。原ケースと同一のPDFを含み、仮説上の正例が未成立なら `existing_unmet` として残す。

```sh
dotnet build tools/ReportDiff.PageFlowProbe/ReportDiff.PageFlowProbe.csproj --no-restore -c Release --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --same-page-support out/new-same-page-support
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-shared-causes.py out/new-same-page-support
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll --shared-product-audit out/new-same-page-support/shared-causes.json out/new-same-page-support/product-audit.json
python3 tools/ReportDiff.PageFlowProbe/check-same-page-support.py out/new-same-page-support out/new-same-page-support/product-audit.json
dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll -parallelMode none -class '*PageFlow*' -result-xml out/new-same-page-support/tests.xml
```

`check-shared-causes.py` の旧104入力は保存済み集約入力を独立モデルへ再生する監査であり、現在の104実CLI出力の互換性試験ではない。支持専用チェッカーは新30方向の同一ページ変位・前後支持を再計算し、候補の原因参照・移動寄与・収支を製品Coreへ、採用済みだけをJSON／HTMLへ照合する。`between-short` と `faint-support` は範囲成立後の採用失敗なので、診断C/Dと製品の基準比較を区別する。

固定PDFと事前期待は `Fixtures/page-flow-same-page-support/`。[検証記録](../../docs/verification/t3-1c-same-page-support.md)を参照。

## 曖昧な送り候補の製品接続

`shared-chain` と `shared-and-independent` の計4方向を追加成立とし、他の固定22方向は旧CLIとの全出力一致を要求する。診断時の独立C/Dと採用監査値は `out/t3-1c-shared-diagnosis/complete/`、固定期待は `Fixtures/page-flow-shared/ambiguity-expected.json`。既存fixtureの期待を上書きしない。

```sh
# before-binは今回の製品変更前に退避したRelease CLI。各出力先には未使用のディレクトリを指定する。
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-ambiguity-cli.py out/t3-1c-ambiguity-cli/fixed out/t3-1c-ambiguity-cli/before-bin/reportdiff.dll
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-shared-legacy.py out/t3-1c-ambiguity-cli/legacy out/t3-1c-ambiguity-cli/before-bin/reportdiff.dll --strict
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-numeric-cli.py out/t3-1c-ambiguity-cli/numeric out/t3-1c-ambiguity-cli/before-bin/reportdiff.dll
# 既存補正はcheck-global-cli.pyの指定なし／--multiple／--numeric／--sharedを別出力先で実行する。
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/check-ambiguity-combinations.py out/t3-1c-ambiguity-cli/combinations
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/audit-ambiguity-output.py out/t3-1c-ambiguity-cli
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/replay-numeric-output.py out/t3-1c-ambiguity-cli/fixed-complete out/t3-1c-ambiguity-cli/fixed --shared
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/replay-numeric-output.py out/t3-1c-ambiguity-cli/numeric-final out/t3-1c-ambiguity-cli/numeric
python3 tools/ReportDiff.PageFlowProbe/instrument-scale.py out/t3-1c-ambiguity-cli/instrumented-final
# 計測コピーのCLIをReleaseビルドしてから実行。重い検証と同時に走らせない。
out/t2-4a/reference-env/bin/python tools/ReportDiff.PageFlowProbe/measure-multiple-cli.py out/t3-1c-ambiguity-cli/performance out/t3-1c-ambiguity-cli/instrumented-final/src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll out/t3-1c-ambiguity-cli/before-bin/reportdiff.dll --ambiguity
```

追加の20PDFを再生成する場合は、pypdfがあるPythonで `create-ambiguity-pdfs.py <新しい出力先>` を実行する。元入力は共有原因の固定PDF、変更内容は数値・色・固定模様・平行移動だけ。実アプリの依存やしきい値は変えない。組合せの再開時はCLIのDLLハッシュが一致することを要求する。未成立の同一ページ二原因やページ別補正の逆方向を成功期待に置き換えない。

### 末尾共有原因の旧入力：前側1行の支持診断

`--unpaired-local-support <新出力先>` は通常の末尾共有原因診断と同じ28PDFを生成し、診断専用の写像コピーで内部原因の前側1行を仮置きする。送り帯を局所支持へ足さず、既存の採用検査がどこで止まるかを記録する。製品CLIへの接続や支持条件の変更には使用しない。

`check-unpaired-local-support.py <今回の診断出力> <限定接続後の通常診断出力>` は独立座標表とPython参照で元ケースを照合し、対照26方向の全診断値・画像と製品計画28方向の不変を要求する。今回の出力先の親には前工程の `source-tested.json` を `source-before.json` として置く。旧入力2方向が採用されないことを明示し、正例の検出期待は変更しない。[検証結果](../../docs/verification/t3-1c-unpaired-local-support.md)を参照。

### A3のCLI出力互換

`verify-anchored-output.py <旧26方向のCLI出力> <新出力先>` は、保存済み26方向を日時以外のJSON／HTML・画像で照合する。元ケース `same-page-two-ab/ba` だけはA3で採用する期待差分（内容0・構造6・集約2）を要求する。全26方向でraw overlayのバイト一致も確認する。例:

```sh
python3 tools/ReportDiff.PageFlowProbe/verify-anchored-output.py \
  out/t3-1c-cross-page-support/compat-final out/t3-1c-anchored-output/compat
```

メモリ上の色・文脈変更のReport検査は `AnchoredOutputTests` にあり、追加の実PDF受け入れとは区別する。

### A4の実PDF受け入れと計測

`AnchoredAcceptanceFixtures` は旧元ケースの再生成をバイト照合し、色変更・文脈差分・C1/C2の差分率／クラスタ数上限の合成12PDFを別フォルダへ作る。色変更の帯は可逆圧縮で埋め込み、再描画の全画素一致を要求する。入力の詳細とSHAは `Fixtures/page-flow-anchored-acceptance/` に固定した。

- `--anchored-acceptance-fixtures <repo> <新出力先>`：合成PDFを再生成する。旧入力や固定期待を書き換える操作には使用しない。
- `verify-anchored-core.py <evidence> --actual-pdf`：`AnchoredAcceptanceTests` が実PDFから保存した6方向のC/D・参照マスクを独立照合する。入力を作るテストに `REPORTDIFF_ANCHORED_PDF_EVIDENCE` の絶対パスを渡す。
- `measure-anchored-acceptance.py <新出力先/measure>`：CLI全工程を6方向・無効／有効・3反復で計測する。反復の全出力、raw overlayの有効／無効一致も確認。重い検証と同時に実行しない。
- `verify-anchored-acceptance.py <新出力先>`：上記measureの保存済み正例を使い、上限8方向のページ出力一致、compare-dir、静的リンクを確認する。ブラウザー操作はしない。
- `instrument-anchored-lifetime.py <未使用のout内コピー先>`：コピーだけに元カラー・C/Dカラー・完成した比較マスクの寿命計数を追加する。コピーCLIをビルドし、`REPORTDIFF_ANCHORED_LIFETIME` にJSON保存先を渡す。ROI・比較器内作業・PNG・PDFium/Skia内部は対象外。

[検証結果と残る環境別受け入れ](../../docs/verification/t3-1c-anchored-acceptance.md)を参照。計測コピーの出力を製品と照合し、計数対象のMatとプロセス全体のRSSを混同しない。
