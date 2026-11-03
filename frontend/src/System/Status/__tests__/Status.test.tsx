import { act, screen, waitFor } from '@testing-library/react';

import {
  renderWithProviders,
  status,
  stubNetworkError,
  stubPending,
  stubStatusHttpError,
  stubStatusOk,
} from '__tests__/render';
import Status from 'System/Status/Status';
import { STATUS_REFRESH_MS } from 'System/Status/useSystemStatus';

describe('Status', () => {
  it('titles the page, names its section, and says it is loading', () => {
    stubPending();

    renderWithProviders(<Status />);

    expect(screen.getByRole('heading', { level: 1, name: 'Status' })).toBeInTheDocument();
    expect(screen.getByText('System')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Loading…');
  });

  it('shows the version, the runtime, a valid start time and the uptime once loaded', async () => {
    stubStatusOk();

    renderWithProviders(<Status />);

    expect(await screen.findByText('1.2.3')).toBeInTheDocument();
    expect(screen.getByText('.NET 10.0.0')).toBeInTheDocument();
    expect(screen.getByText('2h 10m')).toBeInTheDocument();
    expect(screen.getByText('Started')).toBeInTheDocument();
    expect(screen.queryByText('Invalid Date')).not.toBeInTheDocument();
  });

  it('reads /api/v1/system/status', async () => {
    const fetchMock = stubStatusOk();

    renderWithProviders(<Status />);
    await screen.findByText('1.2.3');

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/system/status',
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('shows an error when the server does not answer well', async () => {
    stubStatusHttpError(503);

    renderWithProviders(<Status />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Could not read the system status: The server answered 503.',
    );
  });

  it('shows an error when the network fails', async () => {
    stubNetworkError('Failed to fetch');

    renderWithProviders(<Status />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to fetch');
  });

  it('refreshes by itself, so the uptime keeps moving', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const fetchMock = stubStatusOk();
      renderWithProviders(<Status />);
      expect(await screen.findByText('2h 10m')).toBeInTheDocument();

      fetchMock.mockResolvedValue({ ok: true, json: () => Promise.resolve({ ...status, uptimeSeconds: 7860 }) });
      await act(() => vi.advanceTimersByTimeAsync(STATUS_REFRESH_MS));

      expect(await screen.findByText('2h 11m')).toBeInTheDocument();
      expect(fetchMock).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows the error and not the data of the last good answer when a refresh fails', async () => {
    stubStatusOk();
    const { client } = renderWithProviders(<Status />);
    expect(await screen.findByText('1.2.3')).toBeInTheDocument();

    stubNetworkError('Failed to fetch');
    await client.refetchQueries();

    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to fetch');
    await waitFor(() => expect(screen.queryByText('1.2.3')).not.toBeInTheDocument());
  });
});
