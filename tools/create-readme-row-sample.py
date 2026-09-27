#!/usr/bin/env python3
"""自作の架空帳票。業務データ、外部帳票、検証 fixture は使用しない。"""
import argparse
import hashlib
import json
import shutil
from pathlib import Path

from reportlab.lib.colors import HexColor
from reportlab.lib.pagesizes import A4
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas
from pypdf import PdfReader

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("output", type=Path, help="未作成の出力フォルダ")
parser.add_argument("--fonts-dir", required=True, type=Path, help="BIZ UDGothic Regular/BoldとOFL.txtの保存先")
args = parser.parse_args()
font_hashes = {
    "BIZUDGothic-Regular.ttf": "709fcd41e3209fb765da750472f55ccdf925653e9fa7e1eb007cb65c8f749c75",
    "BIZUDGothic-Bold.ttf": "98a528b6b638463041968783cc0f63adaf4cdc26f5398afed68bab712d1113f3",
    "OFL.txt": "e753d7155d53c747d037a445e584c8ecfca6dd79846db610417e282a736b28bc",
}
for filename, expected in font_hashes.items():
    if hashlib.sha256((args.fonts_dir / filename).read_bytes()).hexdigest() != expected:
        parser.error(f"掲載サンプルのフォントとSHA-256が一致しません: {filename}")
ROOT = args.output
ROOT.mkdir(parents=True, exist_ok=False)
shutil.copyfile(args.fonts_dir / "OFL.txt", ROOT / "BIZUDGothic-OFL.txt")
for suffix, name in [("Regular", "JP"), ("Bold", "JPBold")]:
    pdfmetrics.registerFont(TTFont(name, str(args.fonts_dir / f"BIZUDGothic-{suffix}.ttf")))

# 明細番号は業務上の固定ID。途中挿入でも既存番号を振り直さない。
ROWS = [
    ("010", "デスクトップパソコン", "省スペース型 / メモリ16GB", "4", "台", "情報管理室"),
    ("020", "液晶ディスプレイ", "23.8型 / 高さ調整対応", "4", "台", "情報管理室"),
    ("030", "無線キーボード", "日本語配列 / テンキー付", "4", "台", "情報管理室"),
    ("040", "無線マウス", "静音 / 3ボタン", "4", "個", "情報管理室"),
    ("050", "USB接続ハブ", "4ポート / 電源供給対応", "4", "個", "情報管理室"),
    ("060", "LANケーブル", "カテゴリ6 / 3m / 青", "10", "本", "総務課"),
    ("070", "電源タップ", "6個口 / 3m / 雷対策", "6", "個", "総務課"),
    ("080", "ヘッドセット", "USB接続 / 両耳タイプ", "8", "台", "営業支援課"),
    ("090", "書類保管ボックス", "A4判 / フタ付 / グレー", "12", "個", "総務課"),
    ("100", "ラベルプリンター", "据置型 / 24mm幅対応", "1", "台", "総務課"),
    ("110", "ラベルテープ", "白地・黒文字 / 24mm", "5", "巻", "総務課"),
    ("120", "コピー用紙", "A4判 / 500枚×5冊", "3", "箱", "総務課"),
    ("130", "クリアホルダー", "A4判 / 100枚入", "2", "箱", "営業支援課"),
    ("140", "インデックスシール", "中サイズ / 青 / 120片", "5", "袋", "総務課"),
]
ADDED = ("035", "バーコードスキャナー", "USB接続 / 1次元コード", "2", "台", "物流管理課")
REVISED = ROWS[:3] + [ADDED] + [row for row in ROWS[3:] if row[0] != "100"]
W, H = A4
INK = HexColor("#222222")
MUTED = HexColor("#555555")
RULE = HexColor("#686868")
LIGHT = HexColor("#eeeeee")

