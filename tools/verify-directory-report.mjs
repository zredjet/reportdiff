// 開発時だけ使う一覧レポートのブラウザ検証。個別レポートは既存の検証ツールも使う。
import assert from 'node:assert/strict';
import { readFile, mkdir, access } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
const { chromium } = createRequire(import.meta.url)('playwright');
const filename = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3] ?? 'out/directory-browser');
const report = JSON.parse(await readFile(path.join(path.dirname(filename), 'index.json'), 'utf8'));
await mkdir(output, { recursive: true });
const browser = await chromium.launch(process.env.REPORTDIFF_BROWSER ? { executablePath: process.env.REPORTDIFF_BROWSER } : { channel: 'chrome' });
const results = [], errors = [], external = [];
try {
  for (const width of [1280, 390]) {
    const context = await browser.newContext({ viewport: { width, height: 900 } });
    const page = await context.newPage();
    page.on('pageerror', e => errors.push(String(e)));
    page.on('request', r => { if (!r.url().startsWith('file:')) external.push(r.url()); });
    await page.goto(pathToFileURL(filename).href);
    assert.equal(await page.locator('article.file').count(), report.files.length);
    assert.equal(await page.locator('script').count(), 0);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    for (const element of await page.locator('.file, .path, dd, .error, .count').all()) {
      assert.equal(await element.evaluate(e => e.scrollWidth <= e.clientWidth + 1), true, await element.textContent());
      const box = await element.boundingBox();
      if (box) assert.ok(box.x >= 0 && box.x + box.width <= width + 1);
    }
    for (const file of report.files) {
      const row = page.locator(`#${file.id}`);
      assert.equal(await row.locator('h3').textContent(), file.relative_path);
      assert.equal(await row.locator('a').count(), file.json ? (file.html ? 2 : 1) : 0);
      if (!file.comparison) assert.match(await row.textContent(), /未比較/);
    }
    for (const link of await page.locator('a').all()) {
      const href = await link.getAttribute('href');
      assert.doesNotMatch(href, /^(?:[a-z]+:|\/)/i);
      await access(path.resolve(path.dirname(filename), href));
    }
    await page.keyboard.press('Tab');
    assert.equal(await page.evaluate(() => document.activeElement.tagName), 'A');
    assert.equal(await page.evaluate(() => getComputedStyle(document.activeElement).outlineStyle), 'solid');
    const first = report.files.find(f => f.html);
    await page.keyboard.press('Enter');
    await page.waitForURL(pathToFileURL(path.join(path.dirname(filename), first.html)).href);
    await page.goBack();
    const details = page.locator('summary');
    await details.focus(); await page.keyboard.press('Enter');
    assert.equal(await page.locator('details').getAttribute('open'), '');
    assert.equal(await page.locator('details li').count(), report.ignored.length);
    for (const item of await page.locator('details li').all()) assert.equal(await item.evaluate(e => e.scrollWidth <= e.clientWidth), true);
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: path.join(output, `directory-${width}.png`), fullPage: true });
    const failure = page.locator('.file').filter({ has: page.locator('p.error') }).first();
    if (await failure.count()) await failure.screenshot({ path: path.join(output, `directory-error-${width}.png`) });
    results.push({ width, files: report.files.length, overflow: false, keyboard: true, relative_links: true });
    await context.close();
  }
  const noJs = await browser.newContext({ javaScriptEnabled: false });
  const page = await noJs.newPage(); await page.goto(pathToFileURL(filename).href);
  assert.equal(await page.locator('article.file').count(), report.files.length);
  await noJs.close();
  assert.deepEqual(errors, []); assert.deepEqual(external, []);
  console.log(JSON.stringify({ browser: browser.version(), results, errors, external_requests: external, no_javascript: true }, null, 2));
} finally { await browser.close(); }
