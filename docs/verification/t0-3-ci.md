# T0-3 CI 確認

確認日：2026-09-21（JST）。対象は Public リポジトリ `zredjet/reportdiff`。

- ワークフロー：[CI](../../.github/workflows/ci.yml)
- 実行：[GitHub Actions #35518444710](https://github.com/zredjet/reportdiff/actions/runs/35518444710)
- 対象コミット：`e8d1c06da3ce742e2b892a425f281755268c76ef`
- 起動条件：`master` への push

## 結果

**T0-3 完了。** GitHub Actions の両ジョブが成功し、ログで以下を確認した。

| ジョブ | 実行環境 | .NET SDK | `dotnet build` | `dotnet test` |
|---|---|---|---|---|
| Windows x64 | Windows Server 2025、win-x64 | 10.0.401 | 成功、警告 0・エラー 0 | 2 / 2 成功、失敗 0・スキップ 0 |
| macOS Apple Silicon | macOS 26.6、osx-arm64 | 10.0.401 | 成功、警告 0・エラー 0 | 2 / 2 成功、失敗 0・スキップ 0 |

両環境の .NET Runtime は 10.0.12。ジョブ全体の所要時間は Windows 1 分 29 秒、macOS 6 分 36 秒。macOS のうち約 6 分は FFmpeg なしのランタイムの生成で、生成スクリプトに含まれるソースの SHA-256、アーキテクチャ、リンク先・シンボルの検査も成功した。

## 確認範囲

Windows は公式 `OpenCvSharp4.runtime.win.slim` を復元する。macOS は固定ソースから FFmpeg なしのローカル NuGet を生成してから復元する。ランナーのアーキテクチャが Windows x64 / macOS ARM64 と一致することをジョブ内で検査する。

テストは T0-2 の 2 件。PDF のメモリ上での生成・ラスタライズ・画素確認と、OpenCvSharp の画像演算・画像コーデックを検証する。Intel Mac のテストは行わない。

GitHub ホストランナー上でのビルド・疎通確認であり、Windows の配布 exe、.NET 未導入 PC、日本語パス、実帳票の処理時間、SmartScreen などの実機確認は含まない。実機の残件は [TASKS.md](../TASKS.md) の Windows 確認リストで管理する。
