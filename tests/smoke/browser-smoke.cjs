// Run with PLAYWRIGHT_MODULE pointing to playwright-core, after the API smoke test.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const fs = require('node:fs');

(async () => {
  const state = JSON.parse(fs.readFileSync('.test-state/session.secret', 'utf8'));
  const base = process.env.STREAMFIN_TEST_URL || 'http://127.0.0.1:18096';
  const browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox']});
  const context = await browser.newContext({viewport: {width: 1440, height: 900}});
  const page = await context.newPage();
  try {
    await page.goto(base + '/web/', {waitUntil: 'networkidle'});
    try { await page.locator('#txtManualName').waitFor({state: 'visible', timeout: 5000}); }
    catch { await page.getByRole('button', {name: 'Manual Login', exact: true}).click(); }
    await page.locator('#txtManualName').fill('viewer');
    await page.locator('#txtManualPassword').fill(state.users.viewer.password);
    await page.locator('#txtManualPassword').press('Enter');
    await page.locator(':text-is("Anime"):visible').last().waitFor({timeout: 30000});
    await page.screenshot({path: 'artifacts/test-home.png'});
    await page.locator(':text-is("Anime"):visible').last().click();
    await page.locator(':text-is("Popular"):visible').last().waitFor();
    await page.screenshot({path: 'artifacts/test-anime.png'});
    console.log('PASS: browser login and Anime tile navigation');
    await page.locator(':text-is("Popular"):visible').last().click();
    await page.locator(':text-is("Naruto"):visible').last().waitFor({timeout: 45000});
    await page.screenshot({path: 'artifacts/test-catalog.png'});
    // Pick a title that the API smoke test did not pre-populate.
    await page.locator(':text-is("One-Punch Man"):visible').last().click();
    await page.locator(':text-is("Episodes · AnimeGG"):visible').last().waitFor({timeout: 45000});
    await page.screenshot({path: 'artifacts/test-title.png'});
    await page.locator(':text-is("Episodes · AnimeGG"):visible').last().click();
    await page.waitForTimeout(5000);
    console.log('EPISODES:', (await page.locator('body').innerText()).slice(0,1300));
    await page.screenshot({path: 'artifacts/test-episodes.png'});
    await page.locator('button.btnPlay:visible').first().click();
    await page.waitForFunction(() => {const v = document.querySelector('video'); return v && v.currentTime > 3 && !v.paused;}, null, {timeout: 90000});
    console.log('PASS: native browser video playback');
    await page.locator('video').evaluate(video => { video.currentTime = 120; });
    await page.waitForFunction(() => {const v = document.querySelector('video'); return v && v.currentTime > 122 && !v.paused;}, null, {timeout: 90000});
    await page.screenshot({path: 'artifacts/test-playback.png'});
    console.log('PASS: seek beyond initial buffer');
    await page.locator('video').evaluate(video => video.pause());
    const paused = await page.locator('video').evaluate(video => video.currentTime);
    await page.waitForTimeout(1000);
    if (Math.abs(await page.locator('video').evaluate(video => video.currentTime) - paused) > 0.2) throw new Error('Pause did not hold position');
    await page.locator('video').evaluate(video => video.play());
    await page.waitForTimeout(1500);
    console.log('PASS: pause and resume');
    await context.storageState({path: '.test-state/browser.secret'});
  } catch (error) {
    await page.screenshot({path: 'artifacts/test-browser-failure.png'});
    throw error;
  } finally { await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
