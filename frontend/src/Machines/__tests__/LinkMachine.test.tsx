import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { type ApiAnswer, findCall, readerSession, renderWithProviders, sentBody, stubApi } from '__tests__/render';
import LinkMachine from 'Machines/LinkMachine';
import Machines from 'Machines/Machines';
import type { MachineItem } from 'Machines/machines';
import type { PairingItem } from 'Machines/pairings';

const pairing: PairingItem = {
  id: 'p1',
  hostname: 'Gaming PC',
  requestedAt: '2026-11-06T10:00:00Z',
  expiresAt: '2026-11-06T10:10:00Z',
  knownAs: null,
};

interface Options {
  found?: boolean;
  list?: PairingItem[];
  knownAs?: string;
  approveFails?: boolean;
  machines?: MachineItem[];
}

/** What the API answers to each call this page makes; any other call is a success with no body. */
function answers(options: Options): Record<string, ApiAnswer> {
  return {
    'POST /pairings/lookup':
      options.found === false
        ? { status: 404, body: { title: 'No such request' } }
        : { body: { ...pairing, knownAs: options.knownAs ?? null } },
    'GET /pairings': { body: options.list ?? [] },
    'GET /collectors': { body: options.machines ?? [] },
    'GET /enrolments': { body: [] },
    'POST /pairings/p1/approve': options.approveFails
      ? { status: 409, body: { title: 'This computer is already the machine other-pc.' } }
      : { body: pairing },
  };
}

function server(options: Options = {}) {
  const known = answers(options);
  return stubApi((method, path) => known[`${method} ${path}`] ?? { status: 204 });
}

