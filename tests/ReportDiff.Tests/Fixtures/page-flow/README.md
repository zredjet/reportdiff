# ページ送りの固定入力

T3-1cの独立検証で生成・描画・参照比較済みの合成PDFを使用する。機密帳票・OSフォントは含まない。元は `out/t3-1c-probe/type3-final`、`candidate-fixtures-final`、`causal-fixtures-final`。ファイルを変更した場合は過去の検証の継承とは扱わず、検出・見送り期待と参照比較を再確認する。

- R10: 2→2ページ、R11: 2→3ページ、chain3: 3ページの単一原因。
- paired-content-tone: 送りと同時に本文の色変更。内容クラスタを残す。
- two-inserts: 第二の原因。単一原因に集約しない。
- flow-number-change: 555→556。末尾写像が作れず通常比較に戻して検出を維持する。

SHA256は `sha256.json`。生成器は `tools/ReportDiff.PageFlowProbe`、段階別の記録は `docs/verification/t3-1c-*.md` を参照。
