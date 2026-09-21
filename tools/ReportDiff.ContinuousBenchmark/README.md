# 連続ページ処理の計測

[PERF-2n](../../docs/verification/perf-2n-continuous.md)用の独立した実行ファイル。製品を変更せず、同一ページを20／100回繰り返したPDFを測る。1ページの公開PDFまたは自作PDFを入力にする。異なる内容のページやフォント辞書番号が変わる警告まで一般化する検証ツールではない。

```sh
dotnet build tools/ReportDiff.ContinuousBenchmark -c Release
dotnet tools/ReportDiff.ContinuousBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll \
  repeat-pdf input-a.pdf samples/private/continuous/20-a.pdf 20
dotnet tools/ReportDiff.ContinuousBenchmark/bin/Release/net10.0/ReportDiff.Tests.dll \
  probe input-a.pdf input-b.pdf samples/private/continuous-probe 300
```

`repeat-pdf`は既存のPdfPigで先頭ページを複製する。入力は1ページに限定し、既存出力は上書きしない。先に出力の親ディレクトリを作る。`probe`は両入力のページ数・DPIが一致する場合に限定し、既定設定・全ページ画像保存・HTML生成を使う。PDFiumとページは逐次。製品と同じ処理順・Matのusing範囲を保つ。強制GC、GC設定変更、待機による回収促進は行わない。

## 一括実行

ビルド後の`bin/Release/net10.0/`全体を未使用の`out/`へコピーして固定する。入力ディレクトリには`1-a.pdf`／`1-b.pdf`、それぞれから生成した`20-a.pdf`／`20-b.pdf`／`100-a.pdf`／`100-b.pdf`を置く。

```sh
python3 tools/ReportDiff.ContinuousBenchmark/measure.py \
  out/continuous/bin samples/private/continuous/inputs samples/private/continuous/runs
```

macOSの`/usr/bin/time -l`を使う。通常CLIは起動から保存・終了までの時間と最大RSSを取得するだけで、製品プロセス内へ計測を追加しない。300／400dpi・差異あり／同一PDFの4条件について、単ページ参照1回、20ページのウォームアップ1回、20／100ページを交互順で各3回、最後に100ページプローブを2回実行する。全40プロセス。プロセス内の初回JIT・初期化時間は各回に含まれる。ビルド・テスト・別測定とは同時実行しない。

全実行で単ページのJSONを各ページに対応付ける。変えるのはページ番号・画像／切り出しのページ別パス・警告のページ接頭辞だけで、比較値・注釈・警告本文を省略しない。集計をページ数倍と照合し、入力パス・ページ数・SHA-256も検査する。全PNGのパス・件数・SHA-256を照合し、プローブは通常CLIのJSONとも生成日時を除いて完全一致を要求する。警告にページ接頭辞がなければ停止する。HTML生成は確認するがバイト一致の対象外。

完了後、`summarize.py`で各ページ完了時のメモリ／GC、各段階の時間、ページ区間別の集計を取り出せる。段階ごとのRSS全観測値は元の`summary.json`に残る。公開用データの`observed_peak_rss_bytes`は、そのページの段階終了時の観測値の最大であり、OSが記録したプロセス全体の最大RSSとは異なる。

```sh
python3 tools/ReportDiff.ContinuousBenchmark/summarize.py \
  samples/private/continuous/runs/summary.json out/continuous/summary-public.json
```

## 計測値の読み方

プローブの標準出力はJSON。`stages`に描画・比較・注釈・保存・画像解放・フォント検査後・JSON／HTML生成・入力Dispose後の状態を記録する。`pages[].timings`は既存の比較内部の段階計測。

| 項目 | 意味 |
|---|---|
| `rss_bytes` | `Process.WorkingSet64`の観測時のRSS。短いピークを取り逃すため、プロセス全体の最大値とは別 |
| `managed_estimate_bytes` | `GC.GetTotalMemory(false)`。未回収オブジェクトを含み、強制回収後の生存量ではない |
| `allocated_total_bytes` | `GetTotalAllocatedBytes(false)`によるマネージド領域の累積確保量の概算。ネイティブ画像は含まず、常駐量とは異なる |
| `gen0/1/2` | 各世代のGC回数。上位世代のGCには下位世代の回収も含まれる |
| `last_gc_*` | `GetGCMemoryInfo()`の**直近のGC**の情報。同じindexが続く行は同じGCを示し、現在のヒープ量ではない |
| `last_gc_pause_ms` | そのGCのPauseDurationsの合計。観測間で複数GCが起きた場合の総停止時間ではない |
| `stage_ms` | 前の観測終了から今回の観測開始まで。スナップショット取得の負荷を除く |
| `page_ms` | 1ページ全体。途中の計測負荷を含む。初回ページを後続と分けて見る |

入力Dispose後も、製品の戻り値に相当するレポートメタデータは保持する。解放してもアロケータがOSへ即座に領域を返すとは限らず、RSSが下がらないことだけでリークと判定しない。プローブの記録自体も少量の管理オブジェクトを保持するため、通常CLIの最大RSS・時間を併記する。

入力、レポート、抽出文字、生ログはGit管理外へ保存する。公開する計測JSONには時間・使用量・一致結果・ハッシュだけを含める。反復ページの成功を多様な実帳票・低メモリPC・Windows実機・長時間運用の受け入れと扱わない。
