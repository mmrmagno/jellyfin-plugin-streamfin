const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const fs = require('node:fs');
(async () => {
  const state = JSON.parse(fs.readFileSync('.test-state/session.secret', 'utf8'));
  const base = process.env.ANIFIN_TEST_URL || 'http://127.0.0.1:18096';
  const browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox']});
  const context = await browser.newContext({viewport: {width: 1440, height: 900}, storageState: '.test-state/browser.secret'});
  const page = await context.newPage();
  try {
    await page.goto(base + '/web/', {waitUntil: 'networkidle'});
    await page.locator(':text-is("Anime"):visible').last().waitFor({timeout: 30000});
    const icon = page.locator('a[href*="parentId=' + state.channel + '"] .MuiButton-startIcon:visible, [data-itemid="' + state.channel + '"] .navMenuOptionIcon:visible');
    const mask = await icon.first().evaluate(node => getComputedStyle(node).maskImage);
    if (!mask.includes('data:image/svg+xml')) throw new Error('Shuriken sidebar icon not applied');
    await page.screenshot({path: 'artifacts/test-icon.png'});
    console.log('PASS: shuriken sidebar icon');
    await page.locator('[aria-label="Search"]:visible, .headerSearchButton:visible').first().click();
    const search = page.locator('input:visible').last();
    await search.fill('Cowboy Bebop');
    await page.locator(':text-is("Cowboy Bebop"):visible').last().waitFor({timeout: 45000});
    await page.screenshot({path: 'artifacts/test-search.png'});
    console.log('PASS: native browser search discovers online anime');
    await page.locator(':text-is("Cowboy Bebop"):visible').last().click();
    await page.locator(':text-is("Episodes · AnimeGG"):visible').last().waitFor({timeout: 45000});
    await page.locator(':text-is("Episodes · AnimeGG"):visible').last().click();
    await page.locator('button.btnPlay:visible').first().waitFor({timeout: 45000});
    await page.locator('button.btnPlay:visible').first().click();
    await page.waitForFunction(() => {const v = document.querySelector('video'); return v && v.currentTime > 3 && !v.paused;}, null, {timeout: 90000});
    console.log('PASS: search result opens native episodes and plays');
    await page.screenshot({path: 'artifacts/test-search-playback.png'});
  } catch (error) {
    await page.screenshot({path: 'artifacts/test-search-failure.png'});
    throw error;
  } finally { await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