def make_pdf(filename, rows):
    c = canvas.Canvas(str(ROOT / filename), pagesize=A4, pageCompression=1, invariant=1)
    c.setTitle("発注明細書 | 架空の業務帳票サンプル")
    c.setAuthor("ReportDiff synthetic sample")
    c.setSubject("README掲載検討用の自作デモ。実在の企業・取引とは関係ありません。")
    def text(value, x, y, size=9, bold=False, color=INK, align="left"):
        c.setFillColor(color)
        c.setFont("JPBold" if bold else "JP", size)
        draw = {"left": c.drawString, "right": c.drawRightString, "center": c.drawCentredString}[align]
        draw(x, H-y, value)
    def line(x1,y1,x2,y2,width=.45,color=RULE):
        c.setStrokeColor(color); c.setLineWidth(width)
        c.line(x1,H-y1,x2,H-y2)
    def box(x,y,w,h,fill=None,width=.45):
        c.setLineWidth(width); c.setStrokeColor(RULE)
        if fill: c.setFillColor(fill)
        c.rect(x,H-y-h,w,h,stroke=1,fill=bool(fill))

    text("発 注 明 細 書", W/2, 58, 22, True, align="center")
    line(198,71,397,71,.8)
    text("発注番号  PO-2026-0927", 555,94,8.5,align="right")
    text("発 注 日  2026年9月27日", 555,109,8.5,align="right")
    text("東都オフィス用品株式会社 御中", 40,110,12,True)
    line(40,120,325,120,.65)
    text("下記のとおり、備品を発注いたします。",40,141,9)

    text("青葉事務サービス株式会社",354,145,10.5,True)
    text("管理本部 総務課",354,161,9)
    text("東京都千代田区サンプル町1-2-3",354,177,8)
    text("担当：総務購買係",354,193,8)

    box(40,171,284,42)
    box(40,171,62,42,LIGHT)
    text("件　名",71,197,9,bold=True,align="center")
    text("本社事務所 備品補充（10月分）",114,197,10)

    for y,label,value in [(239,"希望納期","2026年10月9日（金）"),(259,"納入場所","本社2階  総務課受付"),(279,"発注条件","単価は年間購買契約による。分納可。")]:
        text(label,40,y,8.5,True)
        text(value,101,y,9)
        line(40,y+6,555,y+6,.35,HexColor("#aaaaaa"))

    xs=[40,77,218,393,432,463,555]
    top=305
    header_h=25
    row_h=24
    bottom=top+header_h+row_h*len(rows)
    box(40,top,515,header_h,LIGHT)
    # 明細本文は罫線を省いた一覧形式。見出しと末尾に罫線を付ける。
    for x in xs[1:-1]: line(x,top,x,top+header_h)
    line(40,bottom,555,bottom)
    labels=["明細","品　名","規格・仕様","数量","単位","使用部署"]
    for i,label in enumerate(labels):
        text(label,(xs[i]+xs[i+1])/2,top+16,8.5,True,align="center")
    for index,row in enumerate(rows):
        y=top+header_h+row_h*index
        text(row[0],58.5,y+15,8,align="center")
        text(row[1],84,y+15,8.5)
        text(row[2],225,y+15,8)
        text(row[3],425,y+15,9,align="right")
        text(row[4],447.5,y+15,8.5,align="center")
        text(row[5],470,y+15,8.5)

    text("備 考",40,692,9,True)
    text("・納品書には発注番号と明細番号を記載してください。",40,709,8)
    text("・同等品への変更は、事前に総務購買係へご連絡ください。",40,724,8)
    text("・梱包は使用部署ごとに分け、部署名を明記してください。",40,739,8)
    for i,label in enumerate(["承認","確認","起票"]):
        x=402+i*51
        box(x,688,51,65)
        box(x,688,51,18,LIGHT)
        text(label,x+25.5,700,8,align="center")
    line(40,775,555,775,.4)
    text("帳票サンプル / 記載の企業・住所・取引はすべて架空です。",40,791,7,color=MUTED)
    text("1 / 1",555,791,8,color=MUTED,align="right")
    c.showPage(); c.save()

for filename,rows in [("order-before.pdf",ROWS),("order-after.pdf",REVISED)]:
    make_pdf(filename,rows)
    doc=PdfReader(ROOT/filename)
    assert len(doc.pages)==1
    content=doc.pages[0].extract_text()
    assert "発 注 明 細 書" in content
    assert all(row[1] in content for row in rows)
    if filename=="order-after.pdf":
        assert "ラベルプリンター" not in content
    else:
        assert "バーコードスキャナー" not in content

manifest={"format":"A4 portrait, embedded Japanese text, one page per file", "case":"本社事務所の備品補充発注を見直し", "inserted":ADDED, "deleted":ROWS[9], "unchanged_rows":13, "source":"Original synthetic data and layout; no existing fixture reused", "font_source":"https://github.com/google/fonts/tree/main/ofl/bizudgothic", "font_license":"BIZUDGothic-OFL.txt", "files":{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(ROOT.glob("*.pdf"))}, "fonts":font_hashes}
(ROOT/"manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n")
print(json.dumps(manifest,ensure_ascii=False,indent=2))
