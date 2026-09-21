# T2-8 二段目を通る独立周期ケース

検証日：2026-09-21〜22、macOS Apple Silicon、.NET SDK 10.0.401 / Runtime 10.0.12、OpenCV 4.13.0。ユーザー承認に従い、[初回予備検証](t2-8-periodic-golden.md)の候補とは別に I10〜I12・D20・D21 を設計し、正本へ登録した。D16 / D17 の検出期待と未成立の記録は保持する。T2-8 全体は未完了。

## 入力と検証する性質

300dpi、1,350 × 960px の白背景に、原点 (240, 240) から周期の半分が暗い縞、残り半分が白となる帯を置く。縞の長さは 16px。帯の両端を白へ線形に薄くし、中央は 24 周期とする。通常は周期 4px・両端各 128px、loose は周期 8px・両端各 256px。暗い縞の各画素の濃度は `255 - round(255 * min(offset, length - 1 - offset, fade) / fade)`。Python / C# とも整数境界の矩形を 4 倍描画し、INTER_AREA で縮小する。

有限の線端も含めて複数の位置合わせが成立する入力にした。これは新規入力の設計であり、比較器のしきい値や元候補の変更ではない。除外領域や切り出しは使わず、全体補正も無効。loose は従来どおり `max_shift_mm=0.30` のみを使う。

| ID | B の変更 | 一段目の候補画素 | ゼロ残差のずれ (dx, dy) | 最終生差分画素 | 吸収 group | クラスタ |
| --- | --- | ---: | --- | ---: | ---: | ---: |
| I10 | 横 +2px | 1,736 | (±2, −1 / 0 / +1) の 6 通り | 0 | 1 | 0 |
| I11 | 縦 +2px | 1,736 | (−1 / 0 / +1, ±2) の 6 通り | 0 | 1 | 0 |
| I12 | 横 +4px、loose | 7,370 | (−4, 0)、(+4, 0) | 0 | 1 | 0 |
| D20 | I10 の中央の縞 1 本を欠落 | 1,740 | なし。最小残差 60 | 60 | 0 | 1 |
| D21 | I12 の中央の縞 1 本を欠落 | 7,338 | なし。最小残差 96 | 96 | 0 | 1 |

I10 / I11 の最大吸収ずれは 2px、I12 は 4px。D20 / D21 は中央の暗い縞をそれぞれ 2×16px / 4×16px 取り除く。追加の陰性対照により、複数解を許してもこの欠落を吸収しないことを確認する。

## 二段目を検証できた根拠

- 製品比較器を `max_shift_mm=0` で実行し、一段目の候補が上表の数だけ残ることを C# テストで確認する。相違なしという結果だけでは合格にしない。
- 元の Lab・3×3 平均・5×5 局所コントラストを用いて、ページ全画素をずれごとに再評価する。I10〜I12 では符号の異なる二つのずれが残差 0 になる。初期候補画素だけの照合より強い条件であり、実際の group 内でも両方が残差 0 になる。
- 既定の探索を有効にした製品比較器が 1 group を吸収し、最終差分が 0 になることを検証する。
- D20 / D21 は許容範囲の全 25 / 81 通りのずれを走査し、残差の最小値が正であることと、製品比較器による 1 クラスタの検出を検証する。

全探索の残差数・入力ハッシュは [JSON 記録](t2-8-periodic-stage2.json)、再検証は [Python スクリプト](../../tools/verify-periodic-golden.py)、C# の専用回帰は [PeriodicGoldenTests.cs](../../tests/ReportDiff.Tests/PeriodicGoldenTests.cs)を参照。ゴールデンを読む既存の回帰群にも 5 件が加わり、生成画像、全ページ／範囲最適化、並列処理などを照合する。

## 回帰と維持した契約

