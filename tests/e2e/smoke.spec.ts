import AxeBuilder from '@axe-core/playwright';
import { expect, type Page, type PlaywrightWorkerArgs, test } from '@playwright/test';

/** The administrator of the e2e stack (tests/e2e/stack/docker-compose.yml): a test-only value. */
const ADMIN = { name: 'admin', password: 'e2e-admin-password' };

/** A request context that has signed in: it keeps the session cookie, and sends the header a write needs. */
async function signedInRequest(playwright: PlaywrightWorkerArgs['playwright'], baseURL: string) {
  const request = await playwright.request.newContext({ baseURL, extraHTTPHeaders: { 'X-Mnemarr-Request': '1' } });
  const login = await request.post('/api/v1/auth/login', { data: { name: ADMIN.name, password: ADMIN.password } });
  expect(login.status()).toBe(200);
  return request;
}

/** Opens a page and signs in as somebody, through the sign-in form like a person. */
async function signIn(page: Page, path = '/', credentials = ADMIN) {
  await page.goto(path);
  await page.getByLabel('Name').fill(credentials.name);
  await page.getByLabel('Password').fill(credentials.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible();
}

/** The status endpoint answers 503, as when the server is unwell. */
async function breakStatusEndpoint(page: Page) {
  await page.route('**/api/v1/system/status', (route) => route.fulfill({ status: 503 }));
}

test.describe('smoke @smoke', () => {
  test('the application is alive and ready', { tag: '@browserless' }, async ({ request }) => {
    expect((await request.get('/health/live')).status()).toBe(200);
    expect((await request.get('/health/ready')).status()).toBe(200);
  });

  test(
    'on the real image, an unknown API path or file is a 404 and not the HTML shell',
    { tag: '@browserless' },
    async ({ playwright, baseURL }) => {
      const request = await signedInRequest(playwright, baseURL ?? '');
      for (const path of ['/api/v1/nothing-here', '/missing.js']) {
        const response = await request.get(path);

        expect(response.status(), path).toBe(404);
        expect(response.headers()['content-type'] ?? '', path).not.toContain('text/html');
      }
    },
  );

  test('nothing of the API is open to somebody who is not signed in', { tag: '@browserless' }, async ({ request }) => {
    for (const path of ['/api/v1/system/status', '/api/v1/accounts', '/openapi/v1.json', '/api/v1/nothing-here']) {
      expect((await request.get(path)).status(), path).toBe(401);
    }
    expect((await request.get('/health/live')).status()).toBe(200);
  });

  test(
    'the application shell of the real image carries the security headers',
    { tag: '@browserless' },
    async ({ request }) => {
      const response = await request.get('/');
      const headers = response.headers();

      expect(response.status()).toBe(200);
      expect(headers['x-content-type-options']).toBe('nosniff');
      expect(headers['content-security-policy']).toContain("default-src 'self'");
    },
  );

  test('the shell shows the brand, a logo that loaded, and the whole menu with the unbuilt pages marked as soon', async ({
    page,
  }) => {
    await signIn(page);

    const header = page.getByRole('banner');
    await expect(header.getByRole('link', { name: 'Mnemarr' })).toBeVisible();
    const logo = header.locator('img');
    await expect(logo).toBeVisible();
    expect(await logo.evaluate((image: HTMLImageElement) => image.naturalWidth)).toBeGreaterThan(0);

    const menu = page.getByRole('navigation', { name: 'Main menu' });
    await expect(menu.getByRole('link', { name: 'Overview' })).toBeVisible();
    await expect(menu.getByRole('link', { name: 'System' })).toBeVisible();
    await expect(menu.getByRole('link', { name: 'Sources' })).toHaveCount(0);
    await expect(menu).toContainText('Sources');
    await expect(menu).toContainText('Soon');
  });

  test('the overview says the API is reachable', async ({ page }) => {
    await signIn(page);

    await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible();
    await expect(page.getByText('API reachable')).toBeVisible();
    await expect(page.getByText('Nothing to remember yet.')).toBeVisible();
  });

  test('the menu leads to the system status, which shows real values', async ({ page }) => {
    await signIn(page);
    await page.getByRole('navigation', { name: 'Main menu' }).getByRole('link', { name: 'System' }).click();

    await expect(page).toHaveURL(/\/system\/status$/);
    await expect(page).toHaveTitle('Status - Mnemarr');
    const about = page.getByRole('region', { name: 'About' });
    await expect(about.getByText('Version')).toBeVisible();
    // A label with no value would still show: check that the uptime reads like a duration.
    await expect(about).toContainText(/Uptime\s*\d+[smhd]/);
    // The start time is parsed by the browser from what the API sent: a drift in its format shows as this.
    await expect(about).not.toContainText('Invalid Date');
  });

  test('an address typed in the browser is answered with the application, for a page and for a missing page', async ({
    page,
  }) => {
    await signIn(page);
    const found = await page.goto('/system/status');
    expect(found?.status()).toBe(200);
    await expect(page.getByRole('heading', { level: 1, name: 'Status' })).toBeVisible();

    const missing = await page.goto('/nowhere');
    expect(missing?.status()).toBe(200);
    await expect(page.getByRole('heading', { level: 1, name: 'Page not found' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to the overview' })).toBeVisible();
  });

  test('the pages load without any console error or Content-Security-Policy violation', async ({ page }) => {
    const problems: string[] = [];
    page.on('console', (message) => {
      // A person who is not signed in is answered 401 by the session probe: the browser logs it. It is expected, and nothing else is.
      const isSessionProbe = message.location().url.endsWith('/api/v1/auth/session');
      if (message.type() === 'error' && !isSessionProbe) problems.push(message.text());
    });
    page.on('pageerror', (error) => problems.push(error.message));

    await signIn(page);
    await expect(page.getByText('API reachable')).toBeVisible();
    await page.goto('/system/status');
    await expect(page.getByRole('region', { name: 'About' }).getByText('Version')).toBeVisible();

    expect(problems).toEqual([]);
  });

  test('the overview has no accessibility violation, online or unreachable', async ({ page }) => {
    await signIn(page);
    await expect(page.getByText('API reachable')).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

    await breakStatusEndpoint(page);
    await page.reload();
    await expect(page.getByText('Unreachable')).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  });

  test('the system status has no accessibility violation, with its values or with its error', async ({ page }) => {
    await signIn(page, '/system/status');
    await expect(page.getByRole('region', { name: 'About' }).getByText('Version')).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

    await breakStatusEndpoint(page);
    await page.reload();
    await expect(page.getByRole('alert')).toContainText('The server answered 503.');
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  });

  test('somebody who is not signed in sees the sign-in page and nothing else, and it is accessible', async ({
    page,
  }) => {
    await page.goto('/');

    await expect(page.getByRole('heading', { name: 'Sign in to Mnemarr' })).toBeVisible();
    await expect(page.getByRole('navigation', { name: 'Main menu' })).toHaveCount(0);
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  });

  test('a wrong password is told in one way, and the right one signs in and out', async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Name').fill(ADMIN.name);
    await page.getByLabel('Password').fill('not the password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toHaveText('The name or the password is wrong.');

    await page.getByLabel('Password').fill(ADMIN.password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('banner')).toContainText('admin');
    await page.getByRole('button', { name: 'Sign out' }).click();

    await expect(page.getByRole('heading', { name: 'Sign in to Mnemarr' })).toBeVisible();
  });

  test('a reader is shown the notes menu only, without the system or the accounts', async ({
    page,
    playwright,
    baseURL,
  }) => {
    const admin = await signedInRequest(playwright, baseURL ?? '');
    const reader = { name: `reader-${Date.now()}`, password: 'a long enough password' };
    expect((await admin.post('/api/v1/accounts', { data: { ...reader, kind: 'User', role: 'Reader' } })).status()).toBe(
      201,
    );

    await signIn(page, '/', reader);

    const menu = page.getByRole('navigation', { name: 'Main menu' });
    await expect(menu.getByRole('link', { name: 'Overview' })).toBeVisible();
    await expect(menu.getByRole('link', { name: 'System' })).toHaveCount(0);
    await expect(menu.getByRole('link', { name: 'Accounts' })).toHaveCount(0);
  });

  test('an administrator gives a service a key, which works until it is revoked', async ({
    page,
    playwright,
    baseURL,
  }) => {
    const service = `agent-${Date.now()}`;
    await signIn(page, '/accounts');
    const form = page.getByRole('region', { name: 'Create an account' });
    await form.getByLabel('Name').fill(service);
    await form.getByLabel('Kind').selectOption('Service');
    await form.getByRole('button', { name: 'Create' }).click();
    await page.getByRole('link', { name: `Keys of ${service}` }).click();

    await page.getByLabel('Name of the key').fill('laptop');
    await page.getByRole('button', { name: 'Create' }).click();
    const secret = (await page.getByRole('region', { name: 'New key' }).locator('code').textContent()) ?? '';
    expect(secret).toMatch(/^mnm_/);
    await page.getByRole('button', { name: 'Done' }).click();
    await expect(page.getByText(secret)).toHaveCount(0);

    const agent = await playwright.request.newContext({ baseURL, extraHTTPHeaders: { 'X-Api-Key': secret } });
    const session = await agent.get('/api/v1/auth/session');
    expect(session.status()).toBe(200);
    expect((await session.json()) as { kind: string }).toMatchObject({ kind: 'Service', role: 'Assistant' });

    await page.getByRole('button', { name: 'Revoke the key laptop' }).click();
    await expect(page.getByText('Revoked')).toBeVisible();
    expect((await agent.get('/api/v1/auth/session')).status()).toBe(401);
  });
});
