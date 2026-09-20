# ネイティブ依存の準備

## macOS（Apple Silicon）

対象は Apple Silicon。Intel Mac は確認環境を用意できないため対象外とする（2026-09-20、ユーザー指定）。現行スクリプトとパッケージには x64 資産も残っているが、Intel Mac のサポート・実機受け入れは行わない。

必要なものは .NET 10 SDK、Python 3.12 以降、Xcode Command Line Tools、初回取得用のネットワーク接続。CMake と Ninja はスクリプトが `out/native-build/tools/` の仮想環境へ固定バージョンで導入する。

リポジトリのルートで実行する。

```bash
python3 tools/build-macos-runtime.py
dotnet build
dotnet test
```

このスクリプトは OpenCV 4.13.0 と OpenCvSharp の固定コミットを取得し、ソースアーカイブの SHA-256 を検証する。`core`、`imgproc`、`imgcodecs` を静的リンクした `libOpenCvSharpExtern.dylib` を arm64 / x64 の両方に生成する。FFmpeg、動画入出力、GUI、contrib、外部 Homebrew ライブラリのリンクは使わない。PNG・JPEG・BMP・TIFF の入出力は保持する。

`--jobs 4` のようにビルド並列数を指定できる。生成物・ソース・ビルドログ用の領域は `out/` なので Git 管理対象外。

- ローカル NuGet：`out/packages/ReportDiff.OpenCvSharp4.runtime.osx.4.13.0.20260627.nupkg`
- ネイティブ資産：`out/native-build/osx-arm64/wrapper/` と `out/native-build/osx-x64/wrapper/`
- ハッシュ・リンク先の記録：`out/native-build/build-evidence.json`

NuGet のローカルパッケージには両アーキテクチャの資産とライセンス原文を含める。公式 macOS パッケージとは ID を分け、`NuGet.Config` でこの ID の復元元を `out/packages` に限定する。ネイティブパッケージの内容を変更する場合はパッケージのバージョンも上げ、NuGet キャッシュの古い内容を再利用しないこと。

ソースを変更した疑いがある場合は、新しいクローンか新しい `out/native-build` で生成する。スクリプトはダウンロード済みアーカイブのハッシュを毎回検証するが、展開済みソースは再利用する。

## Windows x64

Windows では `OpenCvSharp4.runtime.win.slim` を NuGet から復元する。macOS 用のローカルパッケージを作る必要はない。PDFtoImage が PDFium と SkiaSharp の対応プラットフォーム資産を依存として導入する。

macOS 上からの自己完結・単一 exe の発行：

```bash
dotnet publish src/ReportDiff.Cli -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
python3 tools/inspect-win-bundle.py \
  src/ReportDiff.Cli/bin/Release/net10.0/win-x64/publish/reportdiff.exe
```

T0-2 の成果物は、2026-09-20 にユーザーから Windows での動作 OK が報告されている。個別の実機確認項目は `TASKS.md` を参照。

## ライブラリの範囲

macOS 版は比較と画像コーデック専用。OpenCvSharp の動画・カメラ・GUI・DNN 等の API は使えない。Windows slim でも動画用ラッパー API は提供されない。

Windows slim の `Cv2.GetBuildInformation()` には、リンク前の OpenCV 全体のビルド設定として `FFMPEG: YES` が残る。これだけをもって DLL に FFmpeg が含まれるとは判断しない。パッケージ内の資産一覧、実際の DLL の import / export、および FFmpeg のコード・シンボルの有無を併せて調べる。

ライセンス一覧は `../THIRD_PARTY_NOTICES.md`、T0-2 の確認結果は `verification/t0-2-dependencies.md` を参照。
