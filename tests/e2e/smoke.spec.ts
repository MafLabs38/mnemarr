import AxeBuilder from '@axe-core/playwright';
import { expect, type Page, test } from '@playwright/test';

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
    async ({ request }) => {
      for (const path of ['/api/v1/nothing-here', '/missing.js']) {
        const response = await request.get(path);

        expect(response.status(), path).toBe(404);
        expect(response.headers()['content-type'] ?? '', path).not.toContain('text/html');
      }
    },
  );

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
    await page.goto('/');

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
    await page.goto('/');

    await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible();
    await expect(page.getByText('API reachable')).toBeVisible();
    await expect(page.getByText('Nothing to remember yet.')).toBeVisible();
  });

  test('the menu leads to the system status, which shows real values', async ({ page }) => {
    await page.goto('/');
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
      if (message.type() === 'error') problems.push(message.text());
    });
    page.on('pageerror', (error) => problems.push(error.message));

    await page.goto('/');
    await expect(page.getByText('API reachable')).toBeVisible();
    await page.goto('/system/status');
    await expect(page.getByRole('region', { name: 'About' }).getByText('Version')).toBeVisible();

    expect(problems).toEqual([]);
  });

  test('the overview has no accessibility violation, online or unreachable', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByText('API reachable')).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

    await breakStatusEndpoint(page);
    await page.reload();
    await expect(page.getByText('Unreachable')).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  });

  test('the system status has no accessibility violation, with its values or with its error', async ({ page }) => {
    await page.goto('/system/status');
    await expect(page.getByRole('region', { name: 'About' }).getByText('Version')).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

    await breakStatusEndpoint(page);
    await page.reload();
    await expect(page.getByRole('alert')).toContainText('The server answered 503.');
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  });
});
