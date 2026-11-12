import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { findCall, readerSession, renderWithProviders, sentBody, stubApi } from '__tests__/render';
import Machines from 'Machines/Machines';
import type { MachineItem } from 'Machines/machines';

const machine = (over: Partial<MachineItem>): MachineItem => ({
  machine: 'laptop',
  kind: 'intermittent',
  state: 'online',
  conversations: 13,
  lines: 120,
  lastSeen: '2026-11-05T10:00:00Z',
  enrolledAt: '2026-11-01T09:00:00Z',
  version: '0.1.0',
  pendingLines: 0,
  error: null,
  programDiffers: false,
  updateTo: null,
  updateNote: null,
  watching: null,
  attention: null,
  consolidate: null,
  consolidating: true,
  ...over,
});

function server(machines: MachineItem[]) {
  return stubApi((method, path) => {
    if (method === 'GET' && path === '/collectors') {
      return { body: machines };
    }
    if (method === 'GET' && (path === '/enrolments' || path === '/pairings')) {
      return { body: [] };
    }
    if (method === 'POST' && path === '/enrolments') {
      return {
        status: 201,
        body: {
          enrolment: {
            id: 'e1',
            machine: 'laptop',
            kind: 'intermittent',
            state: 'approved',
            requestedAt: '2026-11-07T09:00:00Z',
            requestedBy: 'admin',
            approvedBy: 'admin',
            expiresAt: '2026-11-07T09:15:00Z',
          },
          code: 'ABCD-EFGH-JKLM',
          command: 'mnemarr-collector enroll --server http://mnemarr.example --code ABCD-EFGH-JKLM --machine laptop',
          installs: [],
          insecure: false,
        },
      };
    }
    if (method === 'POST' && path.endsWith('/merge')) {
      return { body: { moved: 2, alreadyThere: 1 } };
    }
    if (method === 'POST' && path.endsWith('/erase')) {
      return { body: { conversations: 13 } };
    }
    return { status: 204 };
  });
}

const isPost = (path: string) => (call: unknown[]) =>
  call[0] === path && (call[1] as RequestInit | undefined)?.method === 'POST';

const called = (fetchMock: ReturnType<typeof server>, path: string) => findCall(fetchMock, isPost(path));

const wasCalled = (fetchMock: ReturnType<typeof server>, path: string) => fetchMock.mock.calls.some(isPost(path));

const panelOf = (name: string) => within(screen.getByRole('region', { name: `Manage ${name}` }));

