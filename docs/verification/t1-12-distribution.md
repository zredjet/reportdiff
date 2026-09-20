# T1-12 配布物の確認

確認日：2026-09-21、macOS Apple Silicon、.NET SDK 10.0.401、自己完結ランタイム 10.0.12。

## 成果物

README を利用者向けに整理し、Windows の実行例、終了コード、入出力、全 CLI オプション、プロファイルの選び方、DPI と除外領域の扱い、既知の限界を記載した。`examples/settings.yaml` は除外なしの既定値で、そのまま読み込める。

Windows 用発行プロファイルと [配布手順](../DISTRIBUTION.md)を追加した。`THIRD_PARTY_NOTICES.md` と `licenses/` に、依存の一覧・原文 47 ファイル・出典・ハッシュをそろえた。採用済み Windows OpenCV の Intel IPP には固有の Intel Simplified Software License があり、その EULA も明記・同梱した。依存パッケージや比較アルゴリズムの変更はない。

## Windows exe の静的確認

```bash
dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/t1-12-publish
python3 tools/inspect-win-bundle.py out/t1-12-publish/reportdiff.exe
python3 tools/package-win.py out/t1-12-publish/reportdiff.exe out/dist/reportdiff-win-x64.zip
```

| 項目 | 結果 |
|---|---|
| Windows x64 の Release / 自己完結 / 単一ファイル publish | 成功、警告・エラーなし |
| exe サイズ | 150,653,769 bytes（143.67 MiB） |
| exe SHA-256 | `0afe0f28611c0a3eadc395aa86e165f6dad1d0e60b396b1db3a6beeb07fa67f5` |
| バンドル形式・エントリ数 | 6.0、185 件 |
| 埋め込みランタイム | Microsoft.NETCore.App 10.0.12 |
| パッケージ依存 | 7 件。macOS / Linux 用パッケージ・テスト用依存なし |
| FFmpeg・他 OS のネイティブ資産 | なし |

exe 内部のエントリから DLL を取り出し、Windows PE の Machine が x64（0x8664）であることを検証した。必須 DLL の SHA-256 は使用中の NuGet 資産と一致する。

| 必須 DLL | サイズ（bytes） | SHA-256 |
|---|---:|---|
| pdfium.dll | 7,220,736 | `d3d9f4b7c9dabe3363f30779c5c3c715c47332749fa590e4b4a2b8b6780cb1c4` |
| OpenCvSharpExtern.dll | 55,547,904 | `1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd` |
| libSkiaSharp.dll | 12,254,048 | `c8770c219e0d3cd9bb119fad46f2d00ae1855b11315816e86e909e3332826212` |

OpenCvSharpExtern 内に `videoio_VideoCapture_new1`、`avcodec_`、`avformat_`、`libswscale license` がないことも確認する。ランタイムの選定・FFmpeg 除外構成は T0-2 から変更していない。

## 配布 ZIP と回帰確認

`package-win.py` は依存一覧・ランタイム・必須 DLL と原文のハッシュを確認してから、exe、README、設定例、文書、通知を梱包する。ZIP の CRC と全ファイルの SHA-256 を照合する。PDB・別置き DLL は含めない。既存 ZIP を上書きせず、依存情報・DLL ハッシュ・許諾文を変えると梱包を拒否することも確認した。

`dotnet build` は警告 0・エラー 0、`dotnet test` は全 423 件成功（失敗・スキップ 0）。設定例は実際の CLI で同一画像を比較して終了コード 0 となり、実効設定が既定値と一致した。`--dpi 400` を加えた場合は省略した `image_dpi` も 400 に追従した。

ZIP 内の `bundle-inspection.json` と `manifest.json` は、その ZIP に含まれる exe・文書の検証記録になる。ZIP 自体のサイズと SHA-256 は作成コマンドの標準出力で取得できる。ビルド環境やソースが変わった場合に exe や ZIP のハッシュが同一になることまでは保証しない。

## 受け入れ範囲

**T1-12 完了。** 配布準備・macOS 上での Windows publish・静的バンドル検証・ZIP 検証までを確認した。GitHub への push、Release の公開、コード署名は行っていない。

完成版 exe の Windows 実行、.NET 未導入 PC、ネイティブ展開、cmd / PowerShell の日本語表示、Windows Edge / Chrome、SmartScreen、Media Foundation、実帳票の評価は [Windows 確認リスト](../TASKS.md#windows-確認リスト人が実機で行う)に残る。T0-2 のユーザー報告と T0-3 の CI 疎通 2 件は、それぞれ当時の成果物の確認として保持する。Intel Mac は対象外。Phase 2 には未着手。
