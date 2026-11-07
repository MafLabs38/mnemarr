import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { findCall, readerSession, renderWithProviders, rowOf, sentBody, stubApi } from '__tests__/render';
import Machines from 'Machines/Machines';
import type { EnrolmentItem, MachineItem } from 'Machines/machines';
import type { Session } from 'Session/session';

const machine = (over: Partial<MachineItem>): MachineItem => ({
  machine: 'laptop',
  kind: 'intermittent',
  state: 'online',
  conversations: 3,
  lines: 120,
  lastSeen: '2026-11-05T10:00:00Z',
  enrolledAt: '2026-11-01T09:00:00Z',
  version: '0.1.0',
  pendingLines: 0,
  error: null,
  programDiffers: false,
  updateTo: null,
  updateNote: null,
  ...over,
});

const request = (over: Partial<EnrolmentItem>): EnrolmentItem => ({
  id: 'e1',
  machine: 'new-pc',
  kind: 'intermittent',
  state: 'pending',
  requestedAt: '2026-11-05T09:00:00Z',
  requestedBy: 'assistant-key',
  approvedBy: null,
  expiresAt: '2026-11-06T09:00:00Z',
  ...over,
});

function server(machines: MachineItem[], requests: EnrolmentItem[] = []) {
  return stubApi((method, path) => {
    if (method === 'GET' && path === '/collectors') {
      return { body: machines };
    }
    if (method === 'GET' && path === '/enrolments') {
      return { body: requests };
    }
    if (method === 'POST' && path === '/enrolments') {
      return {
        status: 201,
        body: {
          enrolment: request({ machine: 'server-1', kind: 'always-on', state: 'approved' }),
          code: 'ABCD-EFGH-JKLM',
          command:
            'mnemarr-collector enroll --server http://mnemarr.example --code ABCD-EFGH-JKLM --machine server-1 --allow-insecure-http',
          installs: [
            {
              system: 'windows',
              label: 'Windows',
              command:
                "irm 'http://mnemarr.example/api/v1/ingest/install.ps1?code=ABCD-EFGH-JKLM&machine=server-1' | iex",
              downloadUrl:
                'http://mnemarr.example/api/v1/ingest/download/windows-x86_64?code=ABCD-EFGH-JKLM&machine=server-1',
              setupUrl: 'http://mnemarr.example/api/v1/ingest/installer/windows',
            },
            {
              system: 'unix',
              label: 'Linux or macOS (terminal)',
              command:
                "curl -fsSL 'http://mnemarr.example/api/v1/ingest/install.sh?code=ABCD-EFGH-JKLM&machine=server-1' | sh",
              downloadUrl: null,
              setupUrl: null,
            },
          ],
          insecure: true,
        },
      };
    }
    return { status: 204 };
  });
}

const assistantWhoReads: Session = {
  accountId: 'a2',
  name: 'chat-runner',
  kind: 'Service',
  role: 'Assistant',
  permissions: ['conversations.read', 'notes.read'],
};