describe('Managing a machine', () => {
  it('renames a machine, and says the name it will have', async () => {
    const fetchMock = server([machine({ machine: 'gamng-pc' })]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Manage gamng-pc' }));
    const name = panelOf('gamng-pc').getByLabelText('Name of the machine');
    await userEvent.clear(name);
    await userEvent.type(name, 'Gaming PC');
    expect(screen.getByText('It will be named: gaming-pc')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Rename' }));

    expect(sentBody(called(fetchMock, '/api/v1/collectors/gamng-pc/rename'))).toEqual({ name: 'gaming-pc' });
  });

  it('turns the notes of the memory on or off for a machine, or puts it back to the default of its format', async () => {
    const fetchMock = server([machine({ machine: 'web', consolidating: false })]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Manage web' }));
    const select = panelOf('web').getByLabelText('Consolidate its conversations');
    expect(select).toHaveValue('default');
    expect(panelOf('web').getByRole('option', { name: 'By default (no)' })).toBeInTheDocument();
    await userEvent.selectOptions(select, 'on');

    expect(sentBody(called(fetchMock, '/api/v1/collectors/web/consolidate'))).toEqual({ consolidate: true });
  });

  it('shows what was said, and sends null to go back to the default', async () => {
    const fetchMock = server([machine({ machine: 'cli', consolidate: false, consolidating: false })]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Manage cli' }));
    const select = panelOf('cli').getByLabelText('Consolidate its conversations');
    expect(select).toHaveValue('off');
    await userEvent.selectOptions(select, 'default');

    expect(sentBody(called(fetchMock, '/api/v1/collectors/cli/consolidate'))).toEqual({ consolidate: null });
  });

  it('offers no setting of consolidation for a name that never joined', async () => {
    server([machine({ machine: 'old-name', kind: null, state: 'unenrolled' })]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Manage old-name' }));

    expect(panelOf('old-name').queryByLabelText('Consolidate its conversations')).not.toBeInTheDocument();
  });

  it('merges what a name sent into a machine that is enrolled, and offers no machine that is revoked or that is itself', async () => {
    const fetchMock = server([
      machine({ machine: 'old-name', kind: null, state: 'unenrolled' }),
      machine({ machine: 'laptop' }),
      machine({ machine: 'gone', state: 'refused' }),
    ]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Manage old-name' }));
    const choices = panelOf('old-name')
      .getAllByRole('option')
      .map((option) => option.textContent);
    expect(choices).toContain('laptop');
    expect(choices).not.toContain('gone');
    expect(choices).not.toContain('old-name');
    await userEvent.selectOptions(screen.getByLabelText('Merge into'), 'laptop');
    await userEvent.click(panelOf('old-name').getByRole('button', { name: /^Merge$/ }));

    expect(sentBody(called(fetchMock, '/api/v1/collectors/old-name/merge'))).toEqual({ into: 'laptop' });
  });

  it('asks before deleting a machine with its conversations, says what it deletes and what it does not, and deletes only when told to', async () => {
    const fetchMock = server([machine({ machine: 'laptop' })]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Manage laptop' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete the machine laptop with its conversations' }));

    expect(screen.getByText(/Delete laptop and its 13 conversations\? This cannot be undone/)).toBeInTheDocument();
    expect(screen.getByText(/what is computed from conversations is not deleted/)).toBeInTheDocument();
    expect(wasCalled(fetchMock, '/api/v1/collectors/laptop/erase')).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByText(/This cannot be undone/)).not.toBeInTheDocument();
    expect(wasCalled(fetchMock, '/api/v1/collectors/laptop/erase')).toBe(false);

    await userEvent.click(screen.getByRole('button', { name: 'Delete the machine laptop with its conversations' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete laptop and its conversations now' }));

    expect(called(fetchMock, '/api/v1/collectors/laptop/erase')).toBeDefined();
  });

  it('removes a machine while keeping its conversations, and offers that only for a machine that is enrolled', async () => {
    const fetchMock = server([
      machine({ machine: 'laptop' }),
      machine({ machine: 'old-name', kind: null, state: 'unenrolled' }),
    ]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Manage old-name' }));
    expect(screen.queryByRole('button', { name: /Remove the machine old-name and keep/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Manage laptop' }));
    await userEvent.click(screen.getByRole('button', { name: 'Remove the machine laptop and keep its conversations' }));

    expect(called(fetchMock, '/api/v1/collectors/laptop/remove')).toBeDefined();
  });

  it('gives a new code for a machine that exists, without revoking it, and offers it for no machine that is refused or never joined', async () => {
    const fetchMock = server([
      machine({ machine: 'laptop' }),
      machine({ machine: 'gone', state: 'refused' }),
      machine({ machine: 'old-name', kind: null, state: 'unenrolled' }),
    ]);
    renderWithProviders(<Machines />);

    await userEvent.click(await screen.findByRole('button', { name: 'Manage laptop' }));
    await userEvent.click(screen.getByRole('button', { name: 'Reinstall' }));

    expect(sentBody(called(fetchMock, '/api/v1/enrolments'))).toEqual({ machine: 'laptop', kind: 'intermittent' });
    expect((await screen.findAllByText(/ABCD-EFGH-JKLM/)).length).toBeGreaterThan(0);
    await userEvent.click(screen.getByRole('button', { name: 'Manage gone' }));
    expect(panelOf('gone').queryByRole('button', { name: 'Reinstall' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Manage old-name' }));
    expect(panelOf('old-name').queryByRole('button', { name: 'Reinstall' })).not.toBeInTheDocument();
  });

  it('shows no way to manage a machine to a reader', async () => {
    server([machine({})]);
    renderWithProviders(<Machines />, '/', readerSession);

    expect(await screen.findByText('laptop')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Manage/ })).not.toBeInTheDocument();
  });

  it('says when this Mnemarr carries another program than the one a machine runs, and says nothing for a machine that is up to date', async () => {
    server([machine({ machine: 'laptop', programDiffers: true }), machine({ machine: 'server-1' })]);
    renderWithProviders(<Machines />);

    expect(await screen.findByText('Another program than this Mnemarr carries')).toBeInTheDocument();
    expect(screen.getAllByText('Another program than this Mnemarr carries')).toHaveLength(1);
  });

  it('says which version a machine installed with the installer can update to, and that instead of the program that differs', async () => {
    server([
      machine({ machine: 'laptop', updateTo: '1.4.0', programDiffers: true }),
      machine({ machine: 'by-hand', programDiffers: true }),
    ]);
    renderWithProviders(<Machines />);

    expect(await screen.findByText('Update available: 1.4.0')).toBeInTheDocument();
    expect(screen.getAllByText('Another program than this Mnemarr carries')).toHaveLength(1);
  });

  it('says why a collector that was installed with the installer cannot update', async () => {
    server([
      machine({
        machine: 'laptop',
        updateTo: '1.4.0',
        updateNote: 'none of the Mnemarr it sends to is reached over HTTPS',
      }),
    ]);
    renderWithProviders(<Machines />);

    expect(await screen.findByText('Update available: 1.4.0')).toBeInTheDocument();
    expect(
      screen.getByText('It cannot update: none of the Mnemarr it sends to is reached over HTTPS'),
    ).toBeInTheDocument();
  });
});
