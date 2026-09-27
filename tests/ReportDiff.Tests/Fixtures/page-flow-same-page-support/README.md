# 同一ページ二原因の支持対照

15ケース、A/B各2ページの30PDF。`geometry.json` は生成前に定めた容量・挿入位置・変更・集約期待、`sha256.json` は固定PDFのハッシュ、`expected.json` はPythonの独立集約結果。

`same-page-two` の2PDFは既存 `page-flow-shared/same-page-two` と全バイト一致する。`grouped: true` と `existing_unmet: true` を併記し、検出期待を未成立のまま残す。回帰テストで現在の安全な見送りを確認しても、要求を満たしたことにはしない。

容量8以上の正例は別入力である。中間支持1行／薄い支持行の負例では候補C/Dだけが成立し、製品は文書全体を基準比較へ戻すため、独立候補の7件を製品期待に流用しない。

[設計](../../../../docs/planning/t3-1c-same-page-support.md)、[検証](../../../../docs/verification/t3-1c-same-page-support.md)、[生成・照合コマンド](../../../../tools/ReportDiff.PageFlowProbe/README.md#同一ページ二原因の支持境界)を参照。
