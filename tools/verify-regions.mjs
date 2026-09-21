// 領域設定・監査・ページ別集計の表示と、狭い幅でのキーボード操作を確認する。
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = createRequire(import.meta.url)('playwright');
assert.ok(process.argv[2], 'prepare-region-review.py の出力ディレクトリを指定してください。');
const root = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3] ?? 'out/regions-browser');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ ...(process.env.REPORTDIFF_BROWSER ? { executablePath: process.env.REPORTDIFF_BROWSER } : { channel: 'chrome' }), headless: true });
const errors = [], external = [], records = [];
try {
  for (const mode of ['regions', 'audit']) for (const width of [1280, 390]) for (const javaScriptEnabled of [true, false]) {
    const report = JSON.parse(await readFile(path.join(root, mode, 'result.json'), 'utf8'));
    const context = await browser.newContext({ viewport: { width, height: 900 }, javaScriptEnabled });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(String(error)));
    page.on('request', request => { if (!request.url().startsWith('file:')) external.push(request.url()); });
    await page.goto(pathToFileURL(path.join(root, mode, 'report.html')).href);
    assert.deepEqual(JSON.parse(await page.locator('#result').textContent()), report);
    const summary = await page.locator('.summary-metrics').textContent();
    await page.locator('.settings > summary').focus(); await page.keyboard.press('Enter');
    const declared = report.config.regions?.length ? report.config.regions : report.config.region_audit.regions;
    assert.equal(await page.locator('.region-settings tbody tr').count(), declared.length);
    for (const [index, declaration] of declared.entries()) {
      const row = page.locator('.region-settings tbody tr').nth(index);
      assert.ok((await row.textContent()).includes(declaration.name));
      if (declaration.effective_diff) assert.ok((await row.textContent()).includes(String(declaration.effective_diff.color_threshold)));
    }
    if (mode === 'audit') {
      assert.match(await page.locator('.region-audit').textContent(), /--no-regions/);
      assert.match(await page.locator('.region-audit').textContent(), /PDF 注釈/);
      assert.equal(await page.locator('.page-regions').count(), 0);
    } else {
      const item = report.pages[0].regions;
      const section = page.locator('.page-regions');
      assert.ok((await section.textContent()).includes(`${item.suppressed_pixels} 画素・${item.suppressed_components} 箇所`));
      assert.equal(await section.locator('.region-results tbody tr').count(), item.items.length);
      for (const [index, expected] of item.items.entries()) {
        const row = section.locator('.region-results tbody tr').nth(index);
        assert.ok((await row.textContent()).includes(expected.name));
        assert.equal(await row.locator('td').nth(2).textContent(), String(expected.effective_pixels));
        assert.equal(await row.locator('td').nth(3).textContent(), String(expected.raw_pixels));
        assert.equal(await row.locator('td').nth(4).textContent(), `${expected.suppressed_pixels} / ${expected.suppressed_components}`);
      }
      await section.locator('.region-runs > summary').focus(); await page.keyboard.press('Enter');
      assert.equal(await section.locator('.region-runs li').count(), item.runs.length);
      await section.evaluate(e => e.scrollIntoView({ block: 'start' }));
      await section.screenshot({ path: path.join(output, `impact-${width}-${javaScriptEnabled}.png`) });
    }
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    const tables = page.locator('.settings .table-scroll, .page-regions .table-scroll');
    let scrollable = 0;
    for (const table of await tables.all()) {
      if (await table.evaluate(e => e.scrollWidth > e.clientWidth)) {
        scrollable++;
        await table.evaluate(e => { e.scrollLeft = 0; });
        await table.focus();
        assert.equal(await table.evaluate(e => getComputedStyle(e).outlineStyle), 'solid');
        await page.keyboard.press('ArrowRight');
        for (let attempt = 0; attempt < 20 && !(await table.evaluate(e => e.scrollLeft > 0)); attempt++) await new Promise(resolve => setTimeout(resolve, 50));
        assert.equal(await table.evaluate(e => e.scrollLeft > 0), true, `${mode} ${width} JS=${javaScriptEnabled}: キーボードで表をスクロールできません。`);
        await table.evaluate(e => { e.scrollLeft = 0; });
      }
    }
    if (width === 390) assert.ok(scrollable > 0);
    assert.equal(await page.locator('.summary-metrics').textContent(), summary);
    await page.locator('.settings').screenshot({ path: path.join(output, `${mode}-settings-${width}-${javaScriptEnabled}.png`) });
    records.push({ mode, width, javaScriptEnabled, scrollable_tables: scrollable, root_overflow: false, counts_match: true });
    await context.close();
  }
  assert.deepEqual(errors, []); assert.deepEqual(external, []);
  await writeFile(path.join(output, 'verification.json'), JSON.stringify({ browser: browser.version(), errors, external_requests: external, records }, null, 2) + '\n');
  console.log(JSON.stringify({ contexts: records.length, errors, external_requests: external }));
} finally { await browser.close(); }