describe('LinkMachine', () => {
  it('looks the request up by the code in the address, shows the name the computer gives itself and links it under the name a person chose', async () => {
    const fetchMock = server();
    renderWithProviders(<LinkMachine />, '/link?code=ABCD-EFGH-JKLM');

    expect(await screen.findByText('Gaming PC')).toBeInTheDocument();
    expect(sentBody(findCall(fetchMock, (call) => String(call[0]).endsWith('/pairings/lookup')))).toEqual({
      code: 'ABCD-EFGH-JKLM',
    });
    expect(screen.getByLabelText(/Code of this request/)).toHaveValue('ABCD-EFGH-JKLM');
    const name = screen.getByLabelText('Name in this Mnemarr');
    expect(name).toHaveValue('gaming-pc');
    await userEvent.clear(name);
    await userEvent.type(name, 'Gaming PC');
    expect(screen.getByText('It will be named: gaming-pc')).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText('Kind'), 'always-on');
    await userEvent.click(screen.getByRole('button', { name: 'Link this computer' }));

    expect(await screen.findByText(/Linked as gaming-pc/)).toBeInTheDocument();
    expect(sentBody(findCall(fetchMock, (call) => String(call[0]).endsWith('/pairings/p1/approve')))).toEqual({
      machine: 'gaming-pc',
      kind: 'always-on',
      code: 'ABCD-EFGH-JKLM',
      takeOver: '',
    });
    expect(screen.getByRole('link', { name: 'See the machines' })).toHaveAttribute('href', '/machines');
  });

  it('says when the computer is already a machine here, and starts from that name', async () => {
    server({ knownAs: 'gaming-pc' });
    renderWithProviders(<LinkMachine />, '/link?code=ABCD-EFGH-JKLM');

    expect(await screen.findByText(/says it is the machine gaming-pc/)).toBeInTheDocument();
    expect(screen.getByLabelText('Name in this Mnemarr')).toHaveValue('gaming-pc');
  });

  it('does not take over a machine that already joined until the person writes its name', async () => {
    server({ knownAs: 'gaming-pc' });
    renderWithProviders(<LinkMachine />, '/link?code=ABCD-EFGH-JKLM');

    const approve = await screen.findByRole('button', { name: 'Link this computer' });
    expect(approve).toBeDisabled();
    await userEvent.type(screen.getByLabelText('To take over the machine gaming-pc, write its name here'), 'gaming-pc');
    expect(approve).toBeEnabled();
  });

  it('says what the server said when the name cannot be given, and keeps the form so that the person can name it again', async () => {
    server({ approveFails: true });
    renderWithProviders(<LinkMachine />, '/link?code=ABCD-EFGH-JKLM');

    expect(await screen.findByLabelText(/Code of this request/)).toHaveValue('ABCD-EFGH-JKLM');
    await userEvent.click(screen.getByRole('button', { name: 'Link this computer' }));

    expect(await screen.findByText('This computer is already the machine other-pc.')).toBeInTheDocument();
    expect(screen.getByLabelText('Name in this Mnemarr')).toBeInTheDocument();
    expect(screen.queryByText(/Linked as/)).not.toBeInTheDocument();
  });

  it('asks for the code the computer shows when the person comes from the list, and approves only with it', async () => {
    const fetchMock = server({ list: [pairing] });
    renderWithProviders(<LinkMachine />, '/link?id=p1');

    const approve = await screen.findByRole('button', { name: 'Link this computer' });
    expect(approve).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/Code of this request/), 'abcd efgh jklm');
    expect(approve).toBeEnabled();
    await userEvent.click(approve);

    expect(await screen.findByText(/Linked as gaming-pc/)).toBeInTheDocument();
    expect(sentBody(findCall(fetchMock, (call) => String(call[0]).endsWith('/pairings/p1/approve')))).toMatchObject({
      code: 'abcd efgh jklm',
    });
  });

  it('says it linked or refused the computer even when the list no longer holds the request, which is what answering it does', async () => {
    // Once the request is answered the server no longer lists it: the page that was showing it is replaced, and what the person did is still told.
    let answered = false;
    stubApi((method, path) => {
      if (method === 'GET' && path === '/pairings') {
        return { body: answered ? [] : [pairing] };
      }
      if (method === 'POST' && (path === '/pairings/p1/approve' || path === '/pairings/p1/reject')) {
        answered = true;
        return { body: pairing };
      }
      return { body: [] };
    });
    renderWithProviders(<LinkMachine />, '/link?id=p1', readerSession);

    await userEvent.click(await screen.findByRole('button', { name: 'This is not my computer' }));

    expect(await screen.findByText(/Refused\./)).toBeInTheDocument();
  });

  it('warns when the name chosen is a machine that exists already, because approving replaces its key', async () => {
    const existing: MachineItem = {
      machine: 'gaming-pc',
      kind: 'intermittent',
      state: 'online',
      conversations: 3,
      lines: 10,
      lastSeen: '2026-11-06T09:00:00Z',
      enrolledAt: '2026-11-01T09:00:00Z',
      version: '0.0.0',
      pendingLines: 0,
      error: null,
      programDiffers: false,
      updateTo: null,
      updateNote: null,
    };
    server({ machines: [existing] });
    renderWithProviders(<LinkMachine />, '/link?code=ABCD-EFGH-JKLM');

    expect(await screen.findByText(/A machine named gaming-pc exists already/)).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText('Name in this Mnemarr'));
    await userEvent.type(screen.getByLabelText('Name in this Mnemarr'), 'another-pc');
    expect(screen.queryByText(/A machine named .* exists already/)).not.toBeInTheDocument();
  });

  it('refuses the computer when the person says it is not theirs, and says so', async () => {
    const fetchMock = server();
    renderWithProviders(<LinkMachine />, '/link?code=ABCD-EFGH-JKLM');

    await userEvent.click(await screen.findByRole('button', { name: 'This is not my computer' }));

    expect(await screen.findByText(/Refused/)).toBeInTheDocument();
    expect(findCall(fetchMock, (call) => String(call[0]).endsWith('/pairings/p1/reject'))).toBeDefined();
  });

  it('says there is no such request waiting, and how to start again, when the code is over or wrong', async () => {
    server({ found: false });

    renderWithProviders(<LinkMachine />, '/link?code=ZZZZ-ZZZZ-ZZZZ');

    expect(await screen.findByText(/There is no request waiting with this code/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Link this computer' })).not.toBeInTheDocument();
  });

  it('asks for the code when it comes with none, and looks it up once it is typed', async () => {
    const fetchMock = server();
    renderWithProviders(<LinkMachine />, '/link');

    await userEvent.type(screen.getByLabelText('Code shown on the computer'), 'abcd-efgh-jklm');
    await userEvent.click(screen.getByRole('button', { name: 'Find the request' }));

    expect(await screen.findByText('Gaming PC')).toBeInTheDocument();
    expect(sentBody(findCall(fetchMock, (call) => String(call[0]).endsWith('/pairings/lookup')))).toEqual({
      code: 'abcd-efgh-jklm',
    });
  });

  it('opens the request that a person chose in the list', async () => {
    server({ list: [pairing] });

    renderWithProviders(<LinkMachine />, '/link?id=p1');

    expect(await screen.findByText('Gaming PC')).toBeInTheDocument();
  });

  it('is closed to those who do not manage the collectors, as the whole page of the machines is for the requests', async () => {
    server();

    renderWithProviders(<Machines />, '/machines', readerSession);

    await screen.findByRole('heading', { level: 1, name: 'Machines' });
    expect(screen.queryByText('Computers asking to be linked')).not.toBeInTheDocument();
  });
});

describe('Machines: computers asking to be linked', () => {
  it('lists the computers that wait for a person, with a way to review each, and says when none does', async () => {
    server({ list: [pairing] });
    renderWithProviders(<Machines />, '/machines');

    const link = await screen.findByRole('link', { name: 'Review the request of Gaming PC' });
    expect(link).toHaveAttribute('href', '/link?id=p1');
    expect(screen.getByText('Computers asking to be linked')).toBeInTheDocument();
  });

  it('says how to ask from a computer when none is asking', async () => {
    server();
    renderWithProviders(<Machines />, '/machines');

    expect(await screen.findByText(/No computer is asking/)).toBeInTheDocument();
  });
});
