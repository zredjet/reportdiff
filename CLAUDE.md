# ReportDiff

業務帳票（PDF・画像）の出力結果を画像として比較し、相違箇所を「まとまり」単位で報告する CLI ツール。
開発は macOS（Apple Silicon）、実行は主に Windows（x64）。Intel Mac は対象外。

## まず読むもの

- `docs/SPEC.md` … 仕様。アルゴリズム・設定・出力形式の正本。着手するタスクに対応する章を必ず読む
- `docs/TASKS.md` … 作業順序と受け入れ条件。上から順に進め、完了したらチェックを付ける
- `reference/prototype.py` … 比較コアの参照実装（Python）。全ゴールデンケース成功を確認済み。C# 実装はこれと同じ結果を出すこと
- `reference/golden/` … ゴールデンケースの入力 PNG と期待値 `expected.json`

## 技術スタック（勝手に変えない。変えたいときは理由を添えて先に相談）

- C# / .NET 10、`Nullable` 有効、警告はエラー扱い
- PDF のラスタライズ: PDFtoImage（PDFium + SkiaSharp）
- 画像処理: OpenCvSharp4（Windows は公式 `OpenCvSharp4.runtime.win.slim`、macOS は `tools/build-macos-runtime.py` で生成する FFmpeg なしの arm64 / x64 ローカルランタイム。詳細は `docs/NATIVE_RUNTIME.md`）
- 設定: YamlDotNet ／ JSON: System.Text.Json ／ テスト: xUnit
- PDF テキスト層（Phase 2）: PdfPig
- パッケージ ID とバージョンは NuGet で実在を確認してから追加する（上記は 2026-09 時点の調査結果）

## コマンド

```bash
dotnet build                                   # 警告ゼロであること
dotnet test                                    # 全件成功であること
dotnet run --project src/ReportDiff.Cli -- compare A.pdf B.pdf --out out
python reference/prototype.py                  # 参照実装のゴールデンケース
# Windows 用の単一 exe（macOS 上で実行できる。Native AOT は使わない）
dotnet publish src/ReportDiff.Cli -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## ソリューション構成

```
src/ReportDiff.Core     比較コア。Mat を受けてマスクとクラスタを返す。ファイル・PDF・コンソールに触れない
src/ReportDiff.Pdf      PDF と画像の読み込み、正規化
src/ReportDiff.Report   result.json、重ね描き画像、切り出し画像、HTML
src/ReportDiff.Cli      引数処理、終了コード
tests/ReportDiff.Tests  ゴールデンテスト、参照実装との一致テスト、結合テスト
```

## ルール

1. アルゴリズムの式と既定値は `docs/SPEC.md` の 5 章と 7 章が正。変える場合はゴールデンテスト全件成功が条件で、`docs/SPEC.md`・`reference/prototype.py`・`reference/golden/expected.json` を同時に更新し、SPEC 12 章に理由を追記する
2. 「無視必須」のテストを通すために「検出必須」のテストを緩めない。逆も同じ。両立しないときは作業を止めて報告する
3. SPEC 5.6 の「既知の限界」は仕様。直そうとして他のケースを壊さない
4. 長さの設定値はすべて mm。px への換算は `Units` の 1 か所だけで行う
5. ファイルのパスをネイティブライブラリに渡さない。.NET で読み書きし、バイト列かストリームで受け渡す（`Cv2.ImRead` / `Cv2.ImWrite` は使わず `ImDecode` / `ImEncode`）。Windows の日本語パス対策
6. `Mat` など `IDisposable` は必ず `using` で解放する
7. PDFium はスレッドセーフではない。ラスタライズは逐次。比較コアはページ単位または独立したグループ単位で並列化できる。入力は読み取り専用、出力領域は分離し、例外時も全 worker の終了後に Mat を解放する。ページとグループの入れ子で並列数を増やさない
8. レポート HTML は外部 CDN・ネットワーク参照を一切使わない（オフライン環境で開く）
9. 吸収・除外したもの（位置ずれの吸収数、除外領域、捨てたノイズ数、サイズ差の余白埋め）は必ず `result.json` とレポートに出す
10. 依存の追加は MIT / Apache-2.0 / BSD のみ。AGPL・GPL（MuPDF、PyMuPDF、iText、Ghostscript）と ImageSharp（Six Labors Split License）は不可。追加したら `THIRD_PARTY_NOTICES.md` を更新する
11. 識別子は英語。コメント・ドキュメント・CLI のメッセージ・レポートの文言は日本語。ソースとレポートは UTF-8、`Console.OutputEncoding` は UTF-8、パス結合は `Path.Combine`
12. 実帳票は機密。`samples/private/` に置き、コミットしない（`.gitignore` 済みにする）。テストには合成データだけを使う

## 進め方

- 1 タスク = 1 コミット。コミット前に `dotnet build` と `dotnet test` を通す
- タスクの受け入れ条件を満たしたら `docs/TASKS.md` のチェックを付ける
- macOS では確認できない点（Windows 固有の挙動）は `docs/TASKS.md` の「Windows 確認リスト」に追記する
- 仕様にない判断が必要になったら、選択肢と推奨を示して確認を取る。黙って仕様を広げない
