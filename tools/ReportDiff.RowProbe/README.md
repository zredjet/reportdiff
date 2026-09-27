# 行整列の継ぎ目の再現

T3-1b の事前検証用。既存の PageMap に既知の正しい帯の対応を与え、白い詰め物の内側だけを除外して既存の比較器へ渡す。行推定の実装ではない。

合成フォントは T2-3 と同じ自作 Type3 方式。実帳票・外部フォント・新規依存を使わない。PDFium は逐次描画する。

リポジトリルートから実行する。

```bash
dotnet restore tools/ReportDiff.RowProbe/ReportDiff.RowProbe.csproj
dotnet build tools/ReportDiff.RowProbe/ReportDiff.RowProbe.csproj -c Release --no-restore --disable-build-servers -m:1
dotnet tools/ReportDiff.RowProbe/bin/Release/net10.0/ReportDiff.RowProbe.dll out/t3-1b-probe/results
dotnet tools/ReportDiff.RowProbe/bin/Release/net10.0/ReportDiff.RowProbe.dll out/t3-1b-probe/results --pdf
python tools/ReportDiff.RowProbe/check-reference.py
```

最後のコマンドには reference/prototype.py と同じ NumPy / OpenCV の環境を使う。今回のローカル環境では `out/t2-4a/reference-env/bin/python` を使用した。ネットワークなしの復元では、既存のローカルパッケージソースを指定する。

```bash
dotnet restore tools/ReportDiff.RowProbe/ReportDiff.RowProbe.csproj --source out/packages -p:NuGetAudit=false -p:RestoreFallbackFolders="$HOME/.nuget/packages" --disable-parallel --disable-build-servers -m:1
```

出力はすべて `out/` 内。画像試験 12 条件、合成 PDF 24 条件（罫線幅 6 種 × 挿入のみ／挿入＋数値変更 × normal／strict）を記録する。PDF では挿入帯と末尾の純白余白以外の画素差の最大値 `pairedNorm` も出す。値 0 はその全画素が一致することを表す。

Python 側は C# の全生差分マスク・状態・クラスタの矩形と画素数を照合する。さらに、正しい対応画素が一致する 9 通りの切断位置と A/B 交換を確認する。これは既存コアの挙動を切り分ける試行であり、偽の差分を機能の受け入れ期待に採用するものではない。

結果と停止理由は [検証記録](../../docs/verification/t3-1b-row-seams.md) を参照。

## 両側を白にする案の検証

比較用のコピーだけで、対応のない帯を両側とも白にする案を試す。帯の外側を変更していないこと、元画像・表示用画像・raw evidence が不変なことも検査する。raw evidence は製品の RawOverlay.cs を変更せず、この試行プロジェクトにリンクして生成する。製品の CLI への組み込み試験ではない。

```bash
dotnet build tools/ReportDiff.RowProbe/ReportDiff.RowProbe.csproj -c Release --no-restore --disable-build-servers -m:1
dotnet tools/ReportDiff.RowProbe/bin/Release/net10.0/ReportDiff.RowProbe.dll out/t3-1b-probe/results --white-band
dotnet tools/ReportDiff.RowProbe/bin/Release/net10.0/ReportDiff.RowProbe.dll out/t3-1b-probe/results --white-band-pdf
python tools/ReportDiff.RowProbe/check-white-band.py
```

画像 30 条件と PDF 42 条件を生成する。PDF の「42」は新しい試行の条件数であり、既存の 42 ゴールデンを指さない。PDF の入力・描画・基準／従来案／今回案の生差分マスクは `out/t3-1b-probe/results/white-band-pdf/`、画像の結果は `white-band/` に保存する。Python は PDF 42 条件 × 3 通りの比較を C# と全画素で照合し、境界の画素の判定値も記録する。

成功する対照例だけで採用せず、基準で検出された変更が今回案で消えるかを記録する。今回案は濃淡変更の検出を失うため不採用。詳細は [白い帯の検証結果](../../docs/verification/t3-1b-white-band.md) を参照。

## 元画像の特徴量を写す案の検証

`MappedFeatures.cs` と `mapped_features.py` は、元画像のぼかし・局所コントラスト・インクを帯ごとに写し、従来の式と探索順で比較する独立した試行。group とシフトは表示キャンバス上、構造帯の除外は二段目の後という案を検査する。製品への組み込みは行わない。

前節の `--white-band-pdf` 出力を生成してから、以下をリポジトリルートで実行する。Pythonは上記と同じNumPy / OpenCV環境を使う。

