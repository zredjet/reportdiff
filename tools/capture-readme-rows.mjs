// 日本語帳票の前後図（説明用）と、実際のHTMLレポートの構造変化一覧を撮影する。
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
const { chromium } = createRequire(import.meta.url)('playwright');
if (process.argv.length !== 5) {
  throw new Error('使い方: node tools/capture-readme-rows.mjs <入力PNGフォルダ> <レポートフォルダ> <出力フォルダ>');
}
const [input, report, output] = process.argv.slice(2).map(value => path.resolve(value));
await mkdir(output, { recursive: true });
const imageData = async name => `data:image/png;base64,${(await readFile(path.join(input, name))).toString('base64')}`;
const [before, after] = await Promise.all([imageData('order-before.png'), imageData('order-after.png')]);
const browser = await chromium.launch({ channel: 'chrome', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1100 }, deviceScaleFactor: 1 });
  const errors = [], external = [];
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', request => {
    if (!request.url().startsWith('file:') && !request.url().startsWith('data:')) external.push(request.url());
  });
  await page.setContent(`<!doctype html><html lang="ja"><meta charset="utf-8"><title>発注明細書の改訂</title>
    <style>
    *{box-sizing:border-box}body{margin:0;background:#edf0f4;color:#1c2837;font-family:system-ui,sans-serif}
    main{padding:32px 40px}h1{font-size:29px;margin:0 0 10px}p{font-size:14px;margin:0;line-height:1.9}
    header{display:flex;align-items:center;justify-content:space-between;gap:24px;margin-bottom:24px}
    .tags{display:flex;gap:10px}.tag{border-radius:5px;padding:10px 13px;font-size:12px;font-weight:700;white-space:nowrap}
    .add{color:#0c674a;background:#dcf2e9}.remove{color:#a12b3c;background:#fce3e7}
    .pair{display:grid;grid-template-columns:1fr 1fr;gap:26px}article{border:1px solid #d2d9e2;background:white}
    h2{margin:0;border-bottom:1px solid #d9dfe6;background:#f9fafb;padding:15px 20px;font-size:15px}
    .sheet{position:relative}.sheet img{display:block;width:100%;height:auto}
    .mark{position:absolute;left:6.4%;width:87.2%;height:2.851%;border:2px solid;pointer-events:none}
    .deleted{top:64.854%;background:#e4476411;border-color:#ce4761}.inserted{top:47.75%;background:#13a77e11;border-color:#11916b}
    .guide{font-size:11px;color:#566373;margin-top:15px}
    @media(max-width:760px){main{padding:20px 14px}header{display:block}.tags{margin-top:18px}.pair{grid-template-columns:1fr}h1{font-size:23px}}
    </style><main><header><div><h1>発注明細書の改訂</h1><p>備品を1行追加し、別の1行を取り消す。間の6行は、内容を変えずに移動。</p></div>
    <div class="tags"><span class="tag add">＋ スキャナーを追加</span><span class="tag remove">− プリンターを削除</span></div></header>
    <div class="pair"><article><h2>A　変更前</h2><div class="sheet"><img src="${before}" alt="変更前の発注明細書"><div class="mark deleted"></div></div></article>
    <article><h2>B　変更後</h2><div class="sheet"><img src="${after}" alt="変更後の発注明細書"><div class="mark inserted"></div></div></article></div>
    <p class="guide">赤・緑の枠は変更箇所を示す説明用ガイドです。PDF原本には着色していません。企業名・住所・取引内容はすべて架空です。</p></main></html>`);
  await page.evaluate(() => document.fonts.ready);
  await page.locator('img').evaluateAll(images => Promise.all(images.map(image => image.decode())));
  await page.screenshot({ path: path.join(output, 'sample-pdf-rows.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
  await page.setViewportSize({ width: 1280, height: 960 });
  await page.goto(pathToFileURL(path.join(report, 'report.html')).href);
  const table = page.locator('.row-alignment .table-scroll').filter({ hasText: 'S1 · 行の挿入' });
  assert.equal(await table.count(), 1);
  await table.screenshot({ path: path.join(output, 'sample-pdf-row-changes.png') });
  const result = JSON.parse(await readFile(path.join(report, 'result.json'), 'utf8'));
  assert.equal(result.pages[0].row_alignment.status, 'applied');
  assert.deepEqual(result.summary.structural_change_counts, { inserted: 1, deleted: 1, block_moved: 1 });
  assert.equal(result.summary.clusters, 0);
  assert.equal(result.pages[0].row_alignment.candidate_raw_pixels, 0);
  assert.equal(result.pages[0].row_alignment.structural_changes.find(change => change.kind === 'inserted').text_b.includes('バーコードスキャナー'), true);
  assert.equal(result.pages[0].row_alignment.structural_changes.find(change => change.kind === 'deleted').text_a.includes('ラベルプリンター'), true);
  assert.deepEqual(errors, []); assert.deepEqual(external, []);
  const verification = { browser: browser.version(), pageErrors: errors, externalRequests: external,
    previewNarrowOverflow: false, tool: result.tool, summary: result.summary };
  await writeFile(path.join(output, 'verification.json'), JSON.stringify(verification, null, 2) + '\n');
  console.log(JSON.stringify(verification, null, 2));
} finally { await browser.close(); }
