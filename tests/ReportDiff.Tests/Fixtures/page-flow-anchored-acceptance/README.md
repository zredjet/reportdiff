# 元ページを保持する内容面の実PDF受け入れ

自作Type3の文字層・図形を使う合成データ。実帳票は含まない。旧 `page-flow-same-page-support` の30PDFと期待値は変更しない。生成器は元の `same-page-two/a.pdf,b.pdf` の再生成がバイト一致することを先に要求する。

| 入力 | 変更と必須期待（両方向） |
|---|---|
| tone | B1の第4行の全カラー画素へ `v × 89 / 255 + 166` を適用した帯を、可逆圧縮画像として重ねる。元の文字層を保持。実PDFの再描画はメモリ診断の画素と完全一致し、Cの3,807px・1件を保持して7→3 |
| context | B1の `(580,798,240,2)` 相当へ黒線を追加するベクター命令。送り帯の元画素は変更しない。Cの956px・1件を保持して7→3、D2へ同じ内容IDの238pxを参照表示 |
| too-different / second-too-different | 横幅480ptの別レイアウト。B1本文またはB2の対応本文を黒塗りにする。C1またはC2が既定の差分率上限を超え、文書全体を `anchored_content_incomplete` で従来比較へ戻す |
| cluster-limit / second-cluster-limit | 同じ横幅480ptでB1本文またはB2の対応本文に離した黒点を配置する。C1またはC2で既定500クラスタ上限へ達し、同じ理由で全体を従来比較へ戻す |

色変更の元ベクターを灰色指定するだけでは描画の丸めが異なり、3,798pxとなったため採用していない。埋込画像は999pxを239.76ptへ配置して300dpiで再標本化を避ける。期待画素数や比較式を合わせ直してはいない。300dpi以外の色変更PDFの描画を、この画素一致の保証へ含めない。

全12PDFの固定値は `sha256.json`。境界ケースで旧固定PDFの寸法・内容を上書きしない。本文の対応関係だけでは採用せず、製品の元帯証明・支持・改善率を実行する。

```sh
dotnet build tools/ReportDiff.PageFlowProbe -c Release --no-restore --disable-build-servers -m:1
dotnet tools/ReportDiff.PageFlowProbe/bin/Release/net10.0/ReportDiff.PageFlowProbe.dll \
  --anchored-acceptance-fixtures . <新しい出力先>
```

固定入力を更新するためのコマンドではない。新しい出力先で再生成し、ここに保存したSHAと一致を確認する。