```bash
dotnet build tools/ReportDiff.RowProbe/ReportDiff.RowProbe.csproj -c Release --no-restore --disable-build-servers -m:1
dotnet tools/ReportDiff.RowProbe/bin/Release/net10.0/ReportDiff.RowProbe.dll out/t3-1b-probe/results --mapped-features
dotnet tools/ReportDiff.RowProbe/bin/Release/net10.0/ReportDiff.RowProbe.dll out/t3-1b-probe/results --mapped-identity
python tools/ReportDiff.RowProbe/check-mapped-features.py --suite all
python tools/ReportDiff.RowProbe/write-mapped-summary.py
```

Pythonの試行は `--suite pdf / context / tone / identity / csharp / causes` で分割実行もできる。`pdf` は前回の42条件、`context` は白い境界の180画像条件、`tone` は濃淡変更の42画像条件。`csharp` は今回の36 PDF条件についてC#と基準／今回案の全マスク・統計・クラスタ、初期候補数・groupの選択したずれを照合する。`identity` と `--mapped-identity` は、恒等写像の42ゴールデンをそれぞれ参照実装／製品コアと比較する。

出力は `out/t3-1b-probe/results/mapped-features/`。最後のスクリプトは全条件の完了と反例の再現を確かめ、`docs/verification/t3-1b-mapped-features.json` に結果とハッシュを記録する。反例を再現できたことは、候補方式の受け入れ成功ではない。検出必須の期待は維持し、今回案を不採用とした。[検証結果と次の設計課題](../../docs/verification/t3-1b-mapped-features.md)を参照。

## 表示面から独立した内容比較面

対応する帯だけの比較面で既存コアを呼び、クラスタ化後の生差分を表示面へ戻す。片側の純白余白も詰める案と、その余白だけは両側に残す案を比較する。前者は固定フッターの例で見逃すため不採用。後者の仕様案と確認範囲は [共同比較面の検証結果](../../docs/verification/t3-1b-common-surface.md)を参照。

先に上記 `--white-band-pdf` と `--mapped-features` でPDFの描画結果を生成する。その後、以下を順番に実行する。`prepare` が終わってから比較を開始する。

```bash
python tools/ReportDiff.RowProbe/common_surface.py prepare
dotnet build tools/ReportDiff.RowProbe/ReportDiff.RowProbe.csproj -c Release --no-restore --disable-build-servers -m:1
dotnet tools/ReportDiff.RowProbe/bin/Release/net10.0/ReportDiff.RowProbe.dll out/t3-1b-probe/results --common-surface
python tools/ReportDiff.RowProbe/common_surface.py run
python tools/ReportDiff.RowProbe/common_surface.py parity
python tools/ReportDiff.RowProbe/common_surface.py swap
python tools/ReportDiff.RowProbe/common_surface.py rejection
python tools/ReportDiff.RowProbe/common_surface.py guard
python tools/ReportDiff.RowProbe/common_surface.py direction
python tools/ReportDiff.RowProbe/write-common-summary.py
```

計306条件。純白余白を残す174条件（うち既存ゴールデン42）では基準A/Bの全画素を復元し、マスクも一致する。132条件の対照案では見逃し12条件を記録する。`parity` はC#とPythonの比較面・表示面のマスク、統計、クラスタ、写像を照合。`swap` は画像と写像を両方入れ替える。`rejection` は内容置換を削除＋挿入で隠す写像を拒否し、`guard` は近傍に元画像の連続した根拠があるかを確認する。

`direction` はA/Bの向きを変えた際の残差を確認する。今回の66組中6組に、既存コア由来の1pxのマスク位置差がある。それぞれの方向の基準マスクとは一致しており、完全な画素対称性まで確認したとは記録しない。

出力は `out/t3-1b-probe/results/common-surface/`。最後のコマンドで `docs/verification/t3-1b-common-surface.json` を更新する。この試行は既知の正しい帯を与えるもので、行推定・製品CLI・HTMLの受け入れ試験ではない。独立した試行の入力を作る際は画素から純白を識別しているが、製品の構造帯を色だけで推定する案ではない。

## 基盤実装後の安全網

純白余白を残す174条件は製品Coreの `RowComparisonSurfaceTests` へ固定入力として移した。元の試行コードと過去のJSONは当時の記録として維持する。自作PDFの共通生成器は `tests/ReportDiff.Tests/RowGridFixture.cs` からリンクする。現在のビルドで確認用画像を作り直す場合はv0.1.4の共通色設定が使われるため、過去の黒い共通色の画像ハッシュを上書きしない。

新しい実装範囲と未接続箇所は[基盤実装の記録](../../docs/verification/t3-1b-foundation.md)を参照。
