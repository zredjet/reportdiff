# T1-5 画像読み込みの確認

確認日：2026-09-21、macOS / Apple Silicon、.NET SDK 10.0.401。

**T1-5 完了。** `ReportDiff.Pdf` に画像入力を実装した。ビルド警告 0・エラー 0、既存 176 件と画像入力 58 件の計 234 件のテストが成功。

## API と動作

- `InputFormatDetector.Detect`：先頭のシグネチャで PNG・JPEG・BMP・TIFF・PDF を識別する。未知の形式は `Unknown`。拡張子は使わない。
- `ImageReader.Read(path, imageDpi)`：ファイルを .NET の `File.ReadAllBytes` で読み、バイト列を OpenCvSharp の `ImDecode` に渡す。ネイティブにパスは渡さない。
- `ImageReader.Decode(bytes, imageDpi)`：メモリ上の画像を読み、白背景の BGR 8bit・3 チャンネルへ変換する。TIFF は先頭ページのみ。PDF は [T1-6 の PdfReader](t1-6-pdf.md) で読み込むため、この API では受け付けない。
- 戻り値の `LoadedImage` が `Pixels` の所有権を持つ。呼び出し側で `using` によって破棄する。
- `Dpi` には指定された `imageDpi` を保持する（既定 300、範囲 72〜1200）。設定を使う呼び出し側は `AppSettings.ImageDpi` を渡す。DPI による画像の拡大縮小は行わない。
- 未対応の形式、破損したデータ、読み込めないパスは日本語の `ImageReadException` とする。

対応する画素深度は 8bit / 16bit の符号なし整数。16bit は 0〜65535 を 0〜255 に換算し、透過画像は合成後に 8bit 化する。浮動小数点・符号付き整数の TIFF は、輝度範囲を推定せず日本語エラーとして拒否する。

PNG の透明度は未乗算として白背景へ合成する。TIFF は OpenCV 4.13.0 の 8bit デコードが乗算済みの色を返すことを考慮する。16bit TIFF は先頭 IFD の `ExtraSamples` で乗算済み／未乗算を判別する。Classic TIFF と BigTIFF の両バイト順に対応し、二重に透明度を掛けることを避ける。[OpenCV の TIFF デコーダー](https://github.com/opencv/opencv/blob/4.13.0/modules/imgcodecs/src/grfmt_tiff.cpp)、[LibTIFF の RGBA 読み込み](https://libtiff.gitlab.io/libtiff/functions/TIFFReadRGBAImage.html) を確認した。

## テスト

すべて合成データを使用する。TIFF のアルファと複数ページの入力は .NET だけで作り、エンコーダーとデコーダーに共通する誤りを検出できるようにした。

| 確認項目 | 件数 |
|---|---:|
| PNG・JPEG・BMP・TIFF の BGR 画素値、形式、DPI | 4 |
| 各形式のグレースケール → BGR | 4 |
| 8bit / 16bit PNG の透明・半透明・不透明画素 | 2 |
| 16bit PNG / TIFF のカラー・グレースケール | 4 |
| TIFF の透明度（8/16bit、乗算済み/未乗算、両バイト順、Classic/BigTIFF） | 16 |
| 2 ページ TIFF の先頭だけを読み込む | 4 |
| 日本語・空白パス、偽の拡張子、読み込み後のファイル削除 | 4 |
| シグネチャ判定、短いヘッダー、未対応形式 | 11 |
| 破損・空データ・画像以外の入力のエラー | 7 |
| 存在しないファイル・DPI 範囲外 | 1 |
| 浮動小数点 TIFF の明示的な拒否 | 1 |

完全透明は白、半透明は白との合成色、不透明は元の色になることを画素値で検証した。JPEG だけは非可逆圧縮を考慮して色差を最大 3 とし、それ以外の確認画素は一致を要求する。

## 残る範囲

PDF 読み込みは [T1-6 確認結果](t1-6-pdf.md)、サイズ・ページ対応は [T1-7 確認結果](t1-7-pages.md) を参照。結果出力と CLI への接続は未着手。Windows での新しいテストと日本語パスの実機確認は未実施で、[Windows 確認リスト](../TASKS.md) に残している。

再実行はリポジトリのルートで `dotnet build` と `dotnet test`。macOS の初回準備は [ネイティブ依存の準備](../NATIVE_RUNTIME.md) を参照。
