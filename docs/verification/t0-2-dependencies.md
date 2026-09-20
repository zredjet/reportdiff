# T0-2 依存の疎通確認

確認日：2026-09-20。環境：macOS 26.5 / Apple Silicon（osx-arm64）、.NET SDK 10.0.401、.NET Runtime 10.0.12。

## 結果

**T0-2 完了。** FFmpeg を含まないランタイムを用意する方針の承認を受け、Windows は公式 slim、macOS は比較と画像入出力に必要なモジュールのみのローカルビルドを採用した。

| 確認項目 | 結果 |
|---|---|
| `dotnet build` | 成功、警告 0・エラー 0 |
| `dotnet test` | 成功 2 / 2、失敗 0、スキップ 0（macOS arm64） |
| macOS の arm64 / x64 ランタイム | 両方のビルド成功、Mach-O のアーキテクチャとリンク先を確認 |
| FFmpeg の除外 | macOS は構成・シンボル・リンク先を検査。Windows はパッケージ資産・DLL の import / export・実装由来の文字列を検査 |
| Windows x64 の自己完結・単一 exe | macOS 上で publish 成功 |
| 必須ネイティブ DLL | exe 内部に Windows x64 の 3 種を確認、NuGet 内の DLL と SHA-256 が一致 |
| Windows 上の実行 | 2026-09-20、ユーザーが動作 OK を確認済み |
| Intel Mac | ユーザー指定により対象外。実機確認は残件に含めない |

現在は設定・比較コア（[T1-4 確認結果](t1-4-core.md)）、画像読み込み（[T1-5 確認結果](t1-5-images.md)）、PDF 読み込み（[T1-6 確認結果](t1-6-pdf.md)）、正規化とページ対応（[T1-7 確認結果](t1-7-pages.md)）、結果出力（[T1-8 確認結果](t1-8-output.md)）まで実装・検証済み。HTML も [T1-9 確認結果](t1-9-html.md) の範囲で検証済み。CLI の比較コマンドは [T1-10 確認結果](t1-10-cli.md) の範囲で実装・検証済み。T0-3 で確認した GitHub Actions の Windows x64 / macOS Apple Silicon の結果は、当時の依存疎通テスト 2 件が対象（[T0-3 確認結果](t0-3-ci.md)）。

ユーザー報告：「Windowsは確認。動作OK。Intel Macは用意できないので対象外。」Windows の動作確認結果として記録する。OS バージョン、.NET 未導入環境、疎通テスト 2 件の実行結果、日本語パス等の個別条件は報告されていないため、`TASKS.md` で別途管理する。

## 採用した依存

公式 NuGet V3 API で公開バージョンを取得し、プレリリースを除く最新安定版の nupkg と nuspec を確認してから追加した。

