// 確認用画像の独立した切り替え、キーボード、PNG 単体、オフライン表示を確認する。
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = createRequire(import.meta.url)('playwright');
assert.ok(process.argv[2], 'report.html を指定してください。');
const reportPath = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3] ?? 'out/raw-overlay-browser');
await mkdir(output, { recursive: true });
const report = JSON.parse(await readFile(path.join(path.dirname(reportPath), 'result.json'), 'utf8'));
assert.ok(report.pages.every(page => page.raw_evidence), '確認用画像を有効化したレポートを指定してください。');
const browser = await chromium.launch({
  ...(process.env.REPORTDIFF_BROWSER ? { executablePath: process.env.REPORTDIFF_BROWSER } : { channel: 'chrome' }),
  headless: true,
});
const errors = [], externalRequests = [], records = [];
try {
  for (const width of [1280, 390]) {
    for (const javaScriptEnabled of [true, false]) {
      const context = await browser.newContext({ viewport: { width, height: 900 }, javaScriptEnabled });
      context.on('request', request => { if (!request.url().startsWith('file:')) externalRequests.push(request.url()); });
      const page = await context.newPage();
      page.on('pageerror', error => errors.push(String(error)));
      await page.goto(pathToFileURL(reportPath).href);
      const summaryBefore = await page.locator('.summary-metrics').textContent();
      assert.deepEqual(JSON.parse(await page.locator('#result').textContent()), report);
      for (const item of report.pages) {
        const section = page.locator(`#page-${item.page}`);
        if (await section.getAttribute('open') === null) {
          await section.locator(':scope > summary').focus(); await page.keyboard.press('Enter');
        }
        const metricsBefore = await section.locator('.page-metrics').textContent();
        const statusBefore = await section.locator(':scope > summary').textContent();
        const evidence = section.locator('.raw-evidence');
        assert.equal(await evidence.count(), 1);
        assert.match(await evidence.innerText(), /判定から独立/);
        assert.match(await evidence.innerText(), /色相/);
        assert.equal(await evidence.locator('.raw-legend').innerText(), `赤：A のみ · 青：B のみ ·  共通：${item.raw_evidence.common_color}`);
        const image = evidence.locator('.page-image');
        assert.equal(await image.getAttribute('src'), item.raw_evidence.overlay);
        await image.scrollIntoViewIfNeeded(); await image.evaluate(element => element.decode());
        assert.deepEqual(await image.evaluate(element => [element.naturalWidth, element.naturalHeight]),
          [item.raw_evidence.canvas_size_px.w, item.raw_evidence.canvas_size_px.h]);
        if (javaScriptEnabled) {
          // 判定・確認用の全ビューを操作し、ほかの画像が変わらないことも検査する。
          for (const viewer of await section.locator('.viewer').all()) {
            const allImages = section.locator('.page-image');
            const initial = await allImages.evaluateAll(items => items.map(element => element.getAttribute('src')));
            const ownImage = viewer.locator('.page-image');
            const initialSource = await ownImage.getAttribute('src');
            const buttons = viewer.locator('button:not(:disabled)');
            await buttons.first().focus();
            for (const [i, button] of (await buttons.all()).entries()) {
              if (i > 0) await page.keyboard.press('Tab');
              assert.equal(await button.evaluate(element => element === document.activeElement), true);
              await page.keyboard.press(i % 2 ? 'Space' : 'Enter');
              await ownImage.evaluate(element => element.decode());
              assert.equal(await ownImage.getAttribute('src'), await button.getAttribute('data-src'));
              assert.equal(await viewer.locator('.page-image-link').getAttribute('href'), await button.getAttribute('data-src'));
              assert.equal(await ownImage.getAttribute('alt'), await button.getAttribute('data-label'));
              assert.equal(await viewer.locator('button[aria-pressed="true"]').count(), 1);
              assert.equal(await button.evaluate(element => getComputedStyle(element).outlineStyle), 'solid');
            }
            await viewer.locator(`button[data-src="${initialSource}"]`).click();
            assert.deepEqual(await allImages.evaluateAll(items => items.map(element => element.getAttribute('src'))), initial);
          }
        } else assert.equal(await evidence.locator('.image-switch').isVisible(), false);
        const links = evidence.locator('.raw-links a');
        assert.deepEqual(await links.evaluateAll(items => items.map(item => item.getAttribute('href'))),
          [item.raw_evidence.overlay, item.raw_evidence.a.image, item.raw_evidence.b.image]);
        for (const link of await links.all()) {
          const png = await context.newPage();
          await png.goto(new URL(await link.getAttribute('href'), pathToFileURL(reportPath)).href);
          await png.locator('img').evaluate(element => element.decode());
          assert.equal(await png.locator('img').evaluate(element => element.naturalWidth), item.raw_evidence.canvas_size_px.w);
          await png.close();
        }
        assert.equal(await section.locator('.page-metrics').textContent(), metricsBefore);
        assert.equal(await section.locator(':scope > summary').textContent(), statusBefore);
        await evidence.scrollIntoViewIfNeeded();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
        assert.deepEqual(await evidence.locator('button, p, a').evaluateAll(items => items.filter(item => {
          const r = item.getBoundingClientRect();
          return item.checkVisibility() && (r.left < 0 || r.right > innerWidth || item.scrollWidth > item.clientWidth);
        }).map(item => item.textContent)), [], '確認用表示の要素が横にはみ出しています。');
        await evidence.screenshot({ path: path.join(output, `p${item.page}-${width}-${javaScriptEnabled ? 'js' : 'no-js'}.png`) });
      }
      assert.equal(await page.locator('.summary-metrics').textContent(), summaryBefore);
      assert.deepEqual(JSON.parse(await page.locator('#result').textContent()), report);
      records.push({ width, javaScriptEnabled, pages: report.pages.length, keyboard: javaScriptEnabled,
        standalonePng: true, countsUnchanged: true, noHorizontalOverflow: true });
      await context.close();
    }
  }
  assert.deepEqual(errors, []); assert.deepEqual(externalRequests, []);
  const result = { browser: browser.version(), records, pageErrors: errors, externalRequests };
  await writeFile(path.join(output, 'result.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify(result, null, 2));
} finally { await browser.close(); }
