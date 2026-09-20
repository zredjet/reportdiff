# サードパーティのライセンス

直接依存と配布に含まれるネイティブライブラリを記録する。

| 名前 | バージョン | ライセンス | 配布元 |
|---|---|---|---|
| PDFtoImage | 5.4.0 | MIT | https://github.com/sungaila/PDFtoImage |
| OpenCvSharp4 / OpenCvSharp4.runtime.win.slim | 4.13.0.20260627 | Apache-2.0 | https://github.com/shimat/opencvsharp |
| ReportDiff.OpenCvSharp4.runtime.osx | 4.13.0.20260627 | 同梱 `licenses/NOTICE.md` と各原文を参照 | `tools/build-macos-runtime.py` で生成するローカルパッケージ |
| OpenCV | 4.13.0 | Apache-2.0 | https://github.com/opencv/opencv |
| YamlDotNet | 18.1.0 | MIT | https://github.com/aaubry/YamlDotNet |
| SkiaSharp / SkiaSharp.NativeAssets.* | 4.150.1 | MIT（Skia と組み込みライブラリは同梱の通知を参照） | https://github.com/mono/SkiaSharp |
| Skia | SkiaSharp 4.150.1 に同梱 | BSD-3-Clause | https://skia.googlesource.com/skia/ |
| bblanchon.PDFium.* | 152.0.7961 | Apache-2.0（パッケージ） | https://github.com/bblanchon/pdfium-binaries |
| PDFium | 152.0.7961 に同梱 | BSD-3-Clause（組み込みライブラリは上流の通知を参照） | https://pdfium.googlesource.com/pdfium/ |
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

## ネイティブ画像コーデック

macOS のローカルランタイムには、OpenCV のソースに含まれる以下のコーデックを静的リンクする。各許諾文と著作権表示をローカル nupkg の `licenses/` に同梱する。これらのコーデックのライセンスを OpenCV の Apache-2.0 と同一とは扱わない。

| 名前 | バージョン | 上流の許諾文 |
|---|---|---|
| libjpeg-turbo | 3.1.2 | BSD 系の IJG / BSD-3-Clause、SIMD 部分は zlib（`LICENSE.md`、`README.ijg`） |
| libpng | 1.6.53 | PNG Reference Library License version 2（`LICENSE`） |
| libtiff | 4.7.1 | LibTIFF license（`LICENSE.md`） |
| zlib | 1.3.1 | zlib license（`LICENSE`） |

FFmpeg は追加・同梱しない。Windows 公式 slim 版、SkiaSharp、PDFium の組み込み依存の許諾文は各配布元の通知も参照する。T1-12 の配布準備では、完成した配布物に対応する全文通知を揃える。
