import AxeBuilder from '@axe-core/playwright';
import { expect, type Page, type PlaywrightWorkerArgs, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';

/** The administrator of the e2e stack (tests/e2e/stack/docker-compose.yml): a test-only value. */
const ADMIN = { name: 'admin', password: 'e2e-admin-password' };

/** A name that no other run, and no other test, has used: the stack keeps its accounts for the whole run. */
const unique = (prefix: string) => `${prefix}-${randomUUID().slice(0, 8)}`;

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
      await request.dispose();
    },
  );

  test(
    'the real server refuses a sign-in body that is larger than a name and a password can be',
    { tag: '@browserless' },
    async ({ request }) => {
      const response = await request.post('/api/v1/auth/login', {
        headers: { 'X-Mnemarr-Request': '1' },
        data: { name: 'admin', password: 'p'.repeat(20_000) },
      });

      expect(response.status()).toBe(413);
    },
  );

  test(
    'the real image applies the closed-by-default rule to an unknown API path and keeps the probes anonymous',
    { tag: '@browserless' },
    async ({ request }) => {
      // The matrix of every endpoint is in the integration tests; here, only that the real image closes the doors it does not know.
      expect((await request.get('/api/v1/nothing-here')).status()).toBe(401);
      expect((await request.get('/health/live')).status()).toBe(200);
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
    await signIn(page);

    const header = page.getByRole('banner');
    await expect(header.getByRole('link', { name: 'Mnemarr' })).toBeVisible();
    const logo = header.locator('img');
    await expect(logo).toBeVisible();
    expect(await logo.evaluate((image: HTMLImageElement) => image.naturalWidth)).toBeGreaterThan(0);

    const menu = page.getByRole('navigation', { name: 'Main menu' });
    await expect(menu.getByRole('link', { name: 'Overview' })).toBeVisible();
    await expect(menu.getByRole('link', { name: 'System' })).toBeVisible();
    await expect(menu.getByRole('link', { name: 'Notes' })).toBeVisible();
    await expect(menu.getByRole('link', { name: 'Sources' })).toBeVisible();
    await expect(menu.getByRole('link', { name: 'Agents' })).toHaveCount(0);
    await expect(menu).toContainText('Agents');
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
    // ... and the probe itself may only answer "signed in" or "nobody": any other answer is a real failure, which the filter above must not hide.
    page.on('response', (response) => {
      if (response.url().endsWith('/api/v1/auth/session') && ![200, 401].includes(response.status())) {
        problems.push(`session probe answered ${response.status()}`);
      }
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

  test('a wrong password is told in one way, and the right one signs in and out', async ({
    page,
    playwright,
    baseURL,
  }) => {
    // A throwaway person: a failed sign-in counts against the name, and every other test relies on the administrator.
    const admin = await signedInRequest(playwright, baseURL ?? '');
    const person = { name: unique('person'), password: 'a long enough password' };
    expect((await admin.post('/api/v1/accounts', { data: { ...person, kind: 'User', role: 'Reader' } })).status()).toBe(
      201,
    );
    await admin.dispose();
    await page.goto('/');
    await page.getByLabel('Name').fill(person.name);
    await page.getByLabel('Password').fill('not the password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toHaveText('The name or the password is wrong.');

    await page.getByLabel('Password').fill(person.password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('banner')).toContainText(person.name);
    await page.getByRole('button', { name: 'Sign out' }).click();

    await expect(page.getByRole('heading', { name: 'Sign in to Mnemarr' })).toBeVisible();
  });

  test('a reader does not see the System or Accounts entries of the menu', async ({ page, playwright, baseURL }) => {
    const admin = await signedInRequest(playwright, baseURL ?? '');
    const reader = { name: unique('reader'), password: 'a long enough password' };
    expect((await admin.post('/api/v1/accounts', { data: { ...reader, kind: 'User', role: 'Reader' } })).status()).toBe(
      201,
    );

    await admin.dispose();

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
    const service = unique('agent');
    await signIn(page, '/accounts');
    const form = page.getByRole('region', { name: 'Create an account' });
    await form.getByLabel('Name').fill(service);
    await form.getByLabel('Kind').selectOption('Service');
    await form.getByLabel('Role').selectOption('Assistant');
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
    await form.getByRole('button', { name: 'Create' }).click();
    await page.getByRole('link', { name: `Keys of ${service}` }).click();

    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
    await page.getByLabel('Name of the key').fill('laptop');
    await page.getByRole('button', { name: 'Create' }).click();
    const secret = (await page.getByRole('region', { name: 'New key' }).locator('code').textContent()) ?? '';
    expect(secret).toMatch(/^mnm_/);
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
    await page.getByRole('button', { name: 'Done' }).click();
    await expect(page.getByText(secret)).toHaveCount(0);

    const agent = await playwright.request.newContext({ baseURL, extraHTTPHeaders: { 'X-Api-Key': secret } });
    const session = await agent.get('/api/v1/auth/session');
    expect(session.status()).toBe(200);
    expect((await session.json()) as { kind: string }).toMatchObject({ kind: 'Service', role: 'Assistant' });

    await page.getByRole('button', { name: 'Revoke the key laptop' }).click();
    await expect(page.getByText('Revoked')).toBeVisible();
    expect((await agent.get('/api/v1/auth/session')).status()).toBe(401);
    await agent.dispose();
  });

  test('an operator declares a folder of notes, indexes it and finds a note by its words', async ({ page }) => {
    const name = unique('demo');
    await signIn(page, '/sources');
    // The base is on the page whatever its state: the memory vault is required, and there can be only one.
    await expect(
      page.getByRole('region', { name: 'Memory vault' }).getByText(/required, and there can be only one/),
    ).toBeVisible();
    const form = page.getByRole('form', { name: 'Declare a source of Knowledge vaults' });
    await expect(form.getByText('A source is a folder inside one of: /notes, /config/sources, /work')).toBeVisible();
    await form.getByLabel('Name').fill(name);
    await form.getByLabel('Folder', { exact: true }).fill('/notes/demo');
    await form.getByRole('button', { name: 'Create' }).click();
    const row = page.getByRole('row', { name: new RegExp(name) });
    await expect(row).toBeVisible();

    await row.getByRole('button', { name: `Index now for ${name}` }).click();
    // The timer of the service may have scanned the new source first: either way the pass is done and the note is there.
    await expect(row.getByRole('status')).toContainText(' added, ');

    // The notes are searched in the reader, by anybody who may read them.
    await page.goto('/notes');
    const search = page.getByRole('region', { name: 'Search' });
    await search.getByLabel('Search the notes').fill('layers');
    await search.getByRole('button', { name: 'Search' }).click();
    await expect(search.getByText(`${name} · docker.md · Docker > Images`)).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

    await page.goto('/sources');
    const declared = page.getByRole('row', { name: new RegExp(name) });
    await declared.getByRole('button', { name: 'Delete' }).click();
    await declared.getByRole('button', { name: `Confirm: delete ${name}` }).click();
    await expect(page.getByRole('row', { name: new RegExp(name) })).toHaveCount(0);
  });

  test('an operator declares a Git source, is told when the host is not allowed, and sees why a sync failed', async ({
    page,
  }) => {
    const name = unique('git');
    await signIn(page, '/sources');
    const form = page.getByRole('form', { name: 'Declare a source of Knowledge vaults' });
    await form.getByLabel('Name').fill(name);
    await form.getByLabel('Origin').selectOption('git');
    await form.getByLabel('Folder to clone into').fill(`/config/sources/${name}`);

    // A host the operator did not allow is refused, in words, and nothing is declared.
    await form.getByLabel('Repository address').fill('https://elsewhere.example/team/notes');
    await form.getByRole('button', { name: 'Create' }).click();
    await expect(form.getByRole('alert')).toContainText('does not allow');
    await expect(page.getByRole('row', { name: new RegExp(name) })).toHaveCount(0);

    await form.getByLabel('Repository address').fill('https://git.example/team/notes');
    await form.getByRole('button', { name: 'Create' }).click();
    const row = page.getByRole('row', { name: new RegExp(name) });
    await expect(row).toContainText('Git: https://git.example/team/notes');
    await expect(row).toContainText('Not synchronised yet');

    // The real engine runs in the real image: the host cannot be resolved, which is said as a code and in words, not as a message of the library.
    await row.getByRole('button', { name: `Sync now for ${name}` }).click();
    await expect(row.getByRole('alert')).toContainText('The last sync failed: the host could not be reached');
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

    await row.getByRole('button', { name: 'Delete' }).click();
    await row.getByRole('button', { name: `Confirm: delete ${name}` }).click();
    await expect(page.getByRole('row', { name: new RegExp(name) })).toHaveCount(0);
  });

  test('a conversation sent by a collector shows in the notes, cannot be edited, and is found by a search', async ({
    page,
    playwright,
    baseURL,
  }) => {
    const word = unique('zebra').replaceAll('-', '');
    const api = await signedInRequest(playwright, baseURL ?? '');
    const account = await api.post('/api/v1/accounts', {
      data: { name: unique('collector'), kind: 'Service', role: 'Collector' },
    });
    expect(account.status()).toBe(201);
    const key = await api.post(`/api/v1/accounts/${(await account.json()).id}/keys`, { data: { name: 'e2e' } });
    const collector = await playwright.request.newContext({
      baseURL,
      extraHTTPHeaders: { 'X-Api-Key': (await key.json()).secret },
    });
    const line = (type: string, text: string, at: string) =>
      JSON.stringify({ type, timestamp: at, cwd: '/work/Mnemarr', message: { role: type, content: text } });

    const sent = await collector.post('/api/v1/ingest/conversations', {
      data: {
        format: 'claude-code',
        machine: unique('pc'),
        application: 'Mnemarr',
        session: unique('s'),
        from: 0,
        lines: [
          line('user', `What did we decide about ${word}?`, '2026-11-04T14:03:00Z'),
          line('assistant', 'We kept the cache in memory.', '2026-11-04T14:04:00Z'),
        ],
      },
    });
    expect(sent.status()).toBe(200);
    // The collector reads nothing: its key is for sending.
    expect((await collector.get('/api/v1/notes')).status()).toBe(403);

    await signIn(page, '/notes');
    await page.getByLabel('Filter the notes').fill(word);
    await page.getByRole('link', { name: `What did we decide about ${word}?` }).click();
    await expect(page.getByText('We kept the cache in memory.')).toBeVisible();
    await expect(page.getByText('This note is not edited here')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeDisabled();
    const found = await api.get(`/api/v1/search?q=${word}`);
    expect((await found.json()).length).toBeGreaterThan(0);
  });

  test('an operator writes notes: creates one, edits it, links to it, renames it and the link follows, removes it', async ({
    page,
    playwright,
    baseURL,
  }, testInfo) => {
    const name = unique('write');
    const api = await signedInRequest(playwright, baseURL ?? '');
    const folder = `/work/${testInfo.project.name}`;
    const created = await api.post('/api/v1/sources', {
      data: { name, kind: 'Knowledge', mode: 'ReadWrite', path: folder },
    });
    expect(created.status()).toBe(201);
    await signIn(page, '/notes');

    // A new note, in a source that is open for writing.
    await page.getByText('New note', { exact: true }).click();
    const form = page.getByRole('form', { name: 'New note' });
    await form.getByLabel('Source').selectOption({ label: name });
    await form.getByLabel('Path').fill(`${name}/First.md`);
    await form.getByRole('button', { name: 'Create note' }).click();
    await expect(page.getByText(`${name} · ${name}/First.md`)).toBeVisible();

    // Edit it: the whole text, with a preview before saving.
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page
      .getByRole('textbox', { name: 'Note text' })
      .fill('# First\n\nBodies of water.\n\n## Part One\n\nDeep words.\n');
    await page.getByRole('button', { name: 'Preview' }).click();
    await expect(page.getByRole('region', { name: 'Preview' }).getByText('Bodies of water.')).toBeVisible();
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByRole('textbox', { name: 'Note text' })).toHaveCount(0);
    await expect(page.getByText('Bodies of water.')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Part One' })).toHaveAttribute('id', 'part-one');

    // A second note that links to the first one, by a heading.
    await page.getByText('New note', { exact: true }).click();
    const second = page.getByRole('form', { name: 'New note' });
    await second.getByLabel('Source').selectOption({ label: name });
    await second.getByLabel('Path').fill(`${name}/Second.md`);
    await second.getByRole('button', { name: 'Create note' }).click();
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByRole('textbox', { name: 'Note text' }).fill('# Second\n\nSee [[First#Part One]].\n');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByRole('link', { name: 'First#Part One' })).toBeVisible();

    // Rename the first note: the link in the second follows.
    await page.getByRole('link', { name: 'First', exact: true }).first().click();
    await page.getByRole('button', { name: 'Rename', exact: true }).click();
    await page.getByLabel('New path').fill(`${name}/Renamed.md`);
    await page.getByRole('form', { name: 'Rename' }).getByRole('button', { name: 'Rename' }).click();
    await expect(page.getByText(`${name} · ${name}/Renamed.md`)).toBeVisible();
    await expect(page.getByRole('region', { name: 'Backlinks' }).getByRole('link', { name: 'Second' })).toBeVisible();
    const id = (await created.json()).id as string;
    const link = await api.get(`/api/v1/notes/${id}/${name}/Second.md`);
    expect((await link.json()).raw).toContain('[[Renamed#Part One]]');

    // Remove it: asked twice, then gone.
    await page.getByRole('button', { name: 'Remove', exact: true }).click();
    await page.getByRole('button', { name: /Confirm: remove/ }).click();
    await expect(page.getByText(`${name} · ${name}/Renamed.md`)).toHaveCount(0);
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

    // Clean up what the test declared.
    await api.delete(`/api/v1/sources/${id}`);
    await api.dispose();
  });

  test('a person reads the notes: the tree, a rendered note, a wikilink to another source, and the backlinks', async ({
    page,
  }) => {
    // The sources of the stack come from its configuration file and are scanned on a timer: index them now, as an operator would.
    await signIn(page, '/sources');
    for (const name of ['seeded', 'memory']) {
      const row = page.getByRole('row', { name: new RegExp(name) });
      await row.getByRole('button', { name: `Index now for ${name}` }).click();
      await expect(row.getByRole('status')).toBeVisible();
    }
    await page.goto('/notes');

    const tree = page.getByRole('navigation', { name: 'Notes' });
    await expect(tree.getByText('seeded')).toBeVisible();
    // The folder of this note is also declared by another journey, in parallel: this one reads the source the stack's file describes.
    await tree.getByRole('region', { name: 'seeded' }).getByRole('link', { name: 'Docker' }).click();
    await expect(page.getByRole('heading', { name: 'Docker' })).toBeVisible();
    await expect(page.getByText('An image is made of read-only layers')).toBeVisible();
    await expect(page.getByRole('list', { name: 'Tags' }).getByRole('link', { name: '#infra' })).toBeVisible();
    await expect(
      page.getByRole('region', { name: 'Backlinks' }).getByRole('link', { name: 'A decision' }),
    ).toBeVisible();
    // A Mermaid diagram is drawn in the real browser, under the real Content-Security-Policy, as an image.
    const diagram = page.getByRole('img', { name: 'Diagram' });
    await expect(diagram).toBeVisible();
    expect(await diagram.evaluate((image: HTMLImageElement) => image.naturalWidth)).toBeGreaterThan(0);
    expect(await diagram.getAttribute('src')).toMatch(/^data:image\/svg\+xml/);
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

    await page.getByRole('article').getByRole('link', { name: 'decision', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'A decision' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Backlinks' }).getByRole('link', { name: 'Docker' })).toBeVisible();
  });

  test('the configuration file given at start is applied: its sources are managed and cannot be changed by hand', async ({
    page,
  }) => {
    await signIn(page, '/sources');

    const memory = page.getByRole('region', { name: 'Memory vault' });
    await expect(memory.getByRole('row', { name: /memory/ }).getByText('Managed by configuration')).toBeVisible();
    await expect(memory.getByRole('button', { name: /Change/ })).toHaveCount(0);
    await expect(memory.getByRole('alert')).toHaveCount(0);
    const seeded = page.getByRole('region', { name: 'Knowledge vaults' }).getByRole('row', { name: /seeded/ });
    await expect(seeded.getByText('Managed by configuration')).toBeVisible();
    await expect(seeded.getByRole('button', { name: 'Delete' })).toHaveCount(0);

    const card = page.getByRole('region', { name: 'Configuration as code' });
    await expect(card.getByLabel('Current configuration (YAML)')).toHaveValue(/seeded/);
    await card.getByLabel('Configuration to apply (YAML)').fill('knowledge: []');
    await card.getByRole('button', { name: 'Preview' }).click();
    await expect(card.getByText('Remove Knowledge seeded')).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  });
});
