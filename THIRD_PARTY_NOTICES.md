# サードパーティのライセンス

直接依存と配布に含まれるネイティブライブラリを記録する。Windows 配布物には、この文書と [licenses/](licenses/) の許諾文・著作権表示を同梱する。パッケージのライセンスと、静的リンクされたコードのライセンスは区別する。

ReportDiff 本体には [MIT License](LICENSE) を適用する。第三者のコード・ライブラリ・内蔵データは、それぞれの許諾条件に従う。本体の MIT License は、Intel IPP を含む第三者部分の条件を置き換えない。再配布時は本体の `LICENSE`、この文書、`licenses/` を保持する。

This software is based in part on the work of the Independent JPEG Group.

This software is based in part on the work of the FreeType Team (https://freetype.org/).

PDFium 内の FreeType には FreeType License（FTL）を適用する。[原文](licenses/PDFium/licenses/freetype.txt)と使用謝辞を保持する。

| 名前 | バージョン | ライセンス | 配布元 |
|---|---|---|---|
| PDFtoImage | 5.4.0 | MIT | https://github.com/sungaila/PDFtoImage |
| PdfPig | 0.1.16 | Apache-2.0（内蔵 Adobe データの通知は下記） | https://github.com/UglyToad/PdfPig |
| OpenCvSharp4 / OpenCvSharp4.runtime.win.slim | 4.13.0.20260627 | Apache-2.0 | https://github.com/shimat/opencvsharp |
| ReportDiff.OpenCvSharp4.runtime.osx（macOS 開発用） | 4.13.0.20260627 | ローカル nupkg 内の `licenses/NOTICE.md` と各原文を参照 | `tools/build-macos-runtime.py` で生成するローカルパッケージ |
| OpenCV | 4.13.0 | Apache-2.0 | https://github.com/opencv/opencv |
| YamlDotNet | 18.1.0 | MIT | https://github.com/aaubry/YamlDotNet |
| SkiaSharp / SkiaSharp.NativeAssets.* | 4.150.1 | MIT（Skia と組み込みライブラリは同梱の通知を参照） | https://github.com/mono/SkiaSharp |
| Skia | SkiaSharp 4.150.1 に同梱 | BSD-3-Clause | https://skia.googlesource.com/skia/ |
| bblanchon.PDFium.* | 152.0.7961 | Apache-2.0（NuGet 宣言）、MIT（公式配布アーカイブの LICENSE） | https://github.com/bblanchon/pdfium-binaries |
| PDFium | 152.0.7961 に同梱 | BSD-3-Clause / Apache-2.0（同梱原文と内蔵コードの通知を参照） | https://pdfium.googlesource.com/pdfium/ |
| xunit.v3 と関連パッケージ（テスト用） | 4.0.1 | Apache-2.0 | https://github.com/xunit/xunit |
| xunit.analyzers（テスト用） | 2.1.0 | Apache-2.0 | https://github.com/xunit/xunit.analyzers |
| Microsoft.Testing.Platform / .MSBuild / Extensions（テスト用） | 2.4.0 | MIT | https://github.com/microsoft/testfx |
| Microsoft.ApplicationInsights（テスト用） | 2.23.0 | MIT | https://github.com/microsoft/ApplicationInsights-dotnet |
| Microsoft.Bcl.AsyncInterfaces（テスト用） | 6.0.0 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Win32.Registry（テスト用） | 5.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Security.AccessControl（テスト用） | 6.0.1 | MIT | https://github.com/dotnet/runtime |
| .NET（自己完結配布） | 10.0.12 | MIT と .NET 同梱通知 | https://github.com/dotnet/runtime |
| CMake（ビルド用） | 4.3.0 | BSD-3-Clause | https://cmake.org/ |
| Ninja（ビルド用） | 1.13.0 | Apache-2.0 | https://ninja-build.org/ |
| Playwright / playwright-core（任意のブラウザ検証用、アプリには含めない） | 1.62.1 で確認 | Apache-2.0 | https://github.com/microsoft/playwright |

## Windows 配布に同梱する許諾文

| 対象 | 原文 |
|---|---|
| PDFtoImage | [MIT](licenses/PDFtoImage/LICENSE) |
| PdfPig と内蔵 Adobe データ | [Apache-2.0](licenses/PdfPig/LICENSE)、[NOTICES](licenses/PdfPig/NOTICES.txt)、[Glyph List](licenses/PdfPig/glyphlist-LICENSE.txt)、[Zapf Dingbats](licenses/PdfPig/zapfdingbats-LICENSE.txt)、[AFM の許諾](licenses/PdfPig/AdobeFontMetrics/MustRead.html)と[各著作権表示](licenses/PdfPig/AdobeFontMetrics/) |
| YamlDotNet | [MIT](licenses/YamlDotNet/LICENSE.txt) |
| OpenCvSharp | [Apache-2.0](licenses/OpenCvSharp/LICENSE) |
| OpenCV | [Apache-2.0](licenses/OpenCV/LICENSE)、[著作権](licenses/OpenCV/COPYRIGHT)、[旧 BSD コード](licenses/OpenCV/doc/LICENSE_BSD.txt) |
| SkiaSharp と内蔵 Skia・コーデック等 | [MIT](licenses/SkiaSharp/LICENSE.txt)、[全文通知](licenses/SkiaSharp/THIRD-PARTY-NOTICES.txt) |
| PDFium 配布パッケージと内蔵コード | [パッケージ](licenses/PDFium/LICENSE)、[PDFium](licenses/PDFium/licenses/pdfium.txt)、[全内蔵コードの通知](licenses/PDFium/licenses/) |
| .NET 自己完結ランタイム | [MIT](licenses/dotnet/LICENSE.TXT)、[全文通知](licenses/dotnet/THIRD-PARTY-NOTICES.TXT) |

SkiaSharp と .NET は使用中の NuGet パッケージから原文をコピーした。PDFium は同じバージョンの公式 Windows x64 アーカイブから全通知を取得し、アーカイブ内の `pdfium.dll` と NuGet の DLL が SHA-256 で一致することを確認した。残る直接依存は nuspec のコミットに対応する原文を使用した。各ファイルの出典と SHA-256 は [sources.json](licenses/sources.json)、確認した依存と DLL のハッシュは [dependencies.json](licenses/dependencies.json) に記録する。

PdfPig は nuspec が指す `a7bb35662bbbf405efddad50aedc9bcdcf515afc` の原文を使用する。内蔵 Adobe Glyph List は BSD 形式の許諾文を保持し、Adobe Font Metrics は独自の再配布許諾（MustRead.html）と全 14 ファイルの著作権表示を保持する。これらの内蔵データの条件を PdfPig の Apache-2.0 表記で置き換えない。net9.0 アセットを .NET 10 で利用し、新しい推移的パッケージやネイティブライブラリは追加しない。

### SkiaSharp の内蔵ライブラリの補足

SkiaSharp 4.150.1 の NuGet に含まれる包括通知を変更せず保持し、固定版の Windows ビルドで使われる次のライブラリの原文を補足する。依存や DLL の追加・変更は行っていない。

| 内蔵コード | 固定コミット | 許諾文 |
|---|---|---|
| VulkanMemoryAllocator | `c788c52156f3ef7bc7ab769cb03c110a53ac8fcb` | [MIT](licenses/SkiaSharp/VulkanMemoryAllocator-LICENSE.txt) |
| D3D12MemoryAllocator | `169895d529dfce00390a20e69c2f516066fe7a3b` | [MIT](licenses/SkiaSharp/D3D12MemoryAllocator-LICENSE.txt) |
| Wuffs | `e3f919ccfe3ef542cfc983a82146070258fb57f8` | [Apache-2.0](licenses/SkiaSharp/Wuffs-LICENSE) |
| SPIRV-Cross | `b8fcf307f1f347089e3c46eb4451d27f32ebc8d3` | [Apache-2.0](licenses/SkiaSharp/SPIRV-Cross-LICENSE) |

採用コミットは SkiaSharp `c3e4f4c20e1f23ab74d31a8838a5bd6dc55365f2` の `externals/skia` が指す Skia `0aa2d542e833ffd1d4d1b68a5152375e7f65ce11` の [DEPS](https://github.com/mono/skia/blob/0aa2d542e833ffd1d4d1b68a5152375e7f65ce11/DEPS) から確認した。[Windows ビルド](https://github.com/mono/SkiaSharp/blob/c3e4f4c20e1f23ab74d31a8838a5bd6dc55365f2/native/windows/build.cake)は Vulkan / Direct3D を有効にし、Skia の `BUILD.gn` / `gn/skia.gni` は上記 4 ライブラリを参照する。包括通知中の libmicrohttpd は、この固定版の DEPS では無効であり、配布 DLL の採用根拠にはしない。

### Windows OpenCV の内蔵ライブラリ

採用済みの公式 slim 版には以下の画像処理・コーデック等も含まれる。FFmpeg は含まない。これらは T1-12 で新たに追加した依存ではなく、既存バイナリの配布通知を補完したもの。

| 内蔵コード | バージョン／範囲 | 許諾文 |
|---|---|---|
| libjpeg-turbo | 3.1.3 | [IJG / BSD / zlib の説明](licenses/OpenCV/libjpeg-turbo/LICENSE.md)、[IJG 原文](licenses/OpenCV/libjpeg-turbo/README.ijg) |
| libpng | 1.6.55 | [PNG Reference Library License version 2](licenses/OpenCV/libpng/LICENSE) |
| libtiff | 4.7.1 | [LibTIFF](licenses/OpenCV/3rdparty/libtiff/LICENSE.md) |
| zlib | 1.3.1 | [zlib](licenses/OpenCV/3rdparty/zlib/LICENSE) |
| libwebp | 1.6.0 | [BSD-3-Clause](licenses/OpenCV/libwebp/COPYING)、[PATENTS](licenses/OpenCV/libwebp/PATENTS) |
| liblzma（TIFF の依存） | 5.8.2 | [0BSD](licenses/OpenCV/liblzma/COPYING.0BSD)、[構成ごとの説明](licenses/OpenCV/liblzma/COPYING) |
| OpenJPEG | 2.5.3 | [BSD](licenses/OpenCV/3rdparty/openjpeg/LICENSE) |
| OpenEXR | 2.3.0 | [BSD-3-Clause](licenses/OpenCV/3rdparty/openexr/LICENSE) |
| Intel IPP / IW | 2022.2.0 | [Intel Simplified Software License](licenses/OpenCV/IPP/EULA.rtf)、[第三者コードの通知](licenses/OpenCV/IPP/third-party-programs.txt) |
| Intel ITT | 3.25.4 | [BSD-3-Clause](licenses/OpenCV/3rdparty/ittnotify/src/ittnotify/BSD-3-Clause.txt)（二重ライセンスの BSD 側を使用） |
| SoftFloat、FLANN、OpenCL、QUIRC、MSCR | OpenCV 4.13.0 の対応ソース | [SoftFloat](licenses/OpenCV/modules/core/3rdparty/SoftFloat/COPYING.txt)、[FLANN](licenses/OpenCV/FLANN-LICENSE.txt)、[OpenCL](licenses/OpenCV/3rdparty/include/opencl/LICENSE.txt)、[QUIRC](licenses/OpenCV/3rdparty/quirc/LICENSE)、[MSCR](licenses/OpenCV/modules/features2d/3rdparty/mscr/chi_table_LICENSE.txt) |
| FlatBuffers、DLPack | OpenCV 4.13.0 の同梱通知 | [FlatBuffers](licenses/OpenCV/3rdparty/flatbuffers/LICENSE.txt)、[DLPack](licenses/OpenCV/3rdparty/dlpack/LICENSE) |

**Intel IPP の許諾条件は Apache-2.0 / MIT / BSD とは異なる。** 固有の EULA を同梱し、OpenCvSharp の Apache-2.0 表記で置き換えない。上表は使用中の既存ランタイムの通知であり、ReportDiff 自身のライセンスを定めるものではない。

Windows コーデックの版は OpenCvSharp の固定コミットにある vcpkg baseline `1e199d32ad53aab1defda61ce41c380302e3f95c` と DLL の構成情報から確認した。IPP は OpenCV 4.13.0 の固定アーカイブの MD5 と照合して EULA を取得した。OpenCV の構成情報にはリンク前の全モジュールも出るため、`FFMPEG: YES` という文字列だけで配布バイナリへの含有を判定しない。

## macOS のネイティブ画像コーデック

macOS のローカルランタイムには、OpenCV のソースに含まれる以下のコーデックを静的リンクする。各許諾文と著作権表示をローカル nupkg の `licenses/` に同梱する。これらのコーデックのライセンスを OpenCV の Apache-2.0 と同一とは扱わない。

| 名前 | バージョン | 上流の許諾文 |
|---|---|---|
| libjpeg-turbo | 3.1.2 | BSD 系の IJG / BSD-3-Clause、SIMD 部分は zlib（`LICENSE.md`、`README.ijg`） |
| libpng | 1.6.53 | PNG Reference Library License version 2（`LICENSE`） |
| libtiff | 4.7.1 | LibTIFF license（`LICENSE.md`） |
| zlib | 1.3.1 | zlib license（`LICENSE`） |

FFmpeg は追加・同梱しない。Windows 配布 ZIP には macOS 用ネイティブ資産やテスト・ビルド用ツールは含めない。macOS ランタイムの再生成方法は [NATIVE_RUNTIME.md](docs/NATIVE_RUNTIME.md) を参照。