| 直接依存 | バージョン | 用途 |
|---|---|---|
| [OpenCvSharp4](https://www.nuget.org/packages/OpenCvSharp4/4.13.0.20260627) | 4.13.0.20260627 | 比較コアの画像演算 |
| [OpenCvSharp4.runtime.win.slim](https://www.nuget.org/packages/OpenCvSharp4.runtime.win.slim/4.13.0.20260627) | 4.13.0.20260627 | Windows x64 の FFmpeg なしランタイム |
| ReportDiff.OpenCvSharp4.runtime.osx | 4.13.0.20260627 | ローカル生成する macOS arm64 / x64 ランタイム。公式 NuGet のパッケージではない |
| [PDFtoImage](https://www.nuget.org/packages/PDFtoImage/5.4.0) | 5.4.0 | PDFium / SkiaSharp による PDF ラスタライズ |
| [YamlDotNet](https://www.nuget.org/packages/YamlDotNet/18.1.0) | 18.1.0 | 設定用。読み込み機能は T1-1 で実装 |
| [xunit.v3](https://www.nuget.org/packages/xunit.v3/4.0.1) | 4.0.1 | テスト。Microsoft.Testing.Platform 2.4.0 を使用 |

PDFtoImage から SkiaSharp / NativeAssets 4.150.1 と bblanchon.PDFium 152.0.7961 が復元された。ライセンスとテスト用の間接依存は [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md) に記録した。NuGet 宣言値と、ネイティブに組み込まれたコーデック固有の許諾文を区別している。

`xunit` 2.9.3 は NuGet で非推奨のため後継を使用。`global.json` で .NET 10 のテストランナーを Microsoft.Testing.Platform に指定した。調査時に確認した Microsoft.NET.Test.Sdk と xunit.runner.visualstudio は追加していない。

## 疎通テストの内容

`tests/ReportDiff.Tests/DependencySmokeTests.cs` の 2 件を逐次実行する。

1. SkiaSharp で 144 × 216 pt の白地に黒い矩形・赤い線を描いた PDF をメモリ上に作り、PDFtoImage で 300dpi にラスタライズする。600 × 900 px（許容 ±1px）、BGR 8bit 3 チャンネル、矩形中心の黒・余白の白・線の赤を確認した。ストリーム、画素ポインタ、ストライドを通じて変換し、ファイルパスをネイティブに渡していない。
2. BGR → float32 → Lab の値域、Blur の平均値、Erode / Dilate の画素数、ConnectedComponents のラベル、WarpAffine の移動先を小配列で確認した。PNG・JPEG・BMP・TIFF のメモリ上の encode / decode も成功し、可逆形式は元の画素と一致した。ロードされたラッパーに動画 API がないことも確認した。

## macOS ローカルランタイム

以下は生成済みバイナリの記録。受け入れ対象は Apple Silicon とし、Intel Mac 向けの x64 資産は参考情報として保持する。

再生成方法は [NATIVE_RUNTIME.md](../NATIVE_RUNTIME.md)。OpenCV 4.13.0 と OpenCvSharp コミット `b161e7e012f5101f6d5dc68a835c59db6cc88b18` を使用し、取得アーカイブの SHA-256 をスクリプトに固定した。

`core` / `imgproc` / `imgcodecs` のみを静的リンク。`WITH_FFMPEG=OFF`、`NO_VIDEOIO` などを指定し、FFmpeg と動画入出力を除外した。CMake の CPU 判定がビルドホストに引きずられないよう、キャッシュを初期化し、対象 CPU・アーキテクチャを指定している。

| RID | dylib のサイズ | SHA-256 |
|---|---:|---|
| osx-arm64 | 8,980,712 bytes | `2af68d4767a44d916b641c0d99aa1bf2ea2478144a0c94caf49442fdaa1a1818` |
| osx-x64 | 11,766,144 bytes | `5154b814e5eb12b5a8be9957055e1338773fd523909a5c4e4865b4c66428e791` |

`lipo -archs` で arm64 / x86_64 を確認。`otool -L` のリンク先はライブラリ自身、AppKit、libc++、libSystem のみ。Homebrew のパスはなく、FFmpeg のシンボルと LGPL 表示も検出されなかった。

生成 nupkg は `out/packages/ReportDiff.OpenCvSharp4.runtime.osx.4.13.0.20260627.nupkg`、7,805,508 bytes。SHA-256 は `5935d3792ae93e1f322abd23f5b495cdd0b5fefdb6b79828913ba684b415fb2d`。原文ライセンスと両 RID のネイティブ資産を同梱する。これらのバイナリは Git 管理対象外で、手順とソースの固定値を管理する。ビルド環境やパスが変わる場合、生成バイナリのハッシュが一致することまでは保証しない。

## Windows publish

実行したコマンド：

```bash
dotnet publish src/ReportDiff.Cli -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
python3 tools/inspect-win-bundle.py \
  src/ReportDiff.Cli/bin/Release/net10.0/win-x64/publish/reportdiff.exe
```

- exe：`src/ReportDiff.Cli/bin/Release/net10.0/win-x64/publish/reportdiff.exe`
- サイズ：**150,507,849 bytes（143.54 MiB）**
- SHA-256：`f0fa88990bff68019f026db30c85e87ed826fd2a1db53b698029e5c9b07d3675`
- バンドル形式 6.0、埋め込みファイル 185 件
- FFmpeg の資産、macOS / Linux のネイティブ資産はバンドルに存在しない

必須 DLL はファイル名の文字列検索だけでなく、バンドルのエントリから範囲を取り出し、PE の Machine 値が x64（0x8664）であること、元の NuGet 資産と SHA-256 が一致することを確認した。

| exe 内のネイティブライブラリ | サイズ | SHA-256 |
|---|---:|---|
| pdfium.dll | 7,220,736 bytes | `d3d9f4b7c9dabe3363f30779c5c3c715c47332749fa590e4b4a2b8b6780cb1c4` |
| OpenCvSharpExtern.dll | 55,547,904 bytes | `1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd` |
| libSkiaSharp.dll | 12,254,048 bytes | `c8770c219e0d3cd9bb119fad46f2d00ae1855b11315816e86e909e3332826212` |

publish ディレクトリには実行用 exe のほかデバッグ用 PDB が 5 個出力され、合計は 239,560,173 bytes。特に libSkiaSharp.pdb は 89,006,080 bytes。これらの PDB は実行に必要なサイドカー DLL ではない。

Windows slim の DLL にある `FFMPEG: YES` はリンク前の OpenCV 全体の構成文字列。そのまま FFmpeg 含有の根拠にはしない。DLL の import に FFmpeg はなく、`videoio_VideoCapture_new1`、`avcodec_`、`avformat_`、`libswscale license` も存在しなかった。一方、Media Foundation の import は残るため、Windows Server の実機確認項目は維持する。

## 標準ランタイムを使わない理由

公式 macOS 標準ランタイム 4.13.0.20260627 は FFmpeg を静的リンクし、Windows 標準版には FFmpeg DLL が含まれる。[上流の説明](https://www.nuget.org/packages/OpenCvSharp4.runtime.win/4.13.0.20260627)と実バイナリ中の LGPL 表示を確認済み。[FFmpeg のライセンス](https://ffmpeg.org/legal.html)を追加しない構成に切り替えた。

## 受け入れの範囲と残件

- macOS arm64 での依存疎通と、macOS 上からの Windows publish・バンドル静的検証を完了した。
- Windows の動作 OK はユーザー確認済み。ネイティブ展開・日本語パス・Media Foundation 等の個別条件は `TASKS.md` の確認リストで管理する。
- Intel Mac はユーザー指定により対象外。既存のバイナリ生成・アーキテクチャ・リンク先の検証記録は保持するが、実機確認を残件に含めない。
- Phase 1 の比較アルゴリズム・参照実装との一致・実帳票・完成版 CLI の受け入れには進んでいない。
