import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { type ApiAnswer, findCall, renderWithProviders, rowOf, sentBody, stubApi } from '__tests__/render';
import Accounts from 'Accounts/Accounts';
import type { AccountItem, AuditItem, RoleItem } from 'Accounts/accounts';

const roles: RoleItem[] = [
  {
    name: 'Admin',
    description: '',
    permissions: ['system.read', 'accounts.manage', 'notes.read', 'search'],
    forUsers: true,
    forServices: false,
  },
  {
    name: 'Operator',
    description: '',
    permissions: ['system.read', 'notes.read', 'search'],
    forUsers: true,
    forServices: true,
  },
  { name: 'Reader', description: '', permissions: ['notes.read', 'search'], forUsers: true, forServices: true },
  {
    name: 'Assistant',
    description: '',
    permissions: ['notes.read', 'notes.write', 'search'],
    forUsers: false,
    forServices: true,
  },
  { name: 'Collector', description: '', permissions: ['ingest.write'], forUsers: false, forServices: true },
];

const accounts: AccountItem[] = [
  { id: 'a1', name: 'admin', kind: 'User', role: 'Admin', enabled: true, createdAt: '2026-11-03T09:00:00Z' },
  { id: 'r2', name: 'alice', kind: 'User', role: 'Reader', enabled: true, createdAt: '2026-11-03T09:00:00Z' },
  { id: 's3', name: 'laptop', kind: 'Service', role: 'Collector', enabled: false, createdAt: '2026-11-03T09:00:00Z' },
];

const audit: AuditItem[] = [
  { id: 1, at: '2026-11-03T09:05:00Z', actor: 'admin', action: 'account.created', target: 'alice', detail: '' },
];

function server(extra: (method: string, path: string, body: unknown) => ApiAnswer | undefined = () => undefined) {
  return stubApi((method, path, init) => {
    const body = init?.body ? (JSON.parse(init.body as string) as unknown) : undefined;
    const special = extra(method, path, body);
    if (special) {
      return special;
    }
    if (method === 'GET' && path === '/accounts') {
      return { body: accounts };
    }
    if (method === 'GET' && path === '/roles') {
      return { body: roles };
    }
    if (method === 'GET' && path.startsWith('/audit')) {
      return { body: audit };
    }
    return { status: 204 };
  });
}