describe('Machines', () => {
  afterEach(() => {
    Reflect.deleteProperty(navigator, 'clipboard');
    Reflect.deleteProperty(document, 'execCommand');
  });

  it('tells each machine by its state in words and an icon, and never a state by colour alone', async () => {
    server([
      machine({ machine: 'online-pc', state: 'online' }),
      machine({ machine: 'sleeper', state: 'away' }),
      machine({ machine: 'resting', state: 'paused' }),
      machine({ machine: 'server-1', kind: 'always-on', state: 'silent' }),
      machine({ machine: 'broken', state: 'failing', error: '2 files could not be read' }),
      machine({ machine: 'gone', state: 'refused' }),
      machine({ machine: 'fresh', state: 'waiting', lastSeen: null }),
      machine({ machine: 'old-name', kind: null, state: 'unenrolled' }),
    ]);

    renderWithProviders(<Machines />);

    expect(await screen.findByRole('heading', { level: 1, name: 'Machines' })).toBeInTheDocument();
    await screen.findByRole('rowheader', { name: 'online-pc' });
    const text = (name: string, label: string) =>
      expect(within(rowOf(screen.getByRole('rowheader', { name }))).getByText(label)).toBeInTheDocument();
    text('online-pc', 'Online');
    text('sleeper', 'Away (asleep or off)');
    text('resting', 'Paused by its user');
    text('server-1', 'Silent');
    text('broken', 'Failing');
    text('gone', 'Refused (revoked)');
    text('fresh', 'Waiting for its first contact');
    text('old-name', 'No longer enrolled (its conversations are kept)');
    expect(
      within(rowOf(screen.getByRole('rowheader', { name: 'broken' }))).getByText('2 files could not be read'),
    ).toBeInTheDocument();
    expect(within(rowOf(screen.getByRole('rowheader', { name: 'fresh' }))).getByText('Never')).toBeInTheDocument();
    expect(
      within(rowOf(screen.getByRole('rowheader', { name: 'server-1' }))).getByText('Always on (a server)'),
    ).toBeInTheDocument();
  });

  it('says there is no machine yet, and how to add one', async () => {
    server([]);

    renderWithProviders(<Machines />);

    expect(await screen.findByText(/No machine has joined yet/)).toBeInTheDocument();
  });

  it('adds a machine and shows one line that installs the collector and joins it, once, with the code and a warning when the address is not HTTPS', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const fetchMock = server([]);
    renderWithProviders(<Machines />);

    await userEvent.type(await screen.findByLabelText('Name of the machine'), 'server-1');
    await userEvent.selectOptions(screen.getByLabelText('Kind'), 'always-on');
    await userEvent.click(screen.getByRole('button', { name: 'Add the machine' }));

    const notice = await screen.findByRole('region', { name: 'Code of the new machine' });
    expect(notice).toHaveTextContent('shown only now');
    expect(notice).toHaveTextContent('Code: ABCD-EFGH-JKLM');
    expect(within(notice).getByText(/install\.sh\?code=ABCD-EFGH-JKLM&machine=server-1/)).toBeInTheDocument();
    expect(within(notice).queryByRole('link', { name: /Download for/ })).not.toBeInTheDocument();
    expect(within(notice).getByRole('alert')).toHaveTextContent('plain HTTP');
    const call = findCall(
      fetchMock,
      ([url, init]) => url === '/api/v1/enrolments' && (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(sentBody(call)).toEqual({ machine: 'server-1', kind: 'always-on' });
    await userEvent.selectOptions(within(notice).getByLabelText('System'), 'windows');
    expect(within(notice).getByRole('link', { name: 'Download the installer for Windows' })).toHaveAttribute(
      'href',
      'http://mnemarr.example/api/v1/ingest/installer/windows',
    );
    const download = within(notice).getByRole('link', { name: 'Download for Windows' });
    expect(download).toHaveAttribute(
      'href',
      expect.stringContaining('/download/windows-x86_64?code=ABCD-EFGH-JKLM&machine=server-1'),
    );
    expect(download).toHaveAttribute('download');
    expect(notice).toHaveTextContent('Or, from a terminal, paste this line');
    expect(notice).toHaveTextContent('publisher is unknown');
    expect(within(notice).getByText(/^irm 'http:\/\/mnemarr\.example/)).toBeInTheDocument();
    await userEvent.click(within(notice).getByRole('button', { name: 'Copy the command' }));
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('install.ps1?code=ABCD-EFGH-JKLM'));
    await userEvent.click(within(notice).getByRole('button', { name: 'Done' }));
    expect(screen.queryByText('Code: ABCD-EFGH-JKLM')).not.toBeInTheDocument();
  });

  it('still copies the line when the page is served over plain HTTP and has no clipboard, and tells the person when nothing can copy', async () => {
    document.execCommand = vi.fn(() => true);
    server([]);
    renderWithProviders(<Machines />);
    await userEvent.type(await screen.findByLabelText('Name of the machine'), 'server-1');
    await userEvent.click(screen.getByRole('button', { name: 'Add the machine' }));
    const notice = await screen.findByRole('region', { name: 'Code of the new machine' });

    await userEvent.click(within(notice).getByRole('button', { name: 'Copy the command' }));

    expect(await within(notice).findByRole('button', { name: 'Copied' })).toBeInTheDocument();
    expect(document.execCommand).toHaveBeenCalledWith('copy');

    document.execCommand = vi.fn(() => false);
    await userEvent.click(within(notice).getByRole('button', { name: 'Copied' }));
    expect(await within(notice).findByText(/will not copy from this page/)).toBeInTheDocument();
  });

  it('says so, and shows the command that only joins, when this Mnemarr has no program to give', async () => {
    stubApi((method) => {
      if (method === 'GET') {
        return { body: [] };
      }
      return {
        status: 201,
        body: {
          enrolment: request({ machine: 'box', state: 'approved' }),
          code: 'ABCD-EFGH-JKLM',
          command: 'mnemarr-collector enroll --server https://m.example --code ABCD-EFGH-JKLM --machine box',
          installs: [],
          insecure: false,
        },
      };
    });
    renderWithProviders(<Machines />);

    await userEvent.type(await screen.findByLabelText('Name of the machine'), 'box');
    await userEvent.click(screen.getByRole('button', { name: 'Add the machine' }));

    const notice = await screen.findByRole('region', { name: 'Code of the new machine' });
    expect(notice).toHaveTextContent('no collector program to give');
    expect(within(notice).getByText(/^mnemarr-collector enroll --server https/)).toBeInTheDocument();
    expect(within(notice).queryByRole('alert')).not.toBeInTheDocument();
    expect(within(notice).queryByLabelText('System')).not.toBeInTheDocument();
  });

  it('lets a person approve or reject a request that an assistant made, and cancel a code that is approved', async () => {
    const fetchMock = server(
      [],
      [request({ id: 'e1' }), request({ id: 'e2', machine: 'ready-pc', state: 'approved', approvedBy: 'admin' })],
    );
    renderWithProviders(<Machines />);

    const waiting = rowOf(await screen.findByRole('rowheader', { name: 'new-pc' }));
    expect(within(waiting).getByText('Waiting for approval')).toBeInTheDocument();
    await userEvent.click(within(waiting).getByRole('button', { name: 'Approve the request for new-pc' }));
    await userEvent.click(
      within(rowOf(screen.getByRole('rowheader', { name: 'ready-pc' }))).getByRole('button', {
        name: 'Reject the request for ready-pc',
      }),
    );

    expect(findCall(fetchMock, ([url]) => url === '/api/v1/enrolments/e1/approve')).toBeDefined();
    expect(findCall(fetchMock, ([url]) => url === '/api/v1/enrolments/e2/reject')).toBeDefined();
    expect(
      within(rowOf(screen.getByRole('rowheader', { name: 'ready-pc' }))).queryByRole('button', { name: /Approve/ }),
    ).not.toBeInTheDocument();
  });

  it('revokes a machine that joined, but offers it neither for a refused machine nor for a name that never joined', async () => {
    const fetchMock = server([
      machine({ machine: 'laptop' }),
      machine({ machine: 'gone', state: 'refused' }),
      machine({ machine: 'old-name', kind: null, state: 'unenrolled' }),
    ]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Revoke the machine laptop' }));

    expect(
      findCall(
        fetchMock,
        ([url, init]) => url === '/api/v1/collectors/laptop' && (init as RequestInit | undefined)?.method === 'DELETE',
      ),
    ).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Revoke the machine gone' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revoke the machine old-name' })).not.toBeInTheDocument();
  });

  it('takes a revoked machine out of the list on request, and offers that for no other', async () => {
    const fetchMock = server([
      machine({ machine: 'laptop' }),
      machine({ machine: 'gone', state: 'refused' }),
      machine({ machine: 'old-name', kind: null, state: 'unenrolled' }),
    ]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Remove the machine gone from the list' }));

    expect(
      findCall(
        fetchMock,
        ([url, init]) =>
          url === '/api/v1/collectors/gone/forget' && (init as RequestInit | undefined)?.method === 'POST',
      ),
    ).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Remove the machine laptop from the list' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove the machine old-name from the list' })).not.toBeInTheDocument();
  });

  it('turns a name typed as a person would into a valid one, says so, and sends it', async () => {
    const fetchMock = server([]);
    renderWithProviders(<Machines />);

    await userEvent.type(await screen.findByLabelText('Name of the machine'), 'Home Office');

    expect(screen.getByText('It will be named: home-office')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Add the machine' }));
    const call = findCall(
      fetchMock,
      ([url, init]) => url === '/api/v1/enrolments' && (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(sentBody(call)).toEqual({ machine: 'home-office', kind: 'intermittent' });
  });

  it('does not offer to add a machine until there is a name, and says nothing when the name is already valid', async () => {
    server([]);
    renderWithProviders(<Machines />);

    expect(await screen.findByRole('button', { name: 'Add the machine' })).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Name of the machine'), 'server-1');
    expect(screen.queryByText(/It will be named/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add the machine' })).toBeEnabled();
  });

  it('shows the machines to a reader of conversations and nothing to add, approve or revoke', async () => {
    const fetchMock = server([machine({ machine: 'laptop' })]);
    renderWithProviders(<Machines />, '/', assistantWhoReads);

    expect(await screen.findByRole('rowheader', { name: 'laptop' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Revoke/ })).not.toBeInTheDocument();
    expect(screen.queryByText('Add a machine')).not.toBeInTheDocument();
    expect(screen.queryByText('Requests to add a machine')).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url]) => url === '/api/v1/enrolments')).toBe(false);
  });

  it('says so when the machines cannot be read', async () => {
    stubApi(() => ({ status: 500 }));
    renderWithProviders(<Machines />, '/', readerSession);

    expect(await screen.findByText('The machines could not be read.')).toBeInTheDocument();
  });
});
