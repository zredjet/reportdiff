# 末尾片側ページへ続く共有原因の固定入力

2026-09-27。前段の `out/t3-1c-unpaired-shared-diagnosis/diagnosis/inputs` から14組28PDFをバイト不変で固定した。`sha256.json` をCLIテストで検査する。

`expectations.json` の input/adoptions は元画像・参照比較器と照合済みの診断結果、candidate は独立した Python shared_cause_model の terminal=True による出力。製品出力から生成した期待値ではない。

before8 / chain / tone の両方向6件だけが追加接続対象。single の両方向は既存成功を維持。original は前側支持1行で未成立の正例のまま、残りの18方向は負例として採用を見送る。tone の1,390画素・内容1件を保持する。元行テキスト、ページ配置の詳細と画像検証は docs/verification/t3-1c-unpaired-shared-diagnosis.md を参照。
