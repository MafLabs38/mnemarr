import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';

import { adminSession, stubApi } from '__tests__/render';
import { createQueryClient } from 'App/queryClient';
import { ApiError } from 'Helpers/Api/apiGet';
import { sessionKey } from 'Session/session';
import { useSession } from 'Session/SessionContext';
import SessionGate from 'Session/SessionGate';

function Inside() {
  return <p>inside as {useSession().name}</p>;
}

function renderGate() {
  const client = createQueryClient();
  render(
    <QueryClientProvider client={client}>
      <SessionGate>
        <Inside />
      </SessionGate>
    </QueryClientProvider>,
  );
  return client;
}

describe('SessionGate', () => {
  it('shows the application to somebody who is signed in, once it knows', async () => {
    stubApi(() => ({ body: adminSession }));

    renderGate();

    expect(screen.getByText('Loading…')).toBeInTheDocument();
    expect(await screen.findByText('inside as admin')).toBeInTheDocument();
  });

  it('shows the sign-in page, and nothing of the application, to nobody', async () => {
    stubApi(() => ({ status: 401 }));

    renderGate();

    expect(await screen.findByRole('heading', { name: 'Sign in to Mnemarr' })).toBeInTheDocument();
    expect(screen.queryByText(/inside as/)).not.toBeInTheDocument();
  });

  it('says so when it cannot tell who is signed in, and does not offer the sign-in page for that', async () => {
    stubApi(() => ({ status: 500 }));

    renderGate();

    // The query client asks once more after a failure, a second later.
    expect(await screen.findByRole('alert', {}, { timeout: 4000 })).toHaveTextContent(
      'Could not check who is signed in.',
    );
    expect(screen.queryByRole('heading', { name: 'Sign in to Mnemarr' })).not.toBeInTheDocument();
  });

  it('goes back to the sign-in page when any call is answered 401', async () => {
    stubApi(() => ({ body: adminSession }));
    const client = renderGate();
    await screen.findByText('inside as admin');

    await client
      .fetchQuery({ queryKey: ['api', '/anything'], queryFn: () => Promise.reject(new ApiError(401)) })
      .catch(() => undefined);

    expect(await screen.findByRole('heading', { name: 'Sign in to Mnemarr' })).toBeInTheDocument();
    expect(client.getQueryData(sessionKey)).toBeNull();
  });
});
