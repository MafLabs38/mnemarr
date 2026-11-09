import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { findCall, readerSession, renderWithProviders, stubApi } from '__tests__/render';
import type { ConsolidationStatus } from 'Sources/consolidation';
import ConsolidationCard from 'Sources/ConsolidationCard';

const status = (over: Partial<ConsolidationStatus> = {}): ConsolidationStatus => ({
  enabled: true,
  state: 'ready',
  model: 'haiku',
  memoryVault: 'memory',
  waiting: 3,
  done: 12,
  failing: 0,
  notes: 20,
  today: 4,
  perDay: 30,
  lastFailure: null,
  lastFailureAt: null,
  problem: null,
  ...over,
});

const admin = { ...readerSession, role: 'Admin', permissions: ['system.read', 'sources.manage'] };

function server(current: ConsolidationStatus) {
  return stubApi((method, path) => {
    if (method === 'GET' && path === '/consolidation') {
      return { body: current };
    }
    if (method === 'POST' && path === '/consolidation/run') {
      return { status: 202 };
    }
    return undefined;
  });
}

describe('ConsolidationCard', () => {
  it('says what waits, what is done and what it costs today, and offers to run it now', async () => {
    const fetchMock = server(status());
    renderWithProviders(<ConsolidationCard />, '/', admin);

    expect(
      await screen.findByText(
        /3 conversation\(s\) wait · 12 done · 20 note\(s\) written or changed · 4 of 30 runs today/,
      ),
    ).toBeInTheDocument();
    expect(screen.getByText(/A model \(haiku\) reads the new turns/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Consolidate what waits now' }));

    expect(findCall(fetchMock, ([url]) => url === '/api/v1/consolidation/run')).toBeDefined();
    expect(await screen.findByText(/It runs in the background/)).toBeInTheDocument();
  });

  it('says that it is off, and how to turn it on', async () => {
    server(status({ enabled: false, state: 'off' }));
    renderWithProviders(<ConsolidationCard />, '/', admin);

    expect(await screen.findByText(/Off\. The conversations stay as they are/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Consolidate what waits now' })).not.toBeInTheDocument();
  });

  it('says why it cannot run, and offers no button then', async () => {
    server(status({ state: 'memory-vault-read-only' }));
    renderWithProviders(<ConsolidationCard />, '/', admin);

    expect(await screen.findByText(/The memory vault is read-only/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Consolidate what waits now' })).not.toBeInTheDocument();
  });

  it('shows the last failure with its reason, and a runner that does not answer', async () => {
    server(
      status({
        lastFailure: 'The login of the subscription is not valid.',
        lastFailureAt: '2026-11-08T10:00:00Z',
        problem: 'The assistant runner does not answer.',
      }),
    );
    renderWithProviders(<ConsolidationCard />, '/', admin);

    expect(await screen.findByText(/The login of the subscription is not valid\./)).toBeInTheDocument();
    expect(screen.getByText('The assistant runner does not answer.')).toBeInTheDocument();
  });

  it('offers no run to a person who may only look', async () => {
    server(status());
    renderWithProviders(<ConsolidationCard />, '/', { ...readerSession, permissions: ['system.read'] });

    expect(await screen.findByText(/3 conversation\(s\) wait/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Consolidate what waits now' })).not.toBeInTheDocument();
  });

  it('offers to try again the conversations that failed, to the person who manages sources', async () => {
    const fetchMock = stubApi((method, path) => {
      if (method === 'GET' && path === '/consolidation') {
        return { body: status({ failing: 2, lastFailure: 'No login.', lastFailureAt: '2026-11-08T10:00:00Z' }) };
      }
      if (method === 'POST' && path === '/consolidation/retry') {
        return { body: { retried: 2 } };
      }
      return undefined;
    });
    renderWithProviders(<ConsolidationCard />, '/', admin);

    await userEvent.click(await screen.findByRole('button', { name: 'Try the 2 conversation(s) that failed again' }));

    expect(findCall(fetchMock, ([url]) => url === '/api/v1/consolidation/retry')).toBeDefined();
    expect(await screen.findByText('2 conversation(s) will be tried again at the next pass.')).toBeInTheDocument();
  });
});
