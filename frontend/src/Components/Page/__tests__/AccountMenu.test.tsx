import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { readerSession, renderWithProviders, stubApi } from '__tests__/render';
import Page from 'Components/Page/Page';
import PageContent from 'Components/Page/PageContent';
import { sessionKey } from 'Session/session';

const renderPage = (session = readerSession) =>
  renderWithProviders(
    <Page>
      <PageContent title="Test">content</PageContent>
    </Page>,
    '/',
    session,
  );

describe('AccountMenu', () => {
  it('says who is signed in and with what role, and links to the account page', () => {
    stubApi(() => undefined);

    renderPage();

    const account = screen.getByRole('link', { name: /reader/ });
    expect(account).toHaveTextContent('Reader');
    expect(account).toHaveAttribute('href', '/account');
  });

  it('signs out, forgets what the session had read, and leaves nobody signed in', async () => {
    const fetchMock = stubApi(() => ({ status: 204 }));
    const { client } = renderPage();
    client.setQueryData(['api', '/accounts'], ['secret things']);

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));

    await waitFor(() => expect(client.getQueryData(sessionKey)).toBeNull());
    expect(client.getQueryData(['api', '/accounts'])).toBeUndefined();
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/auth/logout', expect.objectContaining({ method: 'POST' }));
  });

  it('stays signed in, and says so, when the server could not end the session: a reload would sign the person back in', async () => {
    stubApi(() => ({ status: 500 }));
    const { client } = renderPage();
    client.setQueryData(sessionKey, readerSession);

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not sign out. Try again.');
    expect(client.getQueryData(sessionKey)).toEqual(readerSession);
  });
});

describe('the menu of a person who may only read notes', () => {
  it('shows neither the system nor the accounts', () => {
    stubApi(() => undefined);

    renderPage();

    const menu = screen.getByRole('navigation', { name: 'Main menu' });
    expect(menu).toHaveTextContent(/Overview.*Sources.*Agents.*Settings/s);
    expect(screen.queryByRole('link', { name: 'System' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Accounts' })).not.toBeInTheDocument();
  });

  it('shows the accounts to somebody who may manage them', () => {
    stubApi(() => undefined);

    renderPage({ ...readerSession, permissions: ['accounts.manage'] });

    expect(screen.getByRole('link', { name: 'Accounts' })).toHaveAttribute('href', '/accounts');
  });
});
