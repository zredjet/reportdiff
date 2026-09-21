// 実際のHTMLレポートからREADME用画面を撮影する。画像の内容は加工しない。
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
const { chromium } = createRequire(import.meta.url)('playwright');
const input = path.resolve(process.argv[2] ?? 'docs/samples/readme/result/report.html');
const output = path.resolve(process.argv[3] ?? 'docs/images');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: 'chrome', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 960 }, deviceScaleFactor: 1 });
  const errors = []; const external = [];
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', request => { if (!request.url().startsWith('file:')) external.push(request.url()); });
  await page.goto(pathToFileURL(input).href);
  await page.getByRole('button', { name: '重ね描き', exact: true }).click();
  for (const image of await page.locator('img').all()) {
    await image.scrollIntoViewIfNeeded(); await image.evaluate(element => element.decode());
  }
  await page.locator('.viewer').screenshot({ path: path.join(output, 'sample-overlay.png') });
  await page.locator('.table-scroll').filter({ has: page.locator('.clusters') }).screenshot({ path: path.join(output, 'sample-differences.png') });
  const result = JSON.parse(await readFile(path.join(path.dirname(input), 'result.json'), 'utf8'));
  assert.equal(result.summary.clusters, 3);
  assert.deepEqual(result.pages[0].clusters.map(cluster => cluster.kind), ['changed', 'removed', 'added']);
  assert.deepEqual(errors, []); assert.deepEqual(external, []);
  console.log(JSON.stringify({ browser: browser.version(), source: input, viewport: [1280, 960], pageErrors: errors, externalRequests: external, result: '成功' }, null, 2));
} finally { await browser.close(); }
