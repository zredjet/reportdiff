# T3-1a 座標写像の検証

2026-09-22、macOS arm64、.NET SDK 10.0.401 / Runtime 10.0.12、OpenCV 4.13.0。比較前の基準は `416a6fd`。比較式・既定値・ゴールデン期待値は変更していない。

## 座標の契約

`PageMap` が元 A/B と比較キャンバスを対応付ける。縦帯はそれぞれの開始位置と共通の長さを持ち、null の側を詰め物として表す。点は半開区間で扱い、元画像外・キャンバス外・詰め物には逆対応がない。領域は各帯の対応部分の外接矩形を返し、上端と下端の間に挿入された帯の高さを含む。整数画素の転写なので文字・罫線を伸縮しない。

現在の CLI が使うのは全体補正の 1 区間だけ。左上合わせと右下の白埋め、補正後 B、PDF の元描画サイズ・上下反転・四辺のクリップ、除外・領域矩形、切り出し、bbox_mm、HTML の除外 YAML を集約した。`Units` は物理換算の実装を維持する。画素とテキストで異なる丸め方、全体補正の評価領域の「丸め→拡張→クリップ」、x/y/w/h の個別換算も維持する。

PageMap を JSON に追加していない。確認用オーバーレイは元画像の左上合わせだけを使い、補正用の写像を適用しない。複数区間を推定する機能や `rows` 設定は T3-1b で扱う。

## 自動検証

- Debug / Release ビルドは警告・エラー 0。
- Release の xUnit v3 in-process runner：**1,330 件成功、失敗・スキップ 0**（101.834 秒）。既存の 1,306 件と今回の 24 件を含む。
- 追加テストは元 A/B とキャンバスの往復、左右端・上下端、片側の詰め物、帯の境界に接する矩形とまたぐ矩形、複数帯の画素転写、正負の dx/dy、異なる元サイズ、丸め前後の矩形、PDF 注釈の側別写像とキャンバス座標での除外を検証する。無効な帯定義と従来の PDF 注釈省略条件も確認する。
- 全回帰には全体補正・PDF の原点／回転／UserUnit・領域別の分類／移動・raw evidence の独立性・HTML・compare-dir・壊れた入力の処理が含まれる。ログの OpenCV の破損 PNG 警告は異常系テストによるもの。

```sh
dotnet build --no-restore --disable-build-servers -m:1
dotnet build -c Release --no-restore --disable-build-servers -m:1
dotnet tests/ReportDiff.Tests/bin/Release/net10.0/ReportDiff.Tests.dll
```

## 変更前後の成果物

[検証スクリプト](../../tools/verify-pagemap-compatibility.py)は基準・変更後の CLI を同じ入力と設定で実行する。JSON は生成日時だけを置換し、数値の表記・プロパティ順・空白を維持した本文を照合する。HTML も JSON 埋め込み・表示・一覧フッターの生成日時だけを置換する。PNG はバイト列の SHA-256 を照合し、ファイルの増減と終了コードも一致を要求する。

42 ゴールデン × 全体補正あり／なしの 84 ケースに、合成 PDF の変更・同内容・逆順・補正なし・no-html・領域指定・領域指定の逆順・サイズ違いと片側ページ、画像の領域指定・監査、compare-dir の 11 ケースを加えた。**95 ケースすべて一致**（JSON 96、HTML 95、PNG 651、計 842 ファイル）。入力はすべて合成データ。最終の結果は [照合記録](t3-1a-compatibility.json) を参照。

```sh
python3 tools/verify-pagemap-compatibility.py \
  out/t3-1a/baseline-bin/reportdiff.dll \
  src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll \
  out/t3-1a/compatibility-final --extra-cases out/t3-1a/extra-cases.json
```

`baseline-bin` は変更前に `416a6fd` の Debug CLI 出力一式を保存したもの。再生成するときはそのコミットを別ディレクトリに展開・ビルドして指定する。追加ケースの引数は実行ディレクトリに保存し、既存の T2-4 / T3-4b 合成フィクスチャを使用した。`--extra-cases` を省略すれば、リポジトリのゴールデンだけで 84 ケースを再現できる。

補正適用 PDF は B→A が `(-6,+4)px`、逆順では `(+6,-4)px`。変更箇所の注釈は `11123`→`11128` を維持する。判定画像の B は補正後、`raw_evidence.coordinate_system` は `original_top_left`、元 B の参照は `pages/p001_b_original.png` のまま。領域指定や監査によって確認用オーバーレイは変わらない。

## Windows と作業境界

win-x64 の自己完結・単一 exe を生成し、同梱 DLL・承認済みネイティブライブラリのハッシュ・.NET 10.0.12・FFmpeg 非同梱を静的に検査した。exe は 156,658,245 バイト、SHA-256 は `5b21cfdd2b86d768f7a1cf3761ef0a5d2193046013251a2e9cc9f82e54691bd8`。

配布 ZIP は `out/reportdiff-t3-1a-win-x64.zip`。最終文書と検証記録を含み、`tools/package-win.py` で依存の許諾文・ネイティブ DLL のハッシュ・ZIP CRC・全ファイルのマニフェストを検査する。

Windows 実機での実行・描画・ブラウザ確認は未実施。TASKS の Windows 確認リストに残す。T3-1a の完了後に一度止まり、T3-1b は着手しない。T2-8 の保留候補 D16 / D17 / J07 / J08 も未完了のまま維持する。
