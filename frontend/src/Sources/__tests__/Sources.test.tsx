import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { type ApiAnswer, findCall, renderWithProviders, rowOf, sentBody, stubApi } from '__tests__/render';
import Sources from 'Sources/Sources';
import type { SourceItem, SourceKindItem } from 'Sources/sources';

const kindsWith = (counts: { Memory: number; Knowledge: number; ApplicationDocs: number }): SourceKindItem[] => [
  {
    kind: 'Memory',
    required: true,
    unique: true,
    count: counts.Memory,
    satisfied: counts.Memory >= 1,
    defaultMode: 'ReadOnly',
  },
  {
    kind: 'Knowledge',
    required: false,
    unique: false,
    count: counts.Knowledge,
    satisfied: true,
    defaultMode: 'ReadOnly',
  },
  {
    kind: 'ApplicationDocs',
    required: false,
    unique: false,
    count: counts.ApplicationDocs,
    satisfied: true,
    defaultMode: 'ReadOnly',
  },
];

const sources: SourceItem[] = [
  {
    id: 's1',
    name: 'cours',
    kind: 'Knowledge',
    mode: 'ReadWrite',
    path: '/config/sources/cours',
    createdAt: '2026-11-03T09:00:00Z',
    notes: 12,
    chunks: 40,
    lastIndexedAt: '2026-11-03T10:30:00Z',
    lastScanAt: '2026-11-03T10:30:00Z',
    lastError: null,
    managed: false,
  },
  {
    id: 's2',
    name: 'memoire',
    kind: 'Memory',
    mode: 'ReadOnly',
    path: '/config/sources/memoire',
    createdAt: '2026-11-03T09:00:00Z',
    notes: 0,
    chunks: 0,
    lastIndexedAt: null,
    lastScanAt: null,
    lastError: null,
    managed: false,
  },
];

function server(
  extra: (method: string, path: string) => ApiAnswer | undefined = () => undefined,
  list: SourceItem[] = sources,
) {
  return stubApi((method, path) => {
    const special = extra(method, path);
    if (special) {
      return special;
    }
    if (method === 'GET' && path === '/sources') {
      return { body: list };
    }
    if (method === 'GET' && path === '/sources/kinds') {
      const count = (kind: SourceItem['kind']) => list.filter((source) => source.kind === kind).length;
      return {
        body: kindsWith({
          Memory: count('Memory'),
          Knowledge: count('Knowledge'),
          ApplicationDocs: count('ApplicationDocs'),
        }),
      };
    }
    if (method === 'GET' && path === '/config') {
      return { body: 'memory:\n  name: memoire\n  path: /config/sources/memoire\n' };
    }
    if (method === 'GET' && path === '/config/status') {
      return { body: { file: false, applied: false, error: null } };
    }
    if (method === 'GET' && path === '/sources/roots') {
      return { body: ['/config/sources'] };
    }
    return { status: 204 };
  });
}

const callsOf = (fetchMock: ReturnType<typeof server>, method: string, url: string) =>
  findCall(fetchMock, ([u, init]) => u === url && (init as RequestInit | undefined)?.method === method);

