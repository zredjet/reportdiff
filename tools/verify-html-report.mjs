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
    const annotationText = value => value == null ? 'テキスト注釈なし' : value === '' ? '該当テキストなし' : value;
    assert.deepEqual(await page.locator('.text-value').allTextContents(), result.pages.flatMap(item =>
      item.clusters.flatMap(cluster => [annotationText(cluster.text_a), annotationText(cluster.text_b)])));
    for (const text of await page.locator('.text-value').all()) {
      assert.equal(await text.evaluate(element => element.scrollWidth <= element.clientWidth), true, 'PDF テキストが横にはみ出しています。');
      if (await text.evaluate(element => element.scrollHeight > element.clientHeight)) {
        await text.focus();
        assert.equal(await text.evaluate(element => getComputedStyle(element).outlineStyle), 'solid');
        await page.keyboard.press('ArrowDown');
        await page.waitForFunction(element => element.scrollTop > 0, await text.elementHandle(), { timeout: 5000 });
        await text.evaluate(element => { element.scrollTop = 0; });
      }
    }
    assert.ok((await page.locator('#pages').innerText()).includes('緑は A だけのインク'));
    assert.deepEqual(await page.locator('.input-path').allTextContents(), [result.inputs.a.path, result.inputs.b.path]);
    for (const warning of result.warnings) assert.ok((await page.locator('.warnings').innerText()).includes(warning.message));
    assert.deepEqual(await page.locator('.warnings li').evaluateAll(items => items.filter(item => {
      const rect = item.getBoundingClientRect();
      return item.scrollWidth > item.clientWidth || rect.left < 0 || rect.right > innerWidth;
    }).map(item => item.textContent)), [], '警告のフォント名・理由が横にはみ出しています。');
    if (result.warnings.some(warning => warning.code === 'NON_EMBEDDED_FONT' || warning.code === 'FONT_INSPECTION_INCOMPLETE')) {
      await page.locator('.warnings').screenshot({ path: path.join(screenshotDirectory, `${path.basename(reportPath, '.html')}-warnings-${width}.png`) });
    }
    assert.equal(await page.locator('.summary-metrics dd').last().innerText(), String(result.summary.absorbed_groups));
    const originalOpen = await page.locator('details.page').evaluateAll(items => items.map(item => item.open));
    await page.locator('details.page').evaluateAll(items => items.forEach(item => { item.open = true; }));
    await page.locator('.settings > summary').click();
    assert.equal(await page.locator('.settings').getAttribute('open'), '');
    assert.ok((await page.locator('.settings').innerText()).includes('除外領域'));
    const settingCount = Object.entries(result.config).filter(([key]) => key !== 'exclude')
      .reduce((sum, [, value]) => sum + (typeof value === 'object' ? Object.keys(value).length : 1), 0);
    assert.equal(await page.locator('.settings .settings-grid dd').count(), settingCount, '実効設定の表示に不足があります。');
    assert.deepEqual(await page.locator('.settings .settings-grid dt, .settings .settings-grid dd').evaluateAll(items => items.filter(item => {
      const rect = item.getBoundingClientRect();
      return item.scrollWidth > item.clientWidth || rect.left < 0 || rect.right > innerWidth;
    }).map(item => item.textContent)), [], '設定の項目名・値が横にはみ出しています。');
    await page.locator('.settings').screenshot({ path: path.join(screenshotDirectory, `${path.basename(reportPath, '.html')}-settings-${width}.png`) });
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
      const section = page.locator(`#page-${item.page}`);
      if (item.global_shift_px) {
        assert.ok((await section.locator(':scope > summary').innerText()).includes('全体補正あり'));
        const detectionViewer = section.locator(':scope > .viewer');
        assert.equal(await detectionViewer.locator(`button[data-src="${item.images.b_original}"]`).count(), 1);
        const originalButton = section.getByRole('button', { name: 'B · 補正前', exact: true });
        await originalButton.focus();
        await page.keyboard.press('Enter');
        assert.equal(await originalButton.getAttribute('aria-pressed'), 'true');
        assert.equal(await detectionViewer.locator('.page-image').getAttribute('src'), item.images.b_original);
        await section.getByRole('button', { name: '重ね描き', exact: true }).click();
        assert.ok((await section.locator('.alignment').innerText()).includes('B→A'));
      }
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
    assert.deepEqual(await page.locator('button, .input-path, .metrics dt, .metrics dd, .alignment dt, .alignment dd').evaluateAll(items => items.filter(item => {
      const rect = item.getBoundingClientRect();
      // 閉じた details の子要素は矩形が残る場合がある。表示中の要素だけを確認する。
      return item.checkVisibility() && rect.width > 0 && (rect.left < 0 || rect.right > innerWidth);
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
    if (result.pages.some(item => item.global_shift_px)) {
      await page.locator('.alignment').first().scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(screenshotDirectory, `${path.basename(reportPath, '.html')}-alignment-${width}.png`) });
    }
    await page.locator('details.page').evaluateAll((items, states) => items.forEach((item, i) => { item.open = states[i]; }), originalOpen);
    await page.locator('.settings > summary').click();
    // キーによるスクロールのアニメーションが撮影直前の復帰を上書きしないようにする。
    await page.waitForTimeout(250);
    await page.evaluate(() => {
      document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; });
      document.querySelectorAll('.text-value').forEach(element => { element.scrollTop = 0; });
      document.activeElement.blur(); window.scrollTo(0, 0);
    });
    await page.screenshot({ path: path.join(screenshotDirectory, `${path.basename(reportPath, '.html')}-${width}.png`), fullPage: true });
    if (width === 390 && await page.locator('.pdf-text').count()) {
      await page.locator('.table-scroll').filter({ has: page.locator('.clusters') }).first().evaluate(element => {
        element.scrollLeft = element.querySelector('.pdf-text').closest('td').offsetLeft;
      });
      await page.locator('.clusters').first().scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(screenshotDirectory, `${path.basename(reportPath, '.html')}-text-${width}.png`) });
    }
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
