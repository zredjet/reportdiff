# T0-2 依存調査

確認日：2026-09-20。環境：macOS 26.5 / Apple Silicon（osx-arm64）、.NET SDK 10.0.401、.NET Runtime 10.0.12。

## 結果

T0-2 は未完了。依存候補の調査で CLAUDE.md ルール 10「依存の追加は MIT / Apache-2.0 / BSD のみ」と衝突する同梱ライブラリを確認し、依存追加前で停止した。技術スタックやライセンス条件は変更していない。

T0-1 の雛形については、通常の `dotnet build` が警告 0・エラー 0、`dotnet test` が終了コード 0 で成功。テストケース・ランナーの導入前なので、この成功は依存の疎通確認を意味しない。

| 確認項目 | 結果 |
|---|---|
| NuGet 上の候補の実在・最新安定版 | 確認済み。下表参照 |
| macOS arm64 / x64 のネイティブ資産 | 両方存在。実体のアーキテクチャを確認済み |
| 許可ライセンスだけでの依存構成 | 未解決。macOS の標準ランタイムに FFmpeg の静的リンクを確認 |
| プロジェクトへの依存追加 | 未実施 |
| PDF → 300dpi → BGR Mat の疎通テスト | 未実施 |
| OpenCV 各演算の疎通テスト | 未実施 |
| win-x64 自己完結・単一 exe の publish | 未実施 |
| 配布物のサイズと pdfium / OpenCvSharpExtern / libSkiaSharp の同梱 | 未確認。配布物を生成していない |
| Windows / Intel Mac の実機での動作 | 未確認 |

## NuGet の調査結果

公式 V3 Flat Container API の `https://api.nuget.org/v3-flatcontainer/<小文字のパッケージID>/index.json` で公開バージョンを取得し、プレリリースを除く最新バージョンの nupkg を取得した。ライセンス欄は nuspec の宣言値であり、ネイティブバイナリ内の全ライブラリが同じライセンスであることを意味しない。以下は採用済み依存の一覧ではない。

| パッケージ | 最新安定版 | nuspec のライセンス |
|---|---|---|
| [PDFtoImage](https://www.nuget.org/packages/PDFtoImage/5.4.0) | 5.4.0 | MIT |
| [OpenCvSharp4](https://www.nuget.org/packages/OpenCvSharp4/4.13.0.20260627) | 4.13.0.20260627 | Apache-2.0 |
| [OpenCvSharp4.runtime.win](https://www.nuget.org/packages/OpenCvSharp4.runtime.win/4.13.0.20260627) | 4.13.0.20260627 | Apache-2.0 |
| [OpenCvSharp4.runtime.osx.arm64](https://www.nuget.org/packages/OpenCvSharp4.runtime.osx.arm64/4.13.0.20260627) | 4.13.0.20260627 | Apache-2.0 |
| [OpenCvSharp4.runtime.osx.x64](https://www.nuget.org/packages/OpenCvSharp4.runtime.osx.x64/4.13.0.20260627) | 4.13.0.20260627 | Apache-2.0 |
| [YamlDotNet](https://www.nuget.org/packages/YamlDotNet/18.1.0) | 18.1.0 | MIT |
| [xunit.v3](https://www.nuget.org/packages/xunit.v3/4.0.1) | 4.0.1 | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.10.1) | 18.10.1 | MIT |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.0) | 4.0.0 | Apache-2.0 |

`xunit` 2.9.3 は NuGet で非推奨とされ、後継に `xunit.v3` が案内されているため、後継の安定版も調査した。テストランナー構成はまだ導入していない。

## 停止の根拠

OpenCvSharp の各ランタイム nupkg に同梱された `README.runtime.md` は、macOS の arm64 / x64 両版について FFmpeg の静的リンクを明記している。[公式ランタイムの説明](https://www.nuget.org/packages/OpenCvSharp4.runtime.win/4.13.0.20260627)にも同じ記載がある。[FFmpeg の公式ライセンス説明](https://ffmpeg.org/legal.html)は LGPL 2.1 以降を示している。

実際に nupkg 内のバイナリを取り出し、`file` と `strings` でも以下を確認した。

| パッケージ内の資産 | 形式 | 確認内容 |
|---|---|---|
| `runtimes/osx-arm64/native/libOpenCvSharpExtern.dylib` | Mach-O arm64 | `FFMPEG: YES`、`libswscale license: LGPL version 2.1 or later` |
| `runtimes/osx-x64/native/libOpenCvSharpExtern.dylib` | Mach-O x86_64 | `libswscale license: LGPL version 2.1 or later` |
| `runtimes/win-x64/native/opencv_videoio_ffmpeg4130_64.dll` | PE32+ x86-64 | `libswscale license: LGPL version 2.1 or later` |

Windows パッケージには `runtimes/win-x64/native/OpenCvSharpExtern.dll` も存在する。ただし、これはパッケージ内の確認であり、publish の成功や単一 exe への同梱を確認したものではない。

調査した nupkg の SHA-256：

```text
OpenCvSharp4.runtime.osx.arm64 4.13.0.20260627
485f8994f42130d76da122f4d02fba035d83f7e5586708cfef15a641a76302bf

OpenCvSharp4.runtime.osx.x64 4.13.0.20260627
96ef2a4bbe3a66b458a974d952594edb365cee63b9875fd3a88716286fac8190

OpenCvSharp4.runtime.win 4.13.0.20260627
907fc3f682b1d430ef47f6782fb2c78a8337ac9bed2e5e7f8f01b9585c42c830
```

`OpenCvSharp4.runtime.osx.arm64.slim` と `OpenCvSharp4.runtime.osx.x64.slim` の V3 バージョン一覧は HTTP 404 だった。この 2 つのパッケージ ID を指定して回避することはできなかった。他の配布元の全パッケージを調査したという意味ではない。

調査用の nupkg は一時ディレクトリに置いた。プロジェクトへの PackageReference 追加・ネイティブライブラリの取り込み・ランタイムの実行は行っていない。採用した依存がないため、`THIRD_PARTY_NOTICES.md` の表は空のまま。

## 再開の選択肢

1. **推奨：許可ライセンスの条件を維持し、FFmpeg を含まないランタイム構成を用意する。** Windows は公式 `OpenCvSharp4.runtime.win.slim` が候補。macOS は FFmpeg など不要なモジュールを無効にしたビルドまたは同等の配布物を検討する。独自ビルドの追加は現行タスクに明記されていないため、方針を確認してから行う。Windows slim を含め、最終的な構成の同梱ライブラリ確認と疎通確認は別途必要。
2. **LGPL を許可する例外を明示する。** 例外の対象（macOS の開発・テスト用のみか、Windows 配布物も含むか）を決めてから、ライセンス条件・通知・配布方法を整理し、指定ランタイムによる T0-2 を再開する。

方針決定後は T0-2 の依存追加から再開する。テスト 2 件、警告ゼロのビルド、Windows publish、配布サイズとネイティブ同梱の確認がすべて成功するまで完了チェックを付けない。T0-3 と Phase 1 は未着手。