describe('Sources', () => {
  it('shows each kind of source in its own section, with what each one holds and when it was indexed', async () => {
    server();

    renderWithProviders(<Sources />);

    const knowledge = await screen.findByRole('region', { name: 'Knowledge vaults' });
    const cours = rowOf(within(knowledge).getByRole('rowheader', { name: 'cours' }));
    expect(within(cours).getByText('Read and write')).toBeInTheDocument();
    expect(within(cours).getByText('/config/sources/cours')).toBeInTheDocument();
    expect(within(cours).getByText('12')).toBeInTheDocument();
    expect(within(cours).getByText('40')).toBeInTheDocument();
    expect(within(cours).getByText('2026-11-03 10:30')).toBeInTheDocument();
    const memory = screen.getByRole('region', { name: 'Memory vault' });
    expect(
      within(rowOf(within(memory).getByRole('rowheader', { name: 'memoire' }))).getByText('Not yet'),
    ).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Application documentation' })).toBeInTheDocument();
  });

  it('shows the memory vault as a required slot that is filled: nothing to add, nothing to remove, a folder to change', async () => {
    server();
    renderWithProviders(<Sources />);

    const memory = await screen.findByRole('region', { name: 'Memory vault' });

    expect(within(memory).getByText(/required, and there can be only one\. Change its folder/)).toBeInTheDocument();
    expect(within(memory).queryByRole('alert')).not.toBeInTheDocument();
    expect(within(memory).queryByRole('form', { name: /Declare a source/ })).not.toBeInTheDocument();
    expect(within(memory).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(within(memory).getByRole('button', { name: 'Change memoire' })).toBeInTheDocument();
  });

  it('says what is missing when the memory vault is not declared, and offers to fill the slot with its name and mode proposed', async () => {
    server(
      () => undefined,
      sources.filter((source) => source.kind !== 'Memory'),
    );
    renderWithProviders(<Sources />);

    const memory = await screen.findByRole('region', { name: 'Memory vault' });

    expect(await within(memory).findByRole('alert')).toHaveTextContent('No source is declared for Memory vault');
    const form = within(memory).getByRole('form', { name: 'Declare a source of Memory vault' });
    expect(within(form).getByLabelText('Name')).toHaveValue('memory');
    expect(within(form).getByLabelText('Mode')).toHaveValue('ReadOnly');
  });

  it('fills the memory vault slot with the kind of the section, not a kind the person chose', async () => {
    const fetchMock = server(
      () => undefined,
      sources.filter((source) => source.kind !== 'Memory'),
    );
    renderWithProviders(<Sources />);
    const form = await screen.findByRole('form', { name: 'Declare a source of Memory vault' });

    await userEvent.type(within(form).getByLabelText('Folder'), '/config/sources/memory');
    await userEvent.click(within(form).getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/api/v1/sources', expect.objectContaining({ method: 'POST' })),
    );
    expect(sentBody(callsOf(fetchMock, 'POST', '/api/v1/sources'))).toEqual({
      name: 'memory',
      kind: 'Memory',
      mode: 'ReadOnly',
      path: '/config/sources/memory',
    });
  });

  it('lets knowledge vaults and application documentation grow, with a form in each section that has no kind to choose', async () => {
    const fetchMock = server();
    renderWithProviders(<Sources />);
    const form = await screen.findByRole('form', { name: 'Declare a source of Application documentation' });
    expect(within(form).getByLabelText('Mode')).toHaveValue('ReadOnly');
    expect(within(form).queryByLabelText('Kind')).not.toBeInTheDocument();

    await userEvent.type(within(form).getByLabelText('Name'), 'portarr-docs');
    await userEvent.type(within(form).getByLabelText('Folder'), '/config/sources/portarr');
    await userEvent.click(within(form).getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/api/v1/sources', expect.objectContaining({ method: 'POST' })),
    );
    expect(sentBody(callsOf(fetchMock, 'POST', '/api/v1/sources'))).toEqual({
      name: 'portarr-docs',
      kind: 'ApplicationDocs',
      mode: 'ReadOnly',
      path: '/config/sources/portarr',
    });
    await waitFor(() => expect(within(form).getByLabelText('Name')).toHaveValue(''));
    expect(screen.getByRole('form', { name: 'Declare a source of Knowledge vaults' })).toBeInTheDocument();
  });

  it('changes the folder, the mode or the name of a source, and gives the change up', async () => {
    const fetchMock = server();
    renderWithProviders(<Sources />);
    const memory = await screen.findByRole('region', { name: 'Memory vault' });

    await userEvent.click(within(memory).getByRole('button', { name: 'Change memoire' }));
    const form = within(memory).getByRole('form', { name: 'Change the source of Memory vault' });
    expect(within(form).getByLabelText('Folder')).toHaveValue('/config/sources/memoire');
    await userEvent.clear(within(form).getByLabelText('Folder'));
    await userEvent.type(within(form).getByLabelText('Folder'), '/config/sources/memoire-2');
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/api/v1/sources/s2', expect.objectContaining({ method: 'PUT' })),
    );
    expect(sentBody(callsOf(fetchMock, 'PUT', '/api/v1/sources/s2'))).toEqual({
      name: 'memoire',
      mode: 'ReadOnly',
      path: '/config/sources/memoire-2',
    });
    await userEvent.click(within(memory).getByRole('button', { name: 'Change memoire' }));
    await userEvent.click(within(memory).getByRole('button', { name: 'Cancel' }));
    expect(within(memory).queryByRole('form', { name: /Change the source/ })).not.toBeInTheDocument();
  });

  it('shows a source that a configuration owns as managed, without the buttons that would change it by hand', async () => {
    server(
      () => undefined,
      sources.map((source) => ({ ...source, managed: source.kind === 'Memory' || source.name === 'cours' })),
    );
    renderWithProviders(<Sources />);

    const cours = rowOf(await screen.findByRole('rowheader', { name: /cours/ }));
    const memory = within(await screen.findByRole('region', { name: 'Memory vault' }));

    expect(within(cours).getByText('Managed by configuration')).toBeInTheDocument();
    expect(within(cours).queryByRole('button', { name: /Change/ })).not.toBeInTheDocument();
    expect(within(cours).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(within(cours).getByRole('button', { name: 'Index now for cours' })).toBeInTheDocument();
    expect(memory.queryByRole('button', { name: /Change/ })).not.toBeInTheDocument();
  });

  it('says why the last scan of a source failed, since its old notes are still there', async () => {
    server(
      () => undefined,
      sources.map((source) => (source.name === 'cours' ? { ...source, lastError: 'folder-missing' as const } : source)),
    );
    renderWithProviders(<Sources />);

    const cours = rowOf(await screen.findByRole('rowheader', { name: /cours/ }));

    expect(within(cours).getByRole('alert')).toHaveTextContent(
      'The last scan failed: the folder is not there any more',
    );
    expect(
      within(rowOf(screen.getByRole('rowheader', { name: /memoire/ }))).queryByRole('alert'),
    ).not.toBeInTheDocument();
  });

  it('shows the current state as a configuration document to keep in a repository', async () => {
    server();
    renderWithProviders(<Sources />);

    const card = await screen.findByRole('region', { name: 'Configuration as code' });

    expect(await within(card).findByLabelText('Current configuration (YAML)')).toHaveValue(
      'memory:\n  name: memoire\n  path: /config/sources/memoire\n',
    );
  });

  it('previews a configuration without applying it, then applies it and lists what changed', async () => {
    const plan = (applied: boolean) => ({
      applied,
      items: [
        { action: 'Create', kind: 'Knowledge', name: 'travail', path: '/config/sources/travail', mode: 'ReadWrite' },
        { action: 'Keep', kind: 'Memory', name: 'memoire', path: '/config/sources/memoire', mode: 'ReadWrite' },
      ],
    });
    const fetchMock = server((method, path) =>
      method === 'PUT' && path.startsWith('/config') ? { body: plan(!path.includes('dryRun')) } : undefined,
    );
    renderWithProviders(<Sources />);
    const card = await screen.findByRole('region', { name: 'Configuration as code' });
    await userEvent.type(within(card).getByLabelText('Configuration to apply (YAML)'), 'memory: x');
    const body = (url: string) => {
      const call = findCall(fetchMock, ([u, init]) => u === url && (init as RequestInit | undefined)?.method === 'PUT');
      return (call[1] as RequestInit).body;
    };

    await userEvent.click(within(card).getByRole('button', { name: 'Preview' }));
    expect(await within(card).findByText('Preview, nothing was changed: 1 change(s).')).toBeInTheDocument();
    expect(within(card).getByText('Create Knowledge travail')).toBeInTheDocument();
    expect(within(card).queryByText(/Keep Memory/)).not.toBeInTheDocument();
    expect(body('/api/v1/config?dryRun=true')).toBe('memory: x');

    await userEvent.click(within(card).getByRole('button', { name: 'Apply' }));
    expect(await within(card).findByText('Applied: 1 change(s).')).toBeInTheDocument();
    expect(body('/api/v1/config')).toBe('memory: x');
  });

  it('says every reason a configuration cannot be applied, and that nothing was changed', async () => {
    server((method, path) =>
      method === 'PUT' && path.startsWith('/config')
        ? {
            status: 400,
            body: {
              title: 'The configuration cannot be applied: nothing was changed.',
              errors: ['knowledge[1]: it has no name.', 'The name x is used twice.'],
            },
          }
        : undefined,
    );
    renderWithProviders(<Sources />);
    const card = await screen.findByRole('region', { name: 'Configuration as code' });
    await userEvent.type(within(card).getByLabelText('Configuration to apply (YAML)'), 'memory: x');

    await userEvent.click(within(card).getByRole('button', { name: 'Apply' }));

    const alert = await within(card).findByRole('alert');
    expect(alert).toHaveTextContent('nothing was changed');
    expect(
      within(alert)
        .getAllByRole('listitem')
        .map((item) => item.textContent),
    ).toEqual(['knowledge[1]: it has no name.', 'The name x is used twice.']);
  });

  it('offers neither to preview nor to apply an empty document', async () => {
    server();
    renderWithProviders(<Sources />);
    const card = await screen.findByRole('region', { name: 'Configuration as code' });

    expect(within(card).getByRole('button', { name: 'Preview' })).toBeDisabled();
    expect(within(card).getByRole('button', { name: 'Apply' })).toBeDisabled();
  });

  it('says that a file is in charge and why it was not applied, and does not offer to apply a document', async () => {
    server((method, path) =>
      method === 'GET' && path === '/config/status'
        ? { body: { file: true, applied: false, error: 'The configuration file cannot be read.' } }
        : undefined,
    );
    renderWithProviders(<Sources />);
    const card = await screen.findByRole('region', { name: 'Configuration as code' });

    expect(await within(card).findByText(/given to Mnemarr as a file/)).toBeInTheDocument();
    expect(await within(card).findByRole('alert')).toHaveTextContent(
      'The configuration file was not applied: The configuration file cannot be read.',
    );
    await userEvent.type(within(card).getByLabelText('Configuration to apply (YAML)'), 'memory: x');
    expect(within(card).getByRole('button', { name: 'Apply' })).toBeDisabled();
    expect(within(card).getByRole('button', { name: 'Preview' })).toBeEnabled();
  });

  it('says when the configuration cannot be read', async () => {
    server((method, path) => (method === 'GET' && path === '/config' ? { status: 500 } : undefined));
    renderWithProviders(<Sources />);
    const card = await screen.findByRole('region', { name: 'Configuration as code' });

    expect(await within(card).findByRole('alert')).toHaveTextContent('Could not read the configuration.');
  });

  it('says when the sources cannot be read', async () => {
    stubApi((method, path) => (method === 'GET' && path === '/sources' ? { status: 500 } : { body: [] }));

    renderWithProviders(<Sources />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not read the sources.');
  });

  it('shows where a source may be, and why a source was refused', async () => {
    server((method, path) =>
      method === 'POST' && path === '/sources'
        ? { status: 409, body: { title: 'There is already a source of the kind Memory: there can be only 1.' } }
        : undefined,
    );
    renderWithProviders(<Sources />);
    const form = await screen.findByRole('form', { name: 'Declare a source of Knowledge vaults' });
    expect(await within(form).findByText('A source is a folder inside one of: /config/sources')).toBeInTheDocument();

    await userEvent.type(within(form).getByLabelText('Name'), 'docs');
    await userEvent.type(within(form).getByLabelText('Folder'), '/config/sources/nope');
    await userEvent.click(within(form).getByRole('button', { name: 'Create' }));

    expect(await within(form).findByRole('alert')).toHaveTextContent('There is already a source of the kind Memory');
  });

  it('indexes a source now and says what that did', async () => {
    const fetchMock = server((method, path) =>
      method === 'POST' && path === '/sources/s1/index'
        ? { body: { added: 3, updated: 1, removed: 0, unchanged: 8 } }
        : undefined,
    );
    renderWithProviders(<Sources />);

    await userEvent.click(await screen.findByRole('button', { name: 'Index now for cours' }));

    expect(await screen.findByText('3 added, 1 updated, 0 removed.')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/sources/s1/index', expect.objectContaining({ method: 'POST' }));
  });

  it('forgets an optional source only after a second press that names it, and lets the deletion be given up', async () => {
    const fetchMock = server();
    renderWithProviders(<Sources />);
    const cours = rowOf(await screen.findByRole('rowheader', { name: 'cours' }));

    await userEvent.click(within(cours).getByRole('button', { name: 'Delete' }));
    await userEvent.click(within(cours).getByRole('button', { name: 'Cancel' }));
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE')).toBe(false);
    await userEvent.click(within(cours).getByRole('button', { name: 'Delete' }));
    await userEvent.click(within(cours).getByRole('button', { name: 'Confirm: delete cours' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/api/v1/sources/s1', expect.objectContaining({ method: 'DELETE' })),
    );
  });

  it('shows why a change was refused, and stops showing it once a later one has worked', async () => {
    let refuse = true;
    server((method, path) => {
      if (method !== 'POST' || path !== '/sources/s1/index') {
        return undefined;
      }
      const answer = refuse
        ? { status: 409, body: { title: 'The folder of the source does not exist any more.' } }
        : { body: { added: 0, updated: 0, removed: 0, unchanged: 1 } };
      refuse = false;
      return answer;
    });
    renderWithProviders(<Sources />);
    const index = await screen.findByRole('button', { name: 'Index now for cours' });

    await userEvent.click(index);
    expect(await screen.findByRole('alert')).toHaveTextContent('The folder of the source does not exist any more.');
    await userEvent.click(index);

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });
});
