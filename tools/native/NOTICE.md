# ReportDiff の macOS OpenCvSharp ランタイム

OpenCvSharp / OpenCV の比較・画像入出力用のローカルビルド。
FFmpeg、動画入出力、GUI、contrib は含まない。

- OpenCvSharp 4.13.0.20260627: Apache-2.0。`OpenCvSharp-LICENSE` を参照。
- OpenCV 4.13.0: Apache-2.0。`OpenCV-LICENSE` を参照。
- 画像コーデックと圧縮処理の許諾文は `libjpeg-turbo/`、`libpng/`、`libtiff/`、`zlib/` に同梱する。

ビルド手順とソースのハッシュは `tools/build-macos-runtime.py` を参照。
公式 NuGet の macOS ランタイムとは異なるパッケージ ID を使用する。
