# T3-6 確認用オーバーレイ

T3-6 完了。確認日：2026-09-22。macOS Apple Silicon、.NET SDK 10.0.401 / Runtime 10.0.12、Chrome 153.0.8010.52。[実装仕様](../planning/t3-6-raw-overlay.md)の固定グレー化と赤青合成を実装した。T2-8 の未成立候補は保留のまま維持する。

## 動作

`--raw-overlay` または `report.raw_overlay: true` で、元 A のみを赤、元 B のみを青、共通の濃淡を黒／グレーで表示する。既定は無効。全体補正・局所吸収・除外・差分マスク・注釈を生成入力に使わない。確認用画像には枠・番号・除外色を描かない。

左上を合わせ、サイズ差は右・下の白埋めだけを行う。方式・元座標・DPI・元サイズ・白埋め量と画像パスを `raw_evidence` に記録する。元カラー A/B は保存済みの未補正 PNG と共有し、必要なときだけ追加保存する。片側ページの欠落はフラグと白画像で表す。相違なし・too_different・除外後も選択全ページを保存し、`--no-html` にも対応する。

共通 YAML → 帳票別 YAML → 明示 CLI の優先順位。帳票別の false で共通の true を解除でき、CLI 指定は true を優先する。schema_version は 1。新規の判定・差分件数は設けない。同じグレー値に変換される色相差は見えないので、元カラー表示と説明を併設した。

## 自動検証と互換性

全体ビルドは警告 0・エラー 0。42 ゴールデンを含む全 1,228 テストが成功、失敗・スキップ 0（320.472 秒）。サンドボックスのローカル IPC 制約を避け、同じ xUnit テスト DLL を直接実行した。

専用テストは 36 件。固定 RGB/BGR、白・黒・グレー・アンチエイリアス・四捨五入・同じグレー値の異色・1px ずれ、全 65,536 通りのグレー対の交換対称性、非連続 Mat と入力不変を確認する。設定の型・既定・部分上書き・CLI 優先順位、JSON の往復、HTML の相対パス制限も含む。

実 CLI では画像・複数ページ PDF・same・different・too_different・除外後の same・右下のサイズ差・全体補正・両方向の片側ページ・ページ選択・HTML 省略・compare-dir を確認。モード有効／無効で既存 JSON・PNG・終了コードが一致した。通常／strict／loose、全面除外、全体補正あり／なしでも確認用 PNG の全バイトが一致した。全体補正時は保存した確認用 B が PDF の補正前ラスタと一致し、補正後 B と異なることを確認した。

確認用 A/B/overlay それぞれの PNG 保存先をディレクトリで塞ぎ、書き込み失敗を注入した。ReportWriteException が発生して Complete を拒否し、`--force` の旧出力を保持する。compare-dir でも確認用 PNG の失敗は全体の確定を中止し、旧結果と後始末を確認した。

変更前 `f49eb31` を別ディレクトリでビルドし、42 ゴールデンを変更前／変更後無効／変更後有効の 3 条件で比較した。全ケースの終了コード、JSON の既存項目、既存 219 PNG が完全一致した。照合から分離したのは generated_at と追加の config.report.raw_overlay / pages[].raw_evidence のみ。

さらに元のゴールデン PNG を NumPy で整数グレー化・合成し、42 確認用画像の全画素を独立に照合した。確認用カラー A/B の 84 画像も元入力＋白埋めと完全一致。[機械可読記録](t3-6-compatibility.json)を参照。比較コア・参照実装・検出期待は変更していない。

## ブラウザ

[専用スクリプト](../../tools/verify-raw-overlay.mjs)で、1280 / 390px と JavaScript 有効／無効を検証した。自作帳票、全体補正を適用した PDF、相違なしのページを対象とする。

- 判定表示、確認用オーバーレイ、元カラー A/B の切り替え。ほかのビューの画像・比較状態・件数・埋め込み JSON を変更しない。
- Tab で順にフォーカスし、Enter / Space で選択、選択状態とフォーカス枠・画像 alt・画像リンクが更新される。
- same の折りたたみをキーボードで開ける。元座標と判定座標の違い、凡例、DPI・サイズ・白埋め量・色相差の限界を表示する。
- JavaScript 無効でも確認用 PNG と元 A/B のリンクを開け、PNG 単体の寸法が一致する。
- ページ全体と確認用のボタン・説明・リンクが横にはみ出さない。スクリーンショットを目視確認した。
- ページエラー 0、外部リクエスト 0。既存 HTML 検証も補正ありのレポートで成功した。

