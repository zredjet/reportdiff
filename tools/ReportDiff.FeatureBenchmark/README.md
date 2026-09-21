# 特徴量の帯サイズ・余白処理の評価

特徴量生成の追加最適化と、製品導入後の条件切り替えを検証・計測する開発用ツール。製品から参照せず、新しい依存も追加しない。試作の結果と採否は [PERF-2i](../../docs/verification/perf-2i-feature-stripes.md)、製品導入の結果は [PERF-2j](../../docs/verification/perf-2j-adaptive-features.md) に記録する。

## 製品方式の検証と計測

リポジトリのルートから、製品と同じNuGet・ネイティブランタイムの環境で実行する。

```sh
dotnet build tools/ReportDiff.FeatureBenchmark -c Release
dotnet tools/ReportDiff.FeatureBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll verify reference/golden
dotnet tools/ReportDiff.FeatureBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll measure input.png 300 > out/feature.json
```

内部APIを使うため出力名はfriend assemblyの `ReportDiff.Tests`。実テストや他の同名ツールと出力先を混在させない。

`verify` は37ゴールデンのA/Bと、幅1・帯境界・末尾1行・非連続ROI・300/400dpi・通常／厳密・インク有無・背景半径0.001/1.5/20mmを検証する。合成入力では1/2/4/8workerと予算0への復帰も確認する。全ページ演算を基準に、float32特徴量・コントラスト・インクをバイト単位で照合し、親画像の不変も確認する。構成ごとに314照合。不一致は終了コード非0となる。

`measure 入力PNG [dpi=300 edge=0.3 radius=1.5 ink=true]` は1画像分の特徴量生成を測定する。3回ウォームアップ後7回、強制GCなし。読み込み・特徴量のハッシュ検査・解放は時間に含めない。各回のSHA-256、worker数、実際に選んだ帯の高さ（`StripeRows`）、1workerの一時Mat見積もりを記録する。すべての反復で特徴量が同じことを確認するが、構成をまたぐ照合は出力SHA-256を比較する。キャッシュが温まった補助計測なので、プロセス初回・PDF描画・比較・保存までのCLI計測とは分けて読む。

```sh
# .NETが認識するCPU数を1に制限する補助計測。物理的な1コア実機の評価ではない。
DOTNET_PROCESSOR_COUNT=1 dotnet tools/ReportDiff.FeatureBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll measure input.png 300
# 厳密比較・大きい背景半径・インクなし
dotnet tools/ReportDiff.FeatureBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll measure input.png 400 0 1.5 true
dotnet tools/ReportDiff.FeatureBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll measure input.png 300 0.3 20 true
dotnet tools/ReportDiff.FeatureBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll measure input.png 300 0.3 1.5 false
```

## PERF-2i時点の比較用コピー

`experiments.py` は現在の製品を `out/` にコピーし、選んだ処理だけを変更する。製品ソースは編集しない。コピー先が既存・`out/` 外の場合、またはパッチ対象が一意でない場合は停止する。途中失敗したコピーは使用しない。PERF-2iの基準は `c9660c3` で、後続の製品変更にも使える汎用パッチではない。再現には評価ツールが追加された `46f34fa` のチェックアウトを使う。条件切り替え導入後のソースではパッチ不一致で停止する。

```sh
python3 tools/ReportDiff.FeatureBenchmark/experiments.py out/feature-experiments
dotnet build out/feature-experiments/combined64/Features/Features.csproj -c Release
dotnet out/feature-experiments/combined64/Features/bin/Release/net10.0/ReportDiff.Tests.dll verify reference/golden
dotnet build out/feature-experiments/combined64/Probe/Probe.csproj -c Release
dotnet out/feature-experiments/combined64/Probe/bin/Release/net10.0/reportdiff.dll \
  compare input-a.pdf input-b.pdf --out samples/private/feature-run --dpi 300 --save-all-pages
```

| 構成 | 帯の高さ | インクを必要行だけ計算 | Lab余白を再利用 |
|---|---:|---|---|
| `stripe64` | 64 | いいえ | いいえ |
| `stripe256` | 256 | いいえ | いいえ |
| `ink128` | 128 | はい | いいえ |
| `ink64` | 64 | はい | いいえ |
| `reuse128` | 128 | いいえ | はい |
| `combined128` | 128 | はい | はい |
| `combined64` | 64 | はい | はい |
| `combined256` | 256 | はい | はい |

インクの範囲縮小は、余白付きL画像の中央ROIを膨張し、差分を同じ一時Matへ上書きして最終インクの担当行へ直接書く。L画像・背景画像は帯ごとに解放する。背景の近傍とfloat32の式・しきい値は変えない。

`LabStripeCache.cs.txt` はコピー用のソースで、製品や評価ツール本体にはコンパイルしない。workerごとの固定バッファで重なるLab行を移し、新しい行だけ変換する。`Span.CopyTo` で重なりを扱い、有効行だけのMatヘッダーを作って末尾の余った容量をフィルターの近傍に含めない。ヘッダーはバッファを所有しないため、必ずヘッダーを先に、workerのバッファを最後に破棄する。worker間でキャッシュは共有しない。

一時Matの見積もりは各案に合わせ、96MiBの並列予算と最大4workerを維持する。予算不足時は逐次。入出力の全ページMat・ネイティブ内部の領域等を含むRSS上限ではない。コピーの検証成功は製品導入の完了を意味せず、例外時の解放・全回帰・Windows等の受け入れは採用時に確認する。
