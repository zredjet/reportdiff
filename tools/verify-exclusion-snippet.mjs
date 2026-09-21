// 除外 YAML 欄のブラウザ受け入れ。既存の Playwright / Chrome を使用する。
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = createRequire(import.meta.url)('playwright');
assert.ok(process.argv[2], 'report.html を指定してください。');
const reportPath = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3] ?? 'out/exclusion-snippet-browser');
await mkdir(output, { recursive: true });
const report = JSON.parse(await readFile(path.join(path.dirname(reportPath), 'result.json'), 'utf8'));
const count = report.pages.reduce((sum, page) => sum + page.clusters.length, 0);
assert.ok(count > 0, '相違があるレポートを指定してください。');
const browser = await chromium.launch({
  ...(process.env.REPORTDIFF_BROWSER ? { executablePath: process.env.REPORTDIFF_BROWSER } : { channel: 'chrome' }),
  headless: true,
});
const errors = [], requests = [], records = [];
try {
  for (const width of [1280, 390]) {
    for (const javaScriptEnabled of [true, false]) {
      const context = await browser.newContext({ viewport: { width, height: 900 }, javaScriptEnabled });
      const page = await context.newPage();
      page.on('pageerror', error => errors.push(String(error)));
      page.on('request', request => { if (!request.url().startsWith('file:')) requests.push(request.url()); });
      await page.goto(pathToFileURL(reportPath).href);
      assert.equal(await page.locator('.exclusion-snippet').count(), count);
      for (const item of report.pages) {
        for (const cluster of item.clusters) {
          const snippet = page.locator(`#page-${item.page}-cluster-${cluster.id} .exclusion-snippet`);
          const summary = snippet.locator('summary');
          await summary.focus();
          await page.keyboard.press('Enter');
          assert.equal(await snippet.getAttribute('open'), '');
          const field = snippet.locator('textarea');
          const text = await field.inputValue();
          assert.ok(text.startsWith(`- {page: ${item.page}, x: `));
          assert.ok(text.endsWith('note: ""}'));
          assert.equal(await field.getAttribute('readonly'), '');
          await page.keyboard.press('Tab');
          assert.equal(await field.evaluate(element => element === document.activeElement), true);
          await page.keyboard.press(process.platform === 'darwin' ? 'Meta+A' : 'Control+A');
          assert.deepEqual(await field.evaluate(element => [element.selectionStart, element.selectionEnd]), [0, text.length]);
          assert.equal(await field.evaluate(element => getComputedStyle(element).outlineStyle), 'solid');
          await page.keyboard.type('変更されない');
          assert.equal(await field.inputValue(), text);
          if (javaScriptEnabled) {
            await snippet.locator('.select-yaml').focus();
            await page.keyboard.press('Enter');
            assert.equal(await field.evaluate(element => element === document.activeElement), true);
            assert.deepEqual(await field.evaluate(element => [element.selectionStart, element.selectionEnd]), [0, text.length]);
          } else assert.equal(await snippet.locator('.select-yaml').isVisible(), false);
          await field.scrollIntoViewIfNeeded();
          assert.equal(await field.evaluate(element => {
            const fieldBox = element.getBoundingClientRect();
            const cellBox = element.closest('td').getBoundingClientRect();
            return fieldBox.left >= cellBox.left && fieldBox.right <= cellBox.right && element.scrollWidth <= element.clientWidth;
          }), true, 'YAML 欄がセル内に収まっていません。');
          assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
          if (cluster === item.clusters[0]) {
            await page.screenshot({ path: path.join(output, `${width}-${javaScriptEnabled ? 'js' : 'no-js'}.png`) });
          }
          await summary.focus();
          await page.keyboard.press('Enter');
          assert.equal(await snippet.getAttribute('open'), null);
        }
      }
      records.push({ width, javaScriptEnabled, snippets: count, keyboardSelection: true, readOnly: true, fitsCell: true });
      await context.close();
    }
  }
  assert.deepEqual(errors, []); assert.deepEqual(requests, []);
  const result = { browser: browser.version(), records, pageErrors: errors, externalRequests: requests };
  await writeFile(path.join(output, 'result.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify(result, null, 2));
} finally { await browser.close(); }
