import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';

import type { Session } from 'Session/session';
import { SessionProvider } from 'Session/SessionContext';
import type { SystemStatus } from 'System/Status/useSystemStatus';

export const adminSession: Session = {
  accountId: 'a1',
  name: 'admin',
  kind: 'User',
  role: 'Admin',
  permissions: [
    'accounts.manage',
    'audit.read',
    'ingest.write',
    'notes.read',
    'notes.write',
    'search',
    'sources.manage',
    'system.read',
  ],
};

export const readerSession: Session = {
  accountId: 'r1',
  name: 'reader',
  kind: 'User',
  role: 'Reader',
  permissions: ['notes.read', 'search'],
};

/**
 * Renders a component with what the application gives it: a query client (no retry, so errors show at once), a router, and a
 * signed-in session (an administrator unless another is given).
 */
export function renderWithProviders(ui: ReactElement, route = '/', session: Session = adminSession) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const result = render(
    <QueryClientProvider client={client}>
      <SessionProvider session={session}>
        <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
      </SessionProvider>
    </QueryClientProvider>,
  );
  return { ...result, client };
}

/** What the fake server answers to a call: a status (200 by default) and a JSON body. */
export interface ApiAnswer {
  status?: number;
  body?: unknown;
}

/**
 * A fake API: `handler` answers by method and path (without `/api/v1`), and a call it does not know is a 404. Returns the mock, to
 * assert on the calls and what they carried.
 */
export function stubApi(handler: (method: string, path: string, init?: RequestInit) => ApiAnswer | undefined) {
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    const answer = handler(init?.method ?? 'GET', url.replace('/api/v1', ''), init) ?? { status: 404 };
    const code = answer.status ?? 200;
    return Promise.resolve({ ok: code < 400, status: code, json: () => Promise.resolve(answer.body) });
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

/** The body that a call sent, as an object. */
export function sentBody(call: unknown[]): unknown {
  return JSON.parse((call[1] as RequestInit).body as string);
}

export const status: SystemStatus = {
  version: '1.2.3',
  runtimeVersion: '.NET 10.0.0',
  startedAt: '2026-11-01T08:00:00Z',
  uptimeSeconds: 7800,
};

/** The status endpoint answers with this status, and an administrator is signed in. Returns the mock, to assert on its calls. */
export function stubStatusOk(body: SystemStatus = status) {
  return stubApi((_method, path) => (path === '/auth/session' ? { body: adminSession } : { body }));
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

/** The table row a cell or a row header belongs to. */
export function rowOf(element: HTMLElement): HTMLElement {
  const row = element.closest('tr');
  if (!row) {
    throw new Error('The element is not in a table row');
  }
  return row as HTMLElement;
}

/** The first call of the fake API that the predicate accepts: the test fails with a clear message when there is none. */
export function findCall(mock: { mock: { calls: unknown[][] } }, predicate: (call: unknown[]) => boolean): unknown[] {
  const call = mock.mock.calls.find(predicate);
  if (!call) {
    throw new Error('The API was not called as expected');
  }
  return call;
}