describe('Accounts', () => {
  it('lists every account with its kind, its role and whether it is enabled', async () => {
    server();

    renderWithProviders(<Accounts />);

    const alice = rowOf(await screen.findByRole('rowheader', { name: 'alice' }));
    expect(within(alice).getByText('User')).toBeInTheDocument();
    expect(within(alice).getByRole('combobox', { name: 'Role of alice' })).toHaveValue('Reader');
    expect(within(alice).getByText('Enabled')).toBeInTheDocument();
    const laptop = rowOf(screen.getByRole('rowheader', { name: 'laptop' }));
    expect(within(laptop).getByText('Service')).toBeInTheDocument();
    expect(within(laptop).getByText('Disabled')).toBeInTheDocument();
  });

  it('offers a user only the roles for users and a service only those for services', async () => {
    server();

    renderWithProviders(<Accounts />);

    const user = await screen.findByRole('combobox', { name: 'Role of alice' });
    const service = screen.getByRole('combobox', { name: 'Role of laptop' });
    expect(
      within(user)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Admin', 'Operator', 'Reader']);
    expect(
      within(service)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Operator', 'Reader', 'Assistant', 'Collector']);
  });

  it('changes a role at once, keeping the state of the account', async () => {
    const fetchMock = server();
    renderWithProviders(<Accounts />);

    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Role of alice' }), 'Operator');

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          ([url, init]) => url === '/api/v1/accounts/r2' && (init as RequestInit).method === 'PUT',
        ),
      ).toBe(true),
    );
    const call = findCall(fetchMock, ([url]) => url === '/api/v1/accounts/r2');
    expect(sentBody(call)).toEqual({ role: 'Operator', enabled: true });
  });

  it('disables an enabled account and enables a disabled one', async () => {
    const fetchMock = server();
    renderWithProviders(<Accounts />);
    const alice = rowOf(await screen.findByRole('rowheader', { name: 'alice' }));
    const laptop = rowOf(screen.getByRole('rowheader', { name: 'laptop' }));

    await userEvent.click(within(alice).getByRole('button', { name: 'Disable' }));
    await userEvent.click(within(laptop).getByRole('button', { name: 'Enable' }));

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'PUT'),
      ).toHaveLength(2),
    );
    const bodies = fetchMock.mock.calls
      .filter(([, init]) => (init as RequestInit | undefined)?.method === 'PUT')
      .map((call) => sentBody(call));
    expect(bodies).toEqual([
      { role: 'Reader', enabled: false },
      { role: 'Collector', enabled: true },
    ]);
  });

  it('deletes only after a second press that names the account, and never offers to delete oneself', async () => {
    const fetchMock = server();
    renderWithProviders(<Accounts />);
    const alice = rowOf(await screen.findByRole('rowheader', { name: 'alice' }));
    const self = rowOf(screen.getByRole('rowheader', { name: 'admin' }));
    expect(within(self).queryByRole('button', { name: /Delete/ })).not.toBeInTheDocument();

    await userEvent.click(within(alice).getByRole('button', { name: 'Delete' }));
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE')).toBe(false);
    await userEvent.click(within(alice).getByRole('button', { name: 'Confirm: delete alice' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/api/v1/accounts/r2', expect.objectContaining({ method: 'DELETE' })),
    );
  });

  it('shows the reason of the server when a change is refused', async () => {
    server((method, path) =>
      method === 'PUT' && path === '/accounts/r2'
        ? { status: 409, body: { title: 'The last enabled administrator cannot be removed.' } }
        : undefined,
    );
    renderWithProviders(<Accounts />);

    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Role of alice' }), 'Operator');

    expect(await screen.findByRole('alert')).toHaveTextContent('The last enabled administrator cannot be removed.');
  });

  it('shows the loading state, and not an error, while the roles are still being read', async () => {
    const rolesAnswer: { promise?: Promise<unknown>; resolve?: (value: unknown) => void } = {};
    rolesAnswer.promise = new Promise((resolve) => {
      rolesAnswer.resolve = resolve;
    });
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string) =>
        url === '/api/v1/roles'
          ? rolesAnswer.promise
          : Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(accounts) }),
      ),
    );
    renderWithProviders(<Accounts />);

    expect(await screen.findByText('Loading…')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    rolesAnswer.resolve?.({ ok: true, status: 200, json: () => Promise.resolve(roles) });

    expect(await screen.findByRole('rowheader', { name: 'alice' })).toBeInTheDocument();
  });

  it('says so when the roles cannot be read', async () => {
    server((method, path) => (method === 'GET' && path === '/roles' ? { status: 503 } : undefined));
    renderWithProviders(<Accounts />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not read the accounts.');
  });

  it('disarms a deletion that was started and then given up', async () => {
    const fetchMock = server();
    renderWithProviders(<Accounts />);
    const alice = rowOf(await screen.findByRole('rowheader', { name: 'alice' }));

    await userEvent.click(within(alice).getByRole('button', { name: 'Delete' }));
    await userEvent.click(within(alice).getByRole('button', { name: 'Cancel' }));

    expect(within(alice).getByRole('button', { name: 'Delete' })).toBeInTheDocument();
    expect(within(alice).queryByRole('button', { name: /Confirm/ })).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE')).toBe(false);
  });

  it('stops showing a refusal once a later change has worked', async () => {
    let refuse = true;
    server((method, path) => {
      if (method !== 'PUT' || path !== '/accounts/r2') {
        return undefined;
      }
      const answer = refuse ? { status: 409, body: { title: 'Refused for now.' } } : { status: 204 };
      refuse = false;
      return answer;
    });
    renderWithProviders(<Accounts />);
    const roleOfAlice = await screen.findByRole('combobox', { name: 'Role of alice' });

    await userEvent.selectOptions(roleOfAlice, 'Operator');
    expect(await screen.findByRole('alert')).toHaveTextContent('Refused for now.');
    await userEvent.selectOptions(roleOfAlice, 'Reader');

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  it('sets the password of a user, and has no such button for a service', async () => {
    const fetchMock = server();
    renderWithProviders(<Accounts />);
    const alice = rowOf(await screen.findByRole('rowheader', { name: 'alice' }));
    const laptop = rowOf(screen.getByRole('rowheader', { name: 'laptop' }));
    expect(within(laptop).queryByRole('button', { name: 'Set a password' })).not.toBeInTheDocument();

    await userEvent.click(within(alice).getByRole('button', { name: 'Set a password' }));
    await userEvent.type(screen.getByLabelText('New password for alice'), 'another long password');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/v1/accounts/r2/password',
        expect.objectContaining({ method: 'PUT' }),
      ),
    );
    const call = findCall(fetchMock, ([url]) => url === '/api/v1/accounts/r2/password');
    expect(sentBody(call)).toEqual({ password: 'another long password' });
    await waitFor(() => expect(screen.queryByLabelText('New password for alice')).not.toBeInTheDocument());
  });

  it('lets a password form be cancelled, and shows a password the server refuses', async () => {
    server((_method, path) =>
      path.endsWith('/password')
        ? { status: 400, body: { title: 'A password is at least 12 characters.' } }
        : undefined,
    );
    renderWithProviders(<Accounts />);
    const alice = rowOf(await screen.findByRole('rowheader', { name: 'alice' }));

    await userEvent.click(within(alice).getByRole('button', { name: 'Set a password' }));
    await userEvent.type(screen.getByLabelText('New password for alice'), 'short');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('A password is at least 12 characters.');
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByLabelText('New password for alice')).not.toBeInTheDocument();
  });

  it('creates a user with a password, then empties the form', async () => {
    const fetchMock = server();
    renderWithProviders(<Accounts />);
    await screen.findByRole('rowheader', { name: 'alice' });
    const form = screen.getByRole('region', { name: 'Create an account' });

    await userEvent.type(within(form).getByLabelText('Name'), 'bob');
    await userEvent.type(within(form).getByLabelText('Password'), 'a long enough password');
    await userEvent.click(within(form).getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/api/v1/accounts', expect.objectContaining({ method: 'POST' })),
    );
    const call = findCall(
      fetchMock,
      ([url, init]) => url === '/api/v1/accounts' && (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(sentBody(call)).toEqual({ name: 'bob', kind: 'User', role: 'Reader', password: 'a long enough password' });
    await waitFor(() => expect(within(form).getByLabelText('Name')).toHaveValue(''));
  });

  it('creates a service account with no password and the role that can do the least to begin with', async () => {
    const fetchMock = server();
    renderWithProviders(<Accounts />);
    await screen.findByRole('rowheader', { name: 'alice' });
    const form = screen.getByRole('region', { name: 'Create an account' });

    await userEvent.selectOptions(within(form).getByLabelText('Kind'), 'Service');
    expect(within(form).queryByLabelText('Password')).not.toBeInTheDocument();
    expect(within(form).getByLabelText('Role')).toHaveValue('Collector');
    await userEvent.type(within(form).getByLabelText('Name'), 'chat-runner');
    await userEvent.click(within(form).getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/api/v1/accounts', expect.objectContaining({ method: 'POST' })),
    );
    const call = findCall(
      fetchMock,
      ([url, init]) => url === '/api/v1/accounts' && (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(sentBody(call)).toEqual({ name: 'chat-runner', kind: 'Service', role: 'Collector' });
  });

  it('offers keys to service accounts only, since a person signs in with a password', async () => {
    server();
    renderWithProviders(<Accounts />);
    await screen.findByRole('rowheader', { name: 'alice' });

    expect(
      within(rowOf(screen.getByRole('rowheader', { name: 'laptop' }))).getByRole('link', { name: 'Keys of laptop' }),
    ).toBeInTheDocument();
    expect(within(rowOf(screen.getByRole('rowheader', { name: 'alice' }))).queryByRole('link')).not.toBeInTheDocument();
  });

  it('shows why an account was refused', async () => {
    server((method, path) =>
      method === 'POST' && path === '/accounts'
        ? { status: 409, body: { title: "An account named 'bob' already exists." } }
        : undefined,
    );
    renderWithProviders(<Accounts />);
    await screen.findByRole('rowheader', { name: 'alice' });
    const form = screen.getByRole('region', { name: 'Create an account' });

    await userEvent.type(within(form).getByLabelText('Name'), 'bob');
    await userEvent.type(within(form).getByLabelText('Password'), 'a long enough password');
    await userEvent.click(within(form).getByRole('button', { name: 'Create' }));

    expect(await within(form).findByRole('alert')).toHaveTextContent("An account named 'bob' already exists.");
  });

  it('shows the recent activity with the time in UTC', async () => {
    server();

    renderWithProviders(<Accounts />);

    const activity = await screen.findByRole('region', { name: 'Recent activity' });
    const row = rowOf(await within(activity).findByText('account.created'));
    expect(within(row).getByText('2026-11-03 09:05')).toBeInTheDocument();
    expect(within(row).getByText('alice')).toBeInTheDocument();
  });

  it('says when the accounts cannot be read, and when the activity cannot', async () => {
    stubApi(() => ({ status: 500 }));

    renderWithProviders(<Accounts />);

    await waitFor(() => expect(screen.getAllByRole('alert')).toHaveLength(2));
  });
});
