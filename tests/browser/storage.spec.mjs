import { test, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { Installation } from './installation.mjs';

const password = 'A household password 123!';
const replacement = 'A replacement password 456!';
const week = '2026-10-26';
function fixture() {
  const recipe = randomUUID(), cook = randomUUID(), carrot = randomUUID(), salt = randomUUID(), soap = randomUUID(), pepper = randomUUID();
  return { SchemaVersion: 1, Household: {
    Ingredients: [],
    Recipes: [{ Id: recipe, Name: 'Acceptance soup', Servings: 2, Instructions: 'Simmer.', Ingredients: [
      { Name: 'Carrot', Quantity: 0.125, Unit: 'kg' }, { Name: 'Salt', Quantity: 2, Unit: 'g' }] }],
    Goods: [{ Id: carrot, Name: 'Carrot', IsFood: true }, { Id: salt, Name: 'Salt', IsFood: true },
      { Id: soap, Name: 'Soap', IsFood: false }, { Id: pepper, Name: 'Red pepper', IsFood: true }],
    Meals: [{ Id: cook, Date: week, RecipeId: recipe, LeftoversFromMealId: null },
      { Id: randomUUID(), Date: '2026-10-27', RecipeId: recipe, LeftoversFromMealId: cook },
      { Id: randomUUID(), Date: '2026-10-28', RecipeId: recipe, LeftoversFromMealId: cook }],
    ExtraItems: [{ Id: randomUUID(), Week: week, GoodId: soap, Quantity: 1, Unit: 'item' },
      { Id: randomUUID(), Week: week, GoodId: pepper, Quantity: 2, Unit: 'item' },
      { Id: randomUUID(), Week: '2026-11-02', GoodId: pepper, Quantity: 3, Unit: 'item' }],
    ShoppingOverrides: [{ Week: week, Name: 'Carrot', Unit: 'g', Quantity: 150 }],
    Pantry: [{ Week: week, Name: 'Soap', Unit: 'item' }],
    Purchases: [{ Week: week, Name: 'Salt', Unit: 'g', Quantity: 2, TotalPrice: 0 }],
    Prices: [{ Name: 'Salt', Unit: 'g', Quantity: 2, TotalPrice: 0 }],
  } };
}
async function setup(page, app, provider, controlled = false) {
  await page.goto(`${app.url}/setup`);
  await expect(page.getByRole('button', { name: 'Continue', exact: true })).toBeEnabled();
  await page.getByLabel('Operator setup token').fill('wrong');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Token rejected');
  await page.getByLabel('Operator setup token').fill(app.token);
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.getByLabel('Username', { exact: true }).fill('admin');
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByLabel('Confirm password').fill(password);
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  const select = page.getByLabel('Storage provider');
  await expect(select.locator('option')).toHaveText(['Simple — JSON files', 'Medium — SQLite']);
  if (controlled) { await expect(select).toBeDisabled(); await expect(select).toHaveValue(provider); }
  else { await expect(select).toBeEnabled(); await select.selectOption(provider); }
  await page.getByRole('button', { name: 'Validate storage and continue' }).click();
  await page.getByRole('button', { name: 'Initialise ISLA' }).click();
  await expect(page).toHaveURL(/\/login$/);
}
async function login(page, app, name = 'admin', secret = password, remember = true) {
  await page.goto(`${app.url}/login`);
  await page.getByLabel('Username', { exact: true }).fill(name);
  await page.getByLabel('Password', { exact: true }).fill(secret);
  await page.getByLabel('Remember this browser').setChecked(remember);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Weekly planner', exact: true })).toBeVisible();
}
async function exported(page, app) {
  const response = await page.request.get(`${app.url}/admin/export`);
  expect(response.status()).toBe(200);
  const data = await response.json();
  expect(JSON.stringify(data)).not.toMatch(/PasswordHash|Sessions|Setup|Accounts/);
  return data;
}
async function upload(page, app, input) {
  await page.goto(`${app.url}/settings`);
  // InputFile renders before Blazor attaches its JavaScript change listener.
  await expect.poll(() => page.locator('input[type=file]').evaluate(input => typeof input._blazorInputFileNextFileId === 'number')).toBe(true);
  await page.getByLabel('Household export (JSON, maximum 10 MB)').setInputFiles({
    name: 'household.json', mimeType: 'application/json', buffer: Buffer.from(typeof input === 'string' ? input : JSON.stringify(input)),
  });
  await page.getByRole('button', { name: 'Import into empty household' }).click();
}
async function importFixture(page, app, data) {
  await upload(page, app, data);
  await expect(page.locator('#import-result')).toContainText('Household imported successfully');
}
async function accountRow(page, name) {
  return page.locator('div.rounded-xl').filter({ has: page.locator('strong', { hasText: new RegExp(`^${name}$`) }) });
}

for (const provider of ['Json', 'Sqlite']) {
  test(`${provider}: setup, household UI, role boundaries, recreation and stopped backup restore`, async ({ browser }) => {
    const app = await new Installation().init();
    const restored = await new Installation().init();
    const context = await browser.newContext();
    const page = await context.newPage();
    let activeSocket, connections = 0;
    await page.routeWebSocket('**/_blazor*', socket => {
      activeSocket = socket;
      connections++;
      socket.connectToServer();
    });
    try {
      await app.start();
      await setup(page, app, provider);
      await login(page, app);
      await page.goto(`${app.url}/settings`);
      await expect(page.getByText(`Active provider: ${provider}`, { exact: false })).toBeVisible();
      await expect(page.getByLabel('Provider after restart')).toBeEnabled();
      await upload(page, app, 'broken');
      await expect(page.locator('#import-result')).toContainText('not a valid ISLA');
      await upload(page, app, { SchemaVersion: 99, Household: {} });
      await expect(page.locator('#import-result')).toContainText('Unsupported export version');
      await importFixture(page, app, fixture());
      const initial = await exported(page, app);
      for (const name of ['Recipes', 'Goods', 'Meals', 'ExtraItems', 'ShoppingOverrides', 'Pantry', 'Purchases', 'Prices'])
        expect(initial.Household[name].length).toBeGreaterThan(0);
      await upload(page, app, initial);
      await expect(page.locator('#import-result')).toContainText('empty household');
      expect(await exported(page, app)).toEqual(initial);

      // Real Blazor edit and leftovers promotion, backed by each selected provider.
      await page.goto(`${app.url}/recipes`);
      await expect(page.getByRole('button', { name: 'Add recipe', exact: true })).toBeEnabled();
      await page.locator('summary').click();
      await page.getByRole('button', { name: 'Edit recipe', exact: true }).click();
      await expect(page.getByLabel('Recipe name', { exact: true })).toBeFocused();
      await page.getByLabel('Method / notes').fill('Simmer until tender.');
      await page.getByRole('button', { name: 'Save recipe', exact: true }).click();
      await expect(page.getByText('Recipe saved.', { exact: true })).toBeVisible();
      await page.goto(`${app.url}/?week=${week}`);
      await page.getByRole('region', { name: 'Monday 26 October', exact: true }).getByRole('button', { name: 'Remove Acceptance soup from Monday' }).click();
      await expect(page.getByRole('status')).toContainText('first leftover meal');
      let state = await exported(page, app);
      expect(state.Household.Meals[0].LeftoversFromMealId).toBeNull();
      expect(state.Household.Meals[1].LeftoversFromMealId).toBe(state.Household.Meals[0].Id);
      expect(state.Household.Recipes[0].Ingredients[0].Quantity).toBe(0.125);

      await page.goto(`${app.url}/shopping-list?week=${week}`);
      await page.getByRole('button', { name: 'In-store mode', exact: true }).click();
      await page.getByLabel('Total price in pounds for Red pepper (item)', { exact: true }).fill('2.40');
      const pepper = page.getByRole('region', { name: 'In-store checklist' }).getByRole('listitem').filter({ hasText: 'Red pepper' });
      await pepper.getByRole('checkbox').check();
      await expect(pepper.getByRole('checkbox')).toBeChecked();
      await page.reload();
      await expect(page.getByRole('button', { name: 'Finish shopping' })).toBeVisible();
      await page.getByRole('button', { name: 'Finish shopping' }).click();
      await page.goto(`${app.url}/shopping-list?week=2026-11-02`);
      await expect(page.getByText('£3.60', { exact: true })).toBeVisible();

      // Catalogue and user-management UI with direct-request authorization checks.
      await page.goto(`${app.url}/goods`);
      await page.getByLabel('Name', { exact: true }).fill('Sponge');
      await page.getByLabel('Category').selectOption('non-food');
      await page.getByRole('button', { name: 'Save good' }).click();
      await expect(page.getByText('Goods catalogue saved.', { exact: true })).toBeVisible();
      const beforeReconnect = connections;
      await activeSocket.close({ code: 1012, reason: 'Acceptance test reconnect' });
      await expect.poll(() => connections, { timeout: 20_000 }).toBeGreaterThan(beforeReconnect);
      await expect(page.locator('#components-reconnect-modal')).not.toBeVisible();
      await page.getByLabel('Name', { exact: true }).fill('Reconnected good');
      await page.getByRole('button', { name: 'Save good' }).click();
      await expect.poll(async () => (await exported(page, app)).Household.Goods.some(g => g.Name === 'Reconnected good')).toBe(true);
      await page.goto(`${app.url}/settings`);
      await page.getByLabel('New username', { exact: true }).fill('member');
      await page.getByLabel('Initial password', { exact: true }).fill(password);
      await page.getByRole('button', { name: 'Create account', exact: true }).click();
      await expect(page.locator('strong').filter({ hasText: /^member$/ })).toBeVisible();
      const creationForm = page.locator('form').filter({ has: page.getByRole('button', { name: 'Create account', exact: true }) });
      await creationForm.getByLabel('New username', { exact: true }).fill('MEMBER');
      await creationForm.getByLabel('Initial password', { exact: true }).fill(password);
      await creationForm.getByRole('button', { name: 'Create account', exact: true }).click();
      await expect(creationForm.getByRole('alert')).toContainText('That username is already in use.');
      await expect(creationForm.getByRole('alert')).toBeInViewport();
      const memberContext = await browser.newContext();
      try {
        const member = await memberContext.newPage();
        await login(member, app, 'member', password, false);
        const memberCookie = (await memberContext.cookies()).find(c => c.name === 'ISLA.Session');
        expect(memberCookie.expires).toBe(-1);
        await member.goto(`${app.url}/settings`);
        await expect(member.getByRole('heading', { name: 'Household storage' })).toHaveCount(0);
        const denied = await member.request.get(`${app.url}/admin/export`, { maxRedirects: 0 });
        expect(denied.status()).toBe(302);
        expect(new URL(denied.headers().location, app.url).pathname).toBe('/access-denied');
        const deniedImport = await member.request.post(`${app.url}/admin/import`, { maxRedirects: 0 });
        expect(deniedImport.status()).toBe(403);
        const deniedPage = await member.goto(`${app.url}/admin/export`);
        expect(deniedPage.status()).toBe(403);
        await expect(member).toHaveURL(/\/access-denied(?:\?|$)/);
        await expect(member.getByRole('heading', { name: 'Access denied', exact: true })).toBeVisible();
        await expect(member.getByRole('alert')).toContainText('You are still signed in');
        await expect(member.getByRole('button', { name: /Account menu for member/ })).toBeVisible();
        await member.getByRole('link', { name: 'Back to planner', exact: true }).click();
        await expect(member.getByRole('heading', { name: 'Weekly planner', exact: true })).toBeVisible();
        const row = await accountRow(page, 'member');
        await row.getByLabel('New password for member').fill(replacement);
        await row.getByRole('button', { name: 'Reset password and revoke sessions' }).click();
        await expect(row.getByRole('status')).toContainText('Password reset');
        const revoked = await member.request.get(`${app.url}/goods`, { maxRedirects: 0 });
        expect(revoked.status()).toBe(302);
        await member.goto(`${app.url}/login`);
        for (let attempt = 0; attempt < 6; attempt++) {
          await member.getByLabel('Username', { exact: true }).fill('member');
          await member.getByLabel('Password', { exact: true }).fill(attempt === 5 ? replacement : 'incorrect');
          await member.getByRole('button', { name: 'Sign in', exact: true }).click();
          await expect(member).toHaveURL(/\/login\?failed=/);
          await expect(member.getByRole('alert')).toContainText(attempt >= 4 ? 'Your account is temporarily locked' : 'Check your username and password');
          await expect(member.getByRole('alert')).toBeInViewport();
        }
        await row.getByLabel('New password for member').fill(replacement);
        await row.getByRole('button', { name: 'Reset password and revoke sessions', exact: true }).click();
        await expect(row.getByRole('status')).toContainText('Password reset');
        await login(member, app, 'member', replacement);
        await row.getByRole('button', { name: 'Disable', exact: true }).click();
        await expect(row.getByRole('button', { name: 'Enable', exact: true })).toBeVisible();
        expect((await member.request.get(`${app.url}/goods`, { maxRedirects: 0 })).status()).toBe(302);
      } finally { await memberContext.close(); }
      const adminRow = await accountRow(page, 'admin');
      await adminRow.getByRole('button', { name: 'Make member', exact: true }).click();
      await expect(adminRow.getByRole('alert')).toContainText('Keep at least one active administrator.');
      await expect(adminRow.getByRole('alert')).toBeInViewport();
      await adminRow.getByRole('button', { name: 'Dismiss notification' }).click();
      await expect(adminRow.getByRole('alert')).toHaveCount(0);
      await adminRow.getByRole('button', { name: 'Disable', exact: true }).click();
      await expect(adminRow.getByRole('alert')).toContainText('Keep at least one active administrator.');
      await expect(adminRow.getByRole('alert')).toBeInViewport();
      await expect((await accountRow(page, 'member')).getByRole('alert')).toHaveCount(0);
      await page.screenshot({ path: test.info().outputPath('account-action-feedback.png') });

      const anonymous = await browser.newContext();
      try {
        for (const path of ['/', '/recipes', '/goods', '/shopping-list', '/settings', '/admin/export', '/access-denied'])
          expect((await anonymous.request.get(`${app.url}${path}`, { maxRedirects: 0 })).status()).toBe(302);
        expect((await anonymous.request.post(`${app.url}/admin/import`, { maxRedirects: 0 })).status()).toBe(302);
      } finally { await anonymous.close(); }
      const baseline = await exported(page, app);
      const cookies = await context.cookies();
      const cookie = cookies.find(c => c.name === 'ISLA.Session');
      expect(cookie.expires - Date.now() / 1000).toBeGreaterThan(89 * 86400);
      await app.stop();
      expect(app.logs).not.toContain(app.token);
      await app.start(app.options);
      // Restore the exact cookie at the replacement container's ephemeral port.
      await context.addCookies([{ name: cookie.name, value: cookie.value, url: app.url, httpOnly: true, sameSite: 'Lax', expires: cookie.expires }]);
      await page.goto(`${app.url}/settings`);
      await expect(page.getByText(`Active provider: ${provider}`, { exact: false })).toBeVisible();
      expect(await exported(page, app)).toEqual(baseline);
      expect(new URL((await page.request.get(`${app.url}/setup`)).url()).pathname).toBe('/');
      await app.stop();
      await app.copyTo(restored);
      await restored.start();
      await context.addCookies([{ name: cookie.name, value: cookie.value, url: restored.url, httpOnly: true, sameSite: 'Lax', expires: cookie.expires }]);
      expect(await exported(page, restored)).toEqual(baseline);
      await login(page, restored); // Restored password and identity also work.
    } finally { await context.close(); await app.dispose(); await restored.dispose(); }
  });
}

test('SQLite → JSON → SQLite transfers preserve all collections and keep accounts separate', async ({ browser }) => {
  const apps = [];
  const context = await browser.newContext();
  const page = await context.newPage();
  try {
    let data = fixture();
    for (const provider of ['Sqlite', 'Json', 'Sqlite']) {
      const app = await new Installation().init(); apps.push(app);
      await app.start();
      await setup(page, app, provider);
      await login(page, app);
      await importFixture(page, app, data);
      await page.goto(`${app.url}/settings`);
      if (apps.length === 1) {
        await page.getByLabel('New username', { exact: true }).fill('source-member');
        await page.getByLabel('Initial password', { exact: true }).fill(password);
        await page.getByRole('button', { name: 'Create account', exact: true }).click();
        await expect(page.locator('strong').filter({ hasText: /^source-member$/ })).toBeVisible();
      } else await expect(page.locator('strong').filter({ hasText: /^source-member$/ })).toHaveCount(0);
      const result = await exported(page, app);
      if (apps.length > 1) expect(result).toEqual(data);
      data = result;
      await page.goto(`${app.url}/settings`);
      await expect(page.locator('strong').filter({ hasText: /^admin$/ })).toHaveCount(1);
      await app.stop();
    }
  } finally { await context.close(); for (const app of apps) await app.dispose(); }
});

test('mounted configuration fixes provider and environment overrides it', async ({ browser }) => {
  for (const [mountedProvider, provider, expected] of [['Sqlite', undefined, 'Sqlite'], ['Sqlite', 'Json', 'Json']]) {
    const app = await new Installation().init();
    const page = await browser.newPage();
    try {
      await app.start({ mountedProvider, provider });
      await setup(page, app, expected, true);
      await login(page, app);
      await page.goto(`${app.url}/settings`);
      await expect(page.getByLabel('Provider after restart')).toBeDisabled();
      await expect(page.getByLabel('Provider after restart')).toHaveValue(expected);
    } finally { await page.close(); await app.dispose(); }
  }
});

test('console token enables first-run setup once and is absent after configured restarts', async ({ page }) => {
  for (const provider of ['Json', 'Sqlite']) {
    const app = await new Installation().init();
    try {
      await app.start({ token: '' });
      const match = app.readLogs().match(/ISLA first-run setup token: ([A-F0-9]{64})/);
      expect(match).not.toBeNull();
      app.token = match[1];
      await setup(page, app, provider);
      await login(page, app);
      await page.goto(`${app.url}/setup`);
      await expect(page).not.toHaveURL(/\/setup$/);
      await app.restart();
      expect(app.readLogs()).not.toContain('ISLA first-run setup token:');
      await page.goto(`${app.url}/setup`);
      await expect(page).not.toHaveURL(/\/setup$/);
      await login(page, app);
    } finally { await app.dispose(); }
  }
});

test('Settings schedules JSON to SQLite only after restart and retains the JSON source', async ({ page }) => {
  const app = await new Installation().init();
  try {
    await app.start();
    await setup(page, app, 'Json');
    await login(page, app);
    await importFixture(page, app, fixture());
    const source = await exported(page, app);
    await page.goto(`${app.url}/settings`);
    await page.getByLabel('Provider after restart').selectOption('Sqlite');
    await page.getByRole('button', { name: 'Save storage choice' }).click();
    await expect(page.getByText('Restart required. Next provider: Sqlite.')).toBeVisible();
    await expect(page.getByText('Active provider: Json', { exact: false })).toBeVisible();
    expect(await exported(page, app)).toEqual(source);
    await app.restart();
    await setup(page, app, 'Sqlite');
    await login(page, app);
    await importFixture(page, app, source);
    expect(await exported(page, app)).toEqual(source);
    await page.goto(`${app.url}/settings`);
    await page.getByLabel('Provider after restart').selectOption('Json');
    await page.getByRole('button', { name: 'Save storage choice' }).click();
    await expect(page.getByText('The destination already contains storage files.', { exact: false })).toBeVisible();
    await app.stop();
    await app.start({ provider: 'Json' });
    await login(page, app); // The original JSON account and household remain usable.
    expect(await exported(page, app)).toEqual(source);
  } finally { await app.dispose(); }
});
