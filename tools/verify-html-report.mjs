// 開発時だけ使う Playwright 検証。アプリの実行依存には含めない。
// NODE_PATH で Playwright の場所、REPORTDIFF_BROWSER でブラウザの実行ファイルを指定できる。
import assert from 'node:assert/strict';
import { readFile, mkdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = createRequire(import.meta.url)('playwright');
const filename = process.argv[2];
assert.ok(filename, 'report.html のパスを指定してください。');
const reportPath = path.resolve(filename);
const html = await readFile(reportPath, 'utf8');
assert.doesNotMatch(html, /https?:\/\//i);
const screenshotDirectory = path.resolve(process.argv[3] ?? 'out/html-browser-check');
await mkdir(screenshotDirectory, { recursive: true });
const browser = await chromium.launch({
  ...(process.env.REPORTDIFF_BROWSER ? { executablePath: process.env.REPORTDIFF_BROWSER } : { channel: 'chrome' }),
  headless: true,
});
const errors = [];
const externalRequests = [];
try {
  for (const width of [1280, 390]) {
    const context = await browser.newContext({ viewport: { width, height: 900 } });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(String(error)));
    page.on('request', request => {
      if (!request.url().startsWith('file:')) externalRequests.push(request.url());
    });
    await page.addInitScript(() => {
      window.reportCspViolations = [];
      document.addEventListener('securitypolicyviolation', event => window.reportCspViolations.push(event.blockedURI));
    });
    await page.goto(pathToFileURL(reportPath).href);
    const result = await page.locator('#result').evaluate(element => JSON.parse(element.textContent));
    if (path.basename(reportPath) === 'report.html') {
      assert.deepEqual(result, JSON.parse(await readFile(path.join(path.dirname(reportPath), 'result.json'), 'utf8')));
    }
    assert.equal(await page.locator('.page').count(), result.pages.length);
    assert.equal(await page.locator('.clusters tbody tr').count(), result.pages.reduce((sum, item) => sum + item.clusters.length, 0));
    const kindLabels = { added: '追加（推定）', removed: '削除（推定）', changed: '変更', color_changed: '色変更（推定）', moved: '移動（推定）' };
    const kinds = result.pages.flatMap(item => item.clusters.map(cluster => kindLabels[cluster.kind] ?? cluster.kind ?? '未分類'));
    assert.deepEqual(await page.locator('.kind').allTextContents(), kinds);
    assert.ok((await page.locator('#pages').innerText()).includes('緑は A だけのインク'));
    assert.deepEqual(await page.locator('.input-path').allTextContents(), [result.inputs.a.path, result.inputs.b.path]);
    for (const warning of result.warnings) assert.ok((await page.locator('.warnings').innerText()).includes(warning.message));
    assert.equal(await page.locator('.summary-metrics dd').last().innerText(), String(result.summary.absorbed_groups));
    const originalOpen = await page.locator('details.page').evaluateAll(items => items.map(item => item.open));
    await page.locator('details.page').evaluateAll(items => items.forEach(item => { item.open = true; }));
    await page.locator('.settings > summary').click();
    assert.equal(await page.locator('.settings').getAttribute('open'), '');
    assert.ok((await page.locator('.settings').innerText()).includes('除外領域'));
    assert.equal(await page.evaluate(() => globalThis.injected), undefined);
    // 遅延読み込みも実際にスクロールして確認する。
    for (const image of await page.locator('img').all()) {
      await image.scrollIntoViewIfNeeded();
      await image.evaluate(element => element.decode());
      assert.equal(await image.evaluate(element => element.complete && element.naturalWidth > 0), true);
    }
    for (const viewer of await page.locator('.viewer').all()) {
      const buttons = viewer.locator('button:not(:disabled)');
      const initialSource = await viewer.locator('.page-image').getAttribute('src');
      const initialSources = await page.locator('.page-image').evaluateAll(items => items.map(item => item.getAttribute('src')));
      for (const button of await buttons.all()) {
        await button.click();
        await viewer.locator('.page-image').evaluate(element => element.decode());
        assert.equal(await viewer.locator('.page-image').getAttribute('src'), await button.getAttribute('data-src'));
        assert.equal(await viewer.locator('.page-image-link').getAttribute('href'), await button.getAttribute('data-src'));
        assert.equal(await viewer.locator('.page-image').getAttribute('alt'), await button.getAttribute('data-label'));
        assert.equal(await viewer.locator('.viewer-label').innerText(), await button.getAttribute('data-label'));
        assert.equal(await viewer.locator('button[aria-pressed="true"]').count(), 1);
        assert.equal(await button.getAttribute('aria-pressed'), 'true');
      }
      // ネイティブボタンのキーボード操作とフォーカス表示。
      await buttons.first().focus();
      await page.keyboard.press('Enter');
      assert.equal(await buttons.first().getAttribute('aria-pressed'), 'true');
      assert.equal(await buttons.first().evaluate(element => getComputedStyle(element).outlineStyle), 'solid');
      if (await buttons.count() > 1) {
        await page.keyboard.press('Tab');
        await page.keyboard.press('Space');
        assert.equal(await buttons.nth(1).getAttribute('aria-pressed'), 'true');
      }
      await viewer.locator(`button[data-src="${initialSource}"]`).click();
      assert.deepEqual(await page.locator('.page-image').evaluateAll(items => items.map(item => item.getAttribute('src'))), initialSources);
    }
    for (const item of result.pages) {
      for (const cluster of item.clusters.filter(cluster => cluster.kind === 'moved')) {
        const row = page.locator(`#page-${item.page}-cluster-${cluster.id}`);
        const { dx, dy } = cluster.shift_px;
        if (dx) assert.ok((await row.innerText()).includes(`${dx > 0 ? '右' : '左'} ${Math.abs(dx)} px`));
        if (dy) assert.ok((await row.innerText()).includes(`${dy > 0 ? '下' : '上'} ${Math.abs(dy)} px`));
        assert.deepEqual(await row.locator('.movement a').allTextContents(), cluster.related_cluster_ids.map(String));
      }
    }
    for (const link of await page.locator('.movement a').all()) {
      const target = await link.getAttribute('href');
      assert.equal(await page.locator(target).count(), 1);
      await link.click();
      assert.equal(await page.locator(target).evaluate(element => element.matches(':target')), true);
      await link.focus();
      await page.keyboard.press('Enter');
      assert.equal(await page.locator(target).evaluate(element => element.matches(':target')), true);
    }
    await page.evaluate(() => history.replaceState(null, '', location.href.split('#')[0]));
    assert.deepEqual(await page.locator('.kind').evaluateAll(items => items.filter(item => {
      const rect = item.getBoundingClientRect();
      const cell = item.closest('th').getBoundingClientRect();
      return item.getClientRects().length !== 1 || rect.left < cell.left || rect.right > cell.right;
    }).map(item => item.textContent)), [], '分類名が折り返されたり、セルの外へはみ出しています。');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `${width}px の画面が横にはみ出しています。`);
    assert.deepEqual(await page.locator('button, .input-path, .metrics dt, .metrics dd').evaluateAll(items => items.filter(item => {
      const rect = item.getBoundingClientRect();
      return rect.width > 0 && (rect.left < 0 || rect.right > innerWidth);
    }).map(item => item.textContent)), []);
    if (width === 390 && await page.locator('.clusters').count() > 0) {
      const scrollRegion = page.locator('.table-scroll').filter({ has: page.locator('.clusters') }).first();
      assert.equal(await scrollRegion.evaluate(element => element.scrollWidth > element.clientWidth), true);
      await scrollRegion.evaluate(element => { element.scrollLeft = 0; });
      await scrollRegion.focus();
      await page.keyboard.press('ArrowRight');
      await page.waitForFunction(() => [...document.querySelectorAll('.table-scroll')].some(element => element.scrollLeft > 0));
    }
    assert.deepEqual(await page.evaluate(() => window.reportCspViolations), []);
    await page.locator('details.page').evaluateAll((items, states) => items.forEach((item, i) => { item.open = states[i]; }), originalOpen);
    await page.locator('.settings > summary').click();
    await page.evaluate(() => {
      document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; });
      document.activeElement.blur(); window.scrollTo(0, 0);
    });
    await page.screenshot({ path: path.join(screenshotDirectory, `${path.basename(reportPath, '.html')}-${width}.png`), fullPage: true });
    await context.close();
  }
  const noScript = await browser.newContext({ javaScriptEnabled: false });
  const staticPage = await noScript.newPage();
  await staticPage.goto(pathToFileURL(reportPath).href);
  assert.equal(await staticPage.locator('h1').isVisible(), true);
  assert.equal(await staticPage.locator('.image-switch:visible').count(), 0);
  await noScript.close();
  assert.deepEqual(errors, []);
  assert.deepEqual(externalRequests, []);
  console.log(JSON.stringify({ browser: browser.version(), file: reportPath, widths: [1280, 390], pageErrors: errors.length, externalRequests: externalRequests.length, result: '成功' }, null, 2));
} finally {
  await browser.close();
}
