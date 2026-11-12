import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { findCall, readerSession, renderWithProviders, rowOf, stubApi } from '__tests__/render';
import Machines from 'Machines/Machines';
import type { MachineItem, NotificationStatus, NotificationTest } from 'Machines/machines';

const asking = (over: Partial<NonNullable<MachineItem['attention']>>): MachineItem => ({
  machine: 'gemini',
  kind: 'always-on',
  state: 'failing',
  conversations: 1,
  lines: 4,
  lastSeen: '2026-11-12T08:00:00Z',
  enrolledAt: '2026-11-01T08:00:00Z',
  version: '0.3.0',
  pendingLines: 0,
  error: null,
  programDiffers: false,
  updateTo: null,
  updateNote: null,
  watching: null,
  attention: {
    code: 'needs-login',
    sentence: 'needs you to sign in again.',
    url: 'https://satellite.homelab.example/connection',
    since: '2026-11-12T08:00:00Z',
    mails: 0,
    mailProblem: null,
    ...over,
  },
  consolidate: null,
  consolidating: false,
});

const on: NotificationStatus = {
  enabled: true,
  provider: 'Smtp',
  recipient: 'me@homelab.example',
  lastAt: '2026-11-12T09:00:00Z',
  lastDelivered: true,
  lastProblem: null,
};

function server(machines: MachineItem[], notifications: NotificationStatus, test?: NotificationTest) {
  return stubApi((method, path) => {
    if (method === 'GET' && path === '/collectors') {
      return { body: machines };
    }
    if (method === 'GET' && path === '/notifications') {
      return { body: notifications };
    }
    if (method === 'POST' && path === '/notifications/test') {
      return { body: test ?? { delivered: true, problem: null } };
    }
    if (method === 'GET') {
      return { body: [] };
    }
    return undefined;
  });
}

describe('Notifications on the machines page', () => {
  it('says how many mails were sent for a machine that asks, and why the last one did not leave', async () => {
    server(
      [
        asking({ mails: 2 }),
        { ...asking({ mails: 0, mailProblem: 'The mail server could not be reached.' }), machine: 'other' },
      ],
      on,
    );

    renderWithProviders(<Machines />);

    const row = rowOf(await screen.findByRole('rowheader', { name: 'gemini' }));
    expect(within(row).getByText('A mail was sent to you (2).')).toBeInTheDocument();
    const other = rowOf(screen.getByRole('rowheader', { name: 'other' }));
    expect(
      within(other).getByText('The mail could not be sent: The mail server could not be reached.'),
    ).toBeInTheDocument();
  });

  it('says that nothing is written to anyone when the notifications are off, and offers no test', async () => {
    server([], {
      enabled: false,
      provider: null,
      recipient: null,
      lastAt: null,
      lastDelivered: null,
      lastProblem: null,
    });

    renderWithProviders(<Machines />);

    expect(await screen.findByText(/Mnemarr does not write to anyone/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send a test mail' })).not.toBeInTheDocument();
  });

  it('sends a test mail and says it left', async () => {
    const fetchMock = server([], on);
    renderWithProviders(<Machines />);

    expect(
      await screen.findByText('When a machine needs you, Mnemarr writes to me@homelab.example (Smtp).'),
    ).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Send a test mail' }));

    expect(await screen.findByText('The test mail left. Look in me@homelab.example.')).toBeInTheDocument();
    expect(findCall(fetchMock, (call) => String(call[0]).endsWith('/notifications/test'))).toBeDefined();
  });

  it('says why a test mail did not leave', async () => {
    server([], on, { delivered: false, problem: 'The mail server could not be reached.' });
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Send a test mail' }));

    expect(
      await screen.findByText('The test mail did not leave: The mail server could not be reached.'),
    ).toBeInTheDocument();
  });

  it('shows the last failure of a mail without a test', async () => {
    server([], { ...on, lastDelivered: false, lastProblem: 'The mail server could not be reached.' });

    renderWithProviders(<Machines />);

    expect(
      await screen.findByText('The last mail could not be sent: The mail server could not be reached.'),
    ).toBeInTheDocument();
  });

  it('does not show the notifications to somebody who cannot manage the machines, and does not ask for them', async () => {
    const fetchMock = server([], on);

    renderWithProviders(<Machines />, '/', readerSession);

    await screen.findByRole('heading', { level: 1, name: 'Machines' });
    expect(screen.queryByText('Notifications')).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some((call) => String(call[0]).endsWith('/notifications'))).toBe(false);
  });
});
