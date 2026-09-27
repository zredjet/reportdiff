# V-03：現行開発版のWindows発行・配布物静的検査

2026-09-27。[進捗台帳](../PROGRESS.md)のV-03について、macOS arm64からWindows x64用のexeを発行し、依存・許諾文・ZIPを検査した。**ローカルでの発行・静的検査は完了。Windows上の実行と公開対象コミットの両OS CIは未確認のため、V-03全体は未完了。** T3-1b／T3-1cの完了・コミット・公開にはしない。

## 検証したもの

全2,375テスト成功時の製品・テスト・固定入力544ファイルは、発行前後ともSHA-256が一致した。これとは別に、製品ソース・プロジェクト・発行プロファイル・依存設定・許諾文・梱包ツール203ファイルを固定し、終了時の一致を確認した。製品コード・比較式・期待値・依存・バージョンの変更はない。

ソースはローカルの未コミット開発版。HEADは `6a899d1a21a1023cf807c8b0342bac672e4dc62d` のままだが、このexeをHEADや公開済みv0.1.4から作った実行物とは扱わない。表示バージョンは既存の0.1.4で、アーカイブ名を `reportdiff-development-validation-win-x64.zip` とした。

| 検査 | 結果 |
| --- | --- |
| publish | Release・win-x64・自己完結・単一exe。終了コード0、警告0・エラー0。AOT／トリミングなし |
| exe | 157,345,349 bytes、Windows x64 PE、埋め込み192ファイル |
| 現行製品の埋め込み | CLI／Core／Pdf／Reportの4アセンブリが、今回の発行で生成したDLLと全バイト一致 |
| ネイティブ依存 | PDFium／OpenCvSharpExtern／SkiaSharpの3DLLがWindows x64で、許諾確認済みハッシュと一致 |
| 管理依存・ランタイム | PdfPigの7DLLを確認。依存一覧・.NET 10.0.12が `licenses/dependencies.json` と一致 |
| 許諾文 | `licenses/sources.json` のハッシュと一致。本体LICENSE・第三者通知・原文を同梱 |
| 除外対象 | FFmpeg・他OS用のネイティブ資産なし。別置きDLL・PDBのZIP混入なし |
| ZIP | 350ファイル、67,367,652 bytes。重複名なし、CRC・全マニフェストのサイズ／SHA-256が一致 |
| ZIPと梱包入力 | 梱包前に固定した348入力の集合・全バイトが一致。自動生成するmanifest／bundle-inspectionの2件を加えて350件 |
| 設定・画像 | rows／page-flow設定例と文書を同梱。READMEの3画像が元ファイルと一致し、確認用raw overlay画像も含む |

ZIPのSHA-256：

```text
274fdcebab8c6cf99deb909e480e8a6518362e4cc020dffc5e1fba94e0b4bb31
```

初回のpublishは制限環境からNuGetの脆弱性情報を取得できず、NU1900で停止した。通常のネットワーク権限で同じコマンドを再実行して成功した。NuGet監査・警告のエラー扱いを無効にして通したものではない。初回と成功時のログを両方残した。

## 証拠と再現

[機械可読記録](t3-1c-windows-static.json)にexe・ZIP・製品アセンブリ・検証ソース・ログ・入力スナップショットのハッシュを保存した。詳細は `out/t3-1c-windows-static/`。ZIPはローカル検証用であり、GitHubの公開物を変更していない。

```sh
dotnet publish src/ReportDiff.Cli -p:PublishProfile=win-x64 \
  -o out/new-windows-validation/publish --disable-build-servers -m:1
python3 tools/inspect-win-bundle.py out/new-windows-validation/publish/reportdiff.exe
python3 tools/package-win.py out/new-windows-validation/publish/reportdiff.exe \
  out/new-windows-validation/reportdiff-development-validation-win-x64.zip
```

同梱文書は梱包時点のスナップショット。このV-03の結果文書・機械可読記録と、台帳／TASKSへの結果追記はZIP作成後のため、この検証用ZIPには含まれない。公開時には確定したソース・文書から別途作り直す。

## 残る確認

全2,375テストは今回再実行していない。以前のmacOS全回帰と今回の発行元544ファイルが同じことを確認した。静的検査ではWindowsのネイティブ呼び出し、PDF描画、日本語パス、終了コード、HTML操作、SmartScreen等の動作は証明しない。

公開対象コミットの両OS CIと、[Windows実機確認リスト](../TASKS.md#windows-確認リスト人が実機で行う)を別途確認する。ブラウザーの保留・R-01〜R-03の未成立期待・実帳票評価の状態も維持する。
