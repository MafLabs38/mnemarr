import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';

import { type ApiAnswer, findCall, renderWithProviders, rowOf, sentBody, stubApi } from '__tests__/render';
import AccountKeys from 'Accounts/AccountKeys';
import type { ApiKeyItem } from 'Accounts/accounts';

const key = (over: Partial<ApiKeyItem>): ApiKeyItem => ({
  id: 'k1',
  name: 'laptop',
  prefix: 'mnm_abcdefgh',
  sources: null,
  createdAt: '2026-11-03T09:00:00Z',
  expiresAt: null,
  lastUsedAt: null,
  revokedAt: null,
  ...over,
});

function server(keys: ApiKeyItem[], extra: (method: string, path: string) => ApiAnswer | undefined = () => undefined) {
  return stubApi((method, path) => {
    const special = extra(method, path);
    if (special) {
      return special;
    }
    if (method === 'GET' && path === '/accounts/s3') {
      return {
        body: {
          id: 's3',
          name: 'chat-runner',
          kind: 'Service',
          role: 'Assistant',
          enabled: true,
          createdAt: '2026-11-03T09:00:00Z',
        },
      };
    }
    if (method === 'GET' && path === '/accounts/s3/keys') {
      return { body: keys };
    }
    return { status: 204 };
  });
}

const renderKeys = () =>
  renderWithProviders(
    <Routes>
      <Route element={<AccountKeys />} path="/accounts/:id/keys" />
    </Routes>,
    '/accounts/s3/keys',
  );

describe('AccountKeys', () => {
  it('lists the keys of the account with their state, last use and expiry, and never a secret', async () => {
    server([
      key({ id: 'k1', name: 'active', lastUsedAt: '2026-11-03T10:00:00Z' }),
      key({ id: 'k2', name: 'old', revokedAt: '2026-11-03T11:00:00Z' }),
      key({ id: 'k3', name: 'lapsed', expiresAt: '2020-01-01T00:00:00Z' }),
    ]);

    renderKeys();

    expect(await screen.findByRole('heading', { level: 1, name: 'Keys of chat-runner' })).toBeInTheDocument();
    const active = rowOf(screen.getByRole('rowheader', { name: 'active' }));
    expect(within(active).getByText('Active')).toBeInTheDocument();
    expect(within(active).getByText('mnm_abcdefgh')).toBeInTheDocument();
    expect(within(active).getByText('2026-11-03 10:00')).toBeInTheDocument();
    expect(within(rowOf(screen.getByRole('rowheader', { name: 'old' }))).getByText('Revoked')).toBeInTheDocument();
    expect(within(rowOf(screen.getByRole('rowheader', { name: 'lapsed' }))).getByText('Expired')).toBeInTheDocument();
    expect(within(rowOf(screen.getByRole('rowheader', { name: 'old' }))).queryByRole('button')).not.toBeInTheDocument();
  });

  it('says when there is no key and links back to the accounts', async () => {
    server([]);

    renderKeys();

    expect(await screen.findByText('There is no key yet.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to the accounts' })).toHaveAttribute('href', '/accounts');
  });

  it('shows a new secret once, in a notice that says it will not be shown again, and lets it be copied', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const fetchMock = server([], (method, path) =>
      method === 'POST' && path === '/accounts/s3/keys'
        ? { status: 201, body: { key: key({ name: 'laptop' }), secret: 'mnm_the-whole-secret' } }
        : undefined,
    );
    renderKeys();

    await userEvent.type(await screen.findByLabelText('Name of the key'), 'laptop');
    await userEvent.click(screen.getByRole('button', { name: 'Create' }));

    const notice = await screen.findByRole('region', { name: 'New key' });
    expect(notice).toHaveTextContent('shown only once');
    expect(within(notice).getByText('mnm_the-whole-secret')).toBeInTheDocument();
    const call = findCall(
      fetchMock,
      ([url, init]) => url === '/api/v1/accounts/s3/keys' && (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(sentBody(call)).toEqual({ name: 'laptop' });
    await userEvent.click(within(notice).getByRole('button', { name: 'Copy' }));
    expect(writeText).toHaveBeenCalledWith('mnm_the-whole-secret');
    expect(await within(notice).findByRole('button', { name: 'Copied' })).toBeInTheDocument();
    await userEvent.click(within(notice).getByRole('button', { name: 'Done' }));
    expect(screen.queryByText('mnm_the-whole-secret')).not.toBeInTheDocument();
  });

  it('sends the day a key expires, as the start of that day in UTC', async () => {
    const fetchMock = server([], (method, path) =>
      method === 'POST' && path === '/accounts/s3/keys'
        ? { status: 201, body: { key: key({}), secret: 'mnm_x' } }
        : undefined,
    );
    renderKeys();

    await userEvent.type(await screen.findByLabelText('Name of the key'), 'temp');
    await userEvent.type(screen.getByLabelText('Expires on'), '2027-01-31');
    await userEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'POST')).toBe(true),
    );
    const call = findCall(fetchMock, ([, init]) => (init as RequestInit | undefined)?.method === 'POST');
    expect(sentBody(call)).toEqual({ name: 'temp', expiresAt: '2027-01-31T00:00:00.000Z' });
  });

  it('shows why a key was refused', async () => {
    server([], (method) =>
      method === 'POST' ? { status: 400, body: { title: 'A key cannot expire in the past.' } } : undefined,
    );
    renderKeys();

    await userEvent.type(await screen.findByLabelText('Name of the key'), 'temp');
    await userEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('A key cannot expire in the past.');
  });

  it('revokes a key', async () => {
    const fetchMock = server([key({ id: 'k1', name: 'laptop' })]);
    renderKeys();

    await userEvent.click(await screen.findByRole('button', { name: 'Revoke the key laptop' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/v1/accounts/s3/keys/k1',
        expect.objectContaining({ method: 'DELETE' }),
      ),
    );
  });

  it('says when the account or its keys cannot be read', async () => {
    stubApi(() => ({ status: 500 }));

    renderKeys();

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not read the accounts.');
  });
});
