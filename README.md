# ReportDiff 開発キット

帳票（PDF・画像）の画像比較ツール「ReportDiff」を、Claude Code に実装してもらうための一式。

## 開発用ビルド

macOS（Apple Silicon）では初回に FFmpeg を含まないローカルランタイムを生成する。Intel Mac は対象外。必要なツールと詳細は [ネイティブ依存の準備](docs/NATIVE_RUNTIME.md) を参照。

```bash
python3 tools/build-macos-runtime.py
dotnet build
dotnet test
```

現在は T0-2 の依存疎通確認まで完了。比較コマンドの機能は未実装。

## 中身

| ファイル | 役割 |
|---|---|
| `CLAUDE.md` | Claude Code が起動時に自動で読む指示書（技術スタック、コマンド、ルール） |
| `docs/SPEC.md` | 仕様の正本（アルゴリズム、設定、出力形式、ゴールデンセット、設計判断の記録） |
| `docs/TASKS.md` | 作業順序と受け入れ条件、Windows 確認リスト |
| `reference/prototype.py` | 比較コアの参照実装（Python + OpenCV）。既定値で全ゴールデンケース成功を確認済み |
| `reference/golden/` | ゴールデンケース 37 件の入力 PNG と期待値 `expected.json` |

## 始め方

1. macOS に .NET 10 SDK を入れる
2. このディレクトリをリポジトリのルートにして `git init`
3. （任意）参照実装を動かす

   ```bash
   pip install opencv-python-headless numpy pillow
   python reference/prototype.py
   ```

4. このディレクトリで Claude Code を起動し、下の指示を送る

## Claude Code への最初の指示

```
CLAUDE.md、docs/SPEC.md、docs/TASKS.md を読んでください。
読み終えたら、理解した内容と不明点を短くまとめてください。
そのあと docs/TASKS.md の Phase 0 を T0-1 から順に進め、
T0-2（依存の疎通確認）が終わったら結果を報告して止まってください。
```

T0-2 は「macOS で PDFium と OpenCV が動き、Windows 用の exe が作れるか」の確認で、この計画の最大のリスク。ここを通過したら、次の指示で Phase 1 に進める。

```
docs/TASKS.md の Phase 1 を T1-1 から順に進めてください。
T1-4（ゴールデンテストと最適化）が終わったら、テスト結果と計測結果を報告して止まってください。
```

## 注意

- 実帳票は機密なので `samples/private/` に置き、コミットしない
- アルゴリズムや既定値を変えるときは、`docs/SPEC.md`・`reference/prototype.py`・`reference/golden/expected.json` を必ず同時に更新する
- `reference/golden/` を作り直すとき：`python reference/prototype.py --dump reference/golden`（日本語ケースは実行環境のフォントで字形が変わるので、作り直したら期待値も変わる）
