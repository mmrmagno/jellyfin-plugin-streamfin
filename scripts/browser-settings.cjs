const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const fs = require('node:fs');

(async () => {
  const base = process.env.ANIFIN_TEST_URL || 'http://127.0.0.1:18096';
  const state = JSON.parse(fs.readFileSync('.test-state/session.secret', 'utf8'));
  const browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox']});
  const context = await browser.newContext();
  const headers = {Authorization: 'MediaBrowser Token="' + state.admin + '"'};
  const path = '/Plugins/a74fdb2e-938f-483b-b2ef-f527e14ff724/Configuration';
  try {
    const info = await (await context.request.get(base + '/System/Info/Public')).json();
    if (info.ServerName !== 'AniFin isolated test') throw new Error('Refusing settings test on a production server');
    const user = await (await context.request.get(base + '/Users/Me', {headers})).json();
    const before = await (await context.request.get(base + path, {headers})).json();
    const server = {Name: info.ServerName, Id: info.Id, ManualAddress: base, LocalAddress: base, manualAddressOnly: true, LastConnectionMode: 2, UserId: user.Id, AccessToken: state.admin};
    await context.addInitScript(({server, user}) => {
      localStorage.setItem('jellyfin_credentials', JSON.stringify({Servers: [server]}));
      localStorage.setItem('enableAutoLogin', 'true');
      localStorage.setItem('user-' + user.Id + '-' + user.ServerId, JSON.stringify(user));
    }, {server, user});
    const page = await context.newPage();
    await page.goto(base + '/web/#/configurationpage?name=anifin', {waitUntil: 'networkidle'});
    await page.locator('#anifinSave:enabled').waitFor({timeout: 20000});
    const selected = await page.locator('#anifinUsers input:checked').evaluateAll(inputs => inputs.map(input => input.value));
    if (!selected.some(id => id.replaceAll('-', '').toLowerCase() === state.users.marc.id)) throw new Error('marc was not loaded as selected');
    console.log('PASS: settings load existing permitted users');
    await page.screenshot({path: 'artifacts/test-settings.png'});
    await Promise.all([
      page.waitForResponse(response => response.url().endsWith(path) && response.request().method() === 'POST'),
      page.locator('#anifinSave').click()
    ]);
    const after = await (await context.request.get(base + path, {headers})).json();
    for (const key of ['Enabled', 'AnimeGgEnabled', 'AnimeHeavenEnabled', 'HideAdultContent', 'HideEcchiContent', 'AllowedUserIds']) {
      if (JSON.stringify(before[key]) !== JSON.stringify(after[key])) throw new Error('Saving unchanged settings changed ' + key);
    }
    console.log('PASS: saving preserves permissions and content filters');
    const clearPath = '/AniFin/SearchResults/Clear';
    const [cleared] = await Promise.all([
      page.waitForResponse(response => response.url().endsWith(clearPath) && response.request().method() === 'POST'),
      page.getByRole('button', {name: 'Clear search results', exact: true}).click()
    ]);
    if (!cleared.ok()) throw new Error('Clear search results failed');
    await page.locator('#anifinClearStatus').filter({hasText: /Search results (cleared|are already empty)/}).waitFor();
    console.log('PASS: clear-search button reports completion');
    await page.route('**' + path, route => route.abort());
    await page.reload({waitUntil: 'networkidle'});
    await page.getByText('Could not load settings.', {exact: false}).waitFor();
    if (await page.locator('#anifinSave').isEnabled()) throw new Error('Save was enabled after failed settings load');
    console.log('PASS: failed settings load prevents accidental overwrites');
  } finally { await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
