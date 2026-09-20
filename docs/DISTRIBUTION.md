# Windows x64 配布手順

## 利用する環境

配布対象は Windows x64。自己完結の `reportdiff.exe` に .NET 10 ランタイム、PDFium、OpenCvSharpExtern、SkiaSharp、PdfPig を含める。実行に必要なアプリのバイナリは exe 1 個で、配布 ZIP には README・設定例・ライセンス通知も同梱する。

ネイティブ DLL は起動時に `%TEMP%/.net` 以下へ展開される。`DOTNET_BUNDLE_EXTRACT_BASE_DIR` を設定すると展開先を変更できる。実行ユーザーが書き込め、他のユーザーが改変できない領域を使う。この動作は [.NET の単一ファイル配布](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#native-libraries)に従う。

今回の exe は未署名。SmartScreen・ウイルス対策ソフト・Windows Server の Media Foundation を含む実機条件は [Windows 確認リスト](TASKS.md#windows-確認リスト人が実機で行う)で管理する。T0-2 の動作 OK 報告は完成版 exe の実機確認を代替しない。

## 発行する

ソースリポジトリのルートで実行する。.NET 10 SDK、ZIP の検証・作成には Python 3.12 以降が必要。macOS は先に [ネイティブ依存](NATIVE_RUNTIME.md)を生成する。Windows は `out/packages` を空フォルダとして作っておく。

```bash
dotnet build
dotnet test
dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 -o out/publish/win-x64
python3 tools/inspect-win-bundle.py out/publish/win-x64/reportdiff.exe
python3 tools/package-win.py out/publish/win-x64/reportdiff.exe out/dist/reportdiff-win-x64.zip
```

Windows の Python ランチャーを使う場合は `python3` を `py -3` に読み替える。ZIP が既にある場合は上書きせずエラーにするため、新しいファイル名を指定する。

`win-x64.pubxml` は Release・win-x64・自己完結・単一ファイル・ネイティブ埋め込みを指定する。Native AOT、トリミングは無効。.NET ランタイムは同梱通知に対応する 10.0.12 に固定している。プロファイルを使わない同等の発行コマンドは次のとおり。

```bash
dotnet publish src/ReportDiff.Cli -c Release -r win-x64 --self-contained \
  -p:RuntimeFrameworkVersion=10.0.12 \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false -p:PublishAot=false -o out/publish/win-x64
```

通常の publish ディレクトリには PDB も出る。`package-win.py` は exe と文書・設定例・許諾文だけを ZIP に入れる。PDB や別置きの DLL を配布する必要はない。

## ZIP の内容と検証

```text
reportdiff.exe
README.md
THIRD_PARTY_NOTICES.md
examples/settings.yaml
docs/                     # 仕様・手順・確認記録。文書間のリンクも保持
licenses/                 # 原文・出典・確認した依存バージョン
bundle-inspection.json    # exe・必須 DLL のハッシュ、依存・ランタイム情報
manifest.json             # manifest 自身を除く全ファイルのサイズ・SHA-256
```

梱包時に次を検証する。

- exe 内の pdfium / OpenCvSharpExtern / libSkiaSharp が Windows x64 の DLL であり、確認済みの SHA-256 と一致する。
- PdfPig が依存に含まれる場合、管理 DLL 7 件がすべて埋め込まれている。
- FFmpeg 資産・他 OS のネイティブ資産が含まれていない。OpenCvSharpExtern に動画用ラッパーや FFmpeg 実装由来の指定シンボルがない。
- 埋め込みの依存一覧・ランタイムの版が `licenses/dependencies.json` と一致する。
- ライセンス原文が `licenses/sources.json` に記録したハッシュと一致する。
- 作成した ZIP に欠損や CRC エラーがなく、全ファイルの SHA-256 が一致する。

依存を更新するときは、許諾文の再取得・版とネイティブ DLL の対応確認を行ってから `sources.json` と `dependencies.json` を更新する。単に検証を通すために期待値を差し替えない。SDK が更新されてもランタイムは固定のため、ランタイム更新時には発行プロファイルと通知を一緒に更新する。

この検証は Windows での実行確認ではない。作成した ZIP を実機に展開し、[README の比較例](../README.md#windows-で使う)、日本語パス・終了コード・HTML 表示等を確認する。現在の結果は [T2-3 検証記録](verification/t2-3-text.md)、配布の初期確認は [T1-12 検証記録](verification/t1-12-distribution.md)を参照。
