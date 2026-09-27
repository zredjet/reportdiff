# 末尾の片側ページと独立原因の固定入力

2026-09-26。`UnpairedComponentProbe`で別生成した自作Type3の16ケース32PDF・111物理ページ。機密帳票・OSフォントは使用しない。300dpiの元画素、文字行列、C/DとPython参照を[事前診断](../../../../docs/verification/t3-1c-unpaired-components.md)で照合した入力を、PDFのバイトを変えずに製品テストへ固定した。

`sha256.json`はPDFのハッシュ、`fixtures.json`は生成時の本文正解とハッシュ、`expectations.json`は32方向の診断上の集約入力・期待・元候補ID。推定へ正解行列を渡さない。末尾の片側ページはクラスタ／構造0の未比較を保持する。

追加成立の8方向は `independent-terminal`、`independent-opposite`、`neutral-between`、`independent-tone` の両方向。前3者は11→2、最後は実PDFの濃淡差2,462px・1クラスタを保持して12→3。元候補の順番はA→BとB→Aの列挙によって異なるため、非送り候補のIDは固定診断の `nonflow_ids` に照合する。常にL2とは仮定しない。

単一R11は6→1、単一の3→4ページ連鎖は9→1の既存成功。帯外の塗り3,570px、BGR(254,254,254)の30px、固定部変更、送り帯自体の555→556、反復本文、同一連鎖の二原因、余分な末尾ページを負例として保持する。濃淡・数値・固定部の検出期待を弱めない。

製品へ接続した範囲、予算・設定・保存・互換・資源の確認は[接続検証](../../../../docs/verification/t3-1c-unpaired-integration.md)を参照。