[ブラウザ記録](t3-6-browser.json)を参照。詳細なスクリーンショットは `out/t3-6-browser*` に保存する。

## A4・300dpi の時間、メモリ、出力量

[計測スクリプト](../../tools/measure-raw-overlay.py)で 2,480 × 3,508px・600 文字の合成画像を使った。Release CLI の独立プロセスを有効／無効各 1 回ウォームアップし、順序を交互にして各 3 回計測。HTML を省略し、起動・PNG 読み込み・比較・PNG/JSON 保存を含む。PDF 描画は含まない。既存の JSON・PNG の一致も各回検査した。

| 入力 | 無効 → 有効の中央値 | 追加時間 | 最大 RSS 無効 → 有効 | 追加出力 |
|---|---:|---:|---:|---:|
| 完全一致 | 290.80 → 373.49ms | 82.69ms | 238.33 → 241.19MiB | 2,122,956 bytes |
| 1 か所変更 | 535.65 → 575.40ms | 39.75ms | 956.09 → 953.77MiB | 708,473 bytes |
| 変更＋全体 1px ずれ | 553.04 → 616.73ms | 63.69ms | 955.62 → 956.80MiB | 739,923 bytes |

完全一致では元 A/B と確認用の 3 PNG を追加する。相違ありでは元 A/B を共有し、確認用 1 PNG だけ増える。サイズは JSON を含む。最大 RSS は macOS `/usr/bin/time -l` の各プロセス最大値を採用し、各条件 3 回の最大を示す。比較段階も含むため、確認用画像だけの割当量ではない。小さい正負の差は実行間の変動として扱い、メモリ改善を主張しない。実装上の追加 Mat はページ単位で保存後に破棄し、変換用配列は 3 行分のみ。[全計測値](t3-6-a4.json)を参照。

## Windows 配布物と残る確認

Windows x64・自己完結・単一 exe の publish と静的バンドル検査が成功。exe は 156,594,757 bytes、SHA-256 は `d6109a71f08f1f27c04546357c27e346bda7d07345042b715a96e31fc3bb906b`。.NET 10.0.12 と既存依存、必須ネイティブ DLL 3 件、許諾文・依存のハッシュを検査する。新規の製品依存・FFmpeg・他 OS 向けネイティブ資産は追加していない。

更新した設定例・文書を含む ZIP の作成、CRC・マニフェスト内ハッシュの照合も成功した。実行ファイル検査は `out/t3-6-win-bundle.json`、最終 ZIP と梱包記録は `out/t3-6-win-final.zip` / `out/t3-6-win-package.json`。Windows exe の実行、Edge / Chrome 操作、Windows の性能値は未確認で、[実機確認リスト](../TASKS.md#windows-確認リスト人が実機で行う)に残す。T2-8 の保留分と次の T3-4b はこの受け入れに含めない。push・リリース公開は行っていない。

## 再実行

```bash
dotnet build --no-restore --disable-build-servers -m:1
dotnet tests/ReportDiff.Tests/bin/Debug/net10.0/ReportDiff.Tests.dll
dotnet src/ReportDiff.Cli/bin/Debug/net10.0/reportdiff.dll compare \
  reference/golden/D11_a.png reference/golden/D11_b.png --raw-overlay --out out/t3-6-rerun
node tools/verify-raw-overlay.mjs out/t3-6-rerun/report.html out/t3-6-rerun-browser
dotnet build src/ReportDiff.Cli -c Release --no-restore --disable-build-servers -m:1
python tools/measure-raw-overlay.py src/ReportDiff.Cli/bin/Release/net10.0/reportdiff.dll out/t3-6-rerun-a4
dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t3-6-win-rerun
python3 tools/package-win.py out/t3-6-win-rerun/reportdiff.exe out/t3-6-win-rerun.zip
```

比較の終了コード 1 は合成入力に差分があることを示す。ブラウザ検証は既存 Playwright / Chrome、計測は既存の Python OpenCV / NumPy を使う。必要なら NODE_PATH / REPORTDIFF_BROWSER を指定する。製品への依存追加ではない。