- ビルド：警告 0・エラー 0。
- C# 全体：1,168 件成功、失敗・スキップ 0、313.245 秒。追加は専用 5 件と既存データ駆動テスト 45 件。サンドボックスの `dotnet test` の named pipe 作成制約を避け、同じテスト DLL を直接実行した。
- Python：保存済み 42 件すべての統計・クラスタが `expected.json` と完全一致。OpenCV 4.13.0 / NumPy 2.5.3。
- 追加 5 件の PNG は Python / C# の各生成器と画素単位で一致。
- 変更前 37 件の期待値は意味的に同一、既存 74 PNG はバイト一致。比較式・既定値の Python 定義と SPEC 5 / 7 章を変更前と照合して維持を確認。`src/` の製品コード・依存を変更していない。
- HTML / CLI 出力の機能変更はないため、この追加に対する新規のブラウザ操作確認は不要。全体テストに含まれる既存の結合確認は成功。

## A4 計測と Windows 配布物

A4・300dpi（2,480 × 3,508px、600 文字）、Release、分類・移動注釈あり。シナリオごとに 2 回の結果照合後、3 回測定した中央値。PDF の読み込みや HTML / PNG 保存を含まない比較部分の時間であり、性能改善の主張ではない。データは [A4 計測 JSON](t2-8-periodic-a4.json)に保存した。

| 条件 | 中央値 | 結果 |
| --- | ---: | --- |
| 完全一致 | 5.726ms | 相違なし |
| 1 か所変更 | 90.011ms | 1 クラスタ・204 画素 |
| 1 か所変更＋全体 1px ずれ | 99.900ms | 同じ 1 クラスタ・204 画素、599 group 吸収 |

Windows x64 自己完結・単一 exe の publish と静的バンドル検査が成功した。exe は 156,575,301 bytes、SHA-256 は `fa7105c4a38a69b966789151cf12d58fcfc5df57efa0ab859bdce8303c454442`。.NET 10.0.12、既存依存・必須ネイティブ DLL 3 件・許諾文のハッシュを維持し、FFmpeg と他 OS のネイティブ資産を含まない。ZIP の作成と CRC・全 201 ファイルのハッシュ照合も成功した。成果物と検査出力は `out/t2-8-win/`・`out/t2-8-win-bundle.json`・`out/t2-8-win-package.json` に保存する。Windows での実行は未確認。

初回の publish は Windows 用の復元先がなく失敗した。ローカルキャッシュには指定版の Windows ランタイムが不足していたため、ネットワークの許可された復元で同じ 10.0.12 を取得して再実行した。プロファイルや依存バージョンは変更していない。

## 再実行

```bash
dotnet build --no-restore --disable-build-servers -m:1
dotnet tests/ReportDiff.Tests/bin/Debug/net10.0/ReportDiff.Tests.dll
out/t2-4a/reference-env/bin/python tools/verify-reference-golden.py
out/t2-4a/reference-env/bin/python tools/verify-periodic-golden.py
# 追加ケースだけを別ディレクトリへ再生成する。既存の J 系を再生成しない。
out/t2-4a/reference-env/bin/python reference/prototype.py \
  --only I10,I11,I12,D20,D21 --dump out/t2-8-periodic-rerun
dotnet run --project tools/ReportDiff.Benchmark -c Release -- 3 --classification=on
dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t2-8-win-rerun
python3 tools/inspect-win-bundle.py out/t2-8-win-rerun/reportdiff.exe
python3 tools/package-win.py out/t2-8-win-rerun/reportdiff.exe out/t2-8-win-rerun.zip
```

## 残件

この 5 件で二段目の複数解と欠落保持の安全網を追加した。元の I06〜I09 / D16〜D19 は予備検証のみで未登録、J07 / J08 は未検証。D16 / D17 を L 系へ変更する承認は含まれていないため、検出期待を維持した未成立候補として残す。T2-8 全体の完了・完了コミット・後続タスクの着手は行わない。Windows の新規ケース実行は [実機確認リスト](../TASKS.md#windows-確認リスト人が実機で行う)に残す。

その後の進め方：2026-09-22 の「次に進んでください」を受け、[保留範囲](t2-8-followup.md)を残して、T2-8 への依存がない T3-4a へ進む。上記は独立周期ケースを検証した時点の境界であり、T2-8 全体の未完了は継続する。
