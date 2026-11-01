import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';

import type { SystemStatus } from 'System/Status/useSystemStatus';

/** Renders a component with what the application gives it: a query client (no retry, so errors show at once) and a router. */
export function renderWithProviders(ui: ReactElement, route = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const result = render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
    </QueryClientProvider>,
  );
  return { ...result, client };
}

export const status: SystemStatus = {
  version: '1.2.3',
  runtimeVersion: '.NET 10.0.0',
  startedAt: '2026-11-01T08:00:00Z',
  uptimeSeconds: 7800,
};

/** The status endpoint answers with this status. Returns the mock, to assert on its calls. */
export function stubStatusOk(body: SystemStatus = status) {
  const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(body) });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

/** The status endpoint answers with an HTTP error. */
export function stubStatusHttpError(code: number) {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: code }));
}

/** The request never reaches the server. */
export function stubNetworkError(message = 'Failed to fetch') {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error(message)));
}

/** The request never completes: the loading state stays. */
export function stubPending() {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => new Promise(() => {})),
  );
}
