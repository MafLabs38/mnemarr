import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { adminSession, sentBody, stubApi } from '__tests__/render';
import { createQueryClient } from 'App/queryClient';
import Login from 'Login/Login';
import { sessionKey } from 'Session/session';

function renderLogin() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <Login />
    </QueryClientProvider>,
  );
  return client;
}

async function signInWith(name: string, password: string) {
  await userEvent.type(screen.getByLabelText('Name'), name);
  await userEvent.type(screen.getByLabelText('Password'), password);
  await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
}

describe('Login', () => {
  it('has a labelled name and a labelled password that is hidden', () => {
    stubApi(() => undefined);

    renderLogin();

    expect(screen.getByLabelText('Name')).toHaveAttribute('autocomplete', 'username');
    expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'password');
  });

  it('sends the name and the password, and starts the session with what comes back', async () => {
    const fetchMock = stubApi((method, path) =>
      method === 'POST' && path === '/auth/login' ? { body: adminSession } : undefined,
    );
    const client = renderLogin();

    await signInWith('admin', 'a long enough password');

    await waitFor(() => expect(client.getQueryData(sessionKey)).toEqual(adminSession));
    expect(sentBody(fetchMock.mock.calls[0] as unknown[])).toEqual({
      name: 'admin',
      password: 'a long enough password',
    });
  });

  it.each([
    [401, 'The name or the password is wrong.'],
    [429, 'Too many failed attempts. Try again later.'],
    [500, 'Signing in is not possible right now.'],
  ])('tells a person who was answered %i what to understand', async (status, text) => {
    stubApi(() => ({ status }));
    renderLogin();

    await signInWith('admin', 'nope');

    expect(await screen.findByRole('alert')).toHaveTextContent(text);
  });

  it('keeps the button from being pressed twice while it waits', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => new Promise(() => {})),
    );
    renderLogin();

    await signInWith('admin', 'x');

    expect(screen.getByRole('button', { name: 'Sign in' })).toBeDisabled();
  });

  it('keeps the form and says so when the application is run with its own query client and the answer is 401', async () => {
    stubApi((method, path) =>
      method === 'POST' && path === '/auth/login' ? { status: 401 } : { body: undefined, status: 401 },
    );
    const client = createQueryClient();
    render(
      <QueryClientProvider client={client}>
        <Login />
      </QueryClientProvider>,
    );

    await signInWith('alice', 'not the password');

    expect(await screen.findByRole('alert')).toHaveTextContent('The name or the password is wrong.');
    expect(screen.getByLabelText('Name')).toHaveValue('alice');
  });
});
