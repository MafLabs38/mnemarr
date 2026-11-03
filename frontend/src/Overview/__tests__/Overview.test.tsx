import { screen, waitFor } from '@testing-library/react';

import { renderWithProviders, stubNetworkError, stubPending, stubStatusOk } from '__tests__/render';
import Overview from 'Overview/Overview';

describe('Overview', () => {
  it('says there is nothing to remember yet while the server has not answered', () => {
    stubPending();

    renderWithProviders(<Overview />);

    expect(screen.getByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument();
    expect(screen.getByText('Nothing to remember yet.')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Loading…');
  });

  it('shows that the API is reachable once the server answers', async () => {
    stubStatusOk();

    renderWithProviders(<Overview />);

    expect(await screen.findByText('API reachable')).toBeInTheDocument();
    expect(screen.queryByText('Loading…')).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('API reachable');
  });

  it('shows that Mnemarr is unreachable when the server does not answer', async () => {
    stubNetworkError();

    renderWithProviders(<Overview />);

    expect(await screen.findByText('Unreachable')).toBeInTheDocument();
  });

  it('shows only that Mnemarr is unreachable when a later refresh fails, not both states', async () => {
    stubStatusOk();
    const { client } = renderWithProviders(<Overview />);
    expect(await screen.findByText('API reachable')).toBeInTheDocument();

    stubNetworkError();
    await client.refetchQueries();

    await waitFor(() => expect(screen.getByText('Unreachable')).toBeInTheDocument());
    expect(screen.queryByText('API reachable')).not.toBeInTheDocument();
  });
});
