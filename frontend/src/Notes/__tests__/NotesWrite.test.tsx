import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { type ApiAnswer, findCall, readerSession, renderWithProviders, sentBody, stubApi } from '__tests__/render';
import Notes from 'Notes/Notes';
import { headingSlug, type Note } from 'Notes/notes';
import type { SourceItem } from 'Sources/sources';

vi.mock('Notes/mermaid', () => ({ drawDiagram: () => Promise.resolve(''), MAX_DIAGRAM_CHARS: 20_000 }));

const S1 = '11111111-1111-4111-8111-111111111111';
const open = `/notes?source=${S1}&path=devops%2Fdocker.md`;

const note: Note = {
  sourceId: S1,
  sourceName: 'cours',
  path: 'devops/docker.md',
  title: 'Docker',
  markdown: '# Docker\nImages.',
  rendered: '# Docker\nImages.',
  tags: [],
  links: [],
  backlinks: [],
  backlinksTotal: 0,
  raw: '---\ntags: [infra]\n---\n# Docker\nImages.',
  version: 'v1',
  writable: true,
};

const writableSource: SourceItem = {
  id: S1,
  name: 'cours',
  kind: 'Knowledge',
  mode: 'ReadWrite',
  path: '/config/sources/cours',
  createdAt: '2026-11-04T09:00:00Z',
  notes: 1,
  chunks: 1,
  lastIndexedAt: null,
  lastScanAt: null,
  lastError: null,
  managed: false,
  repository: null,
  lastSyncAt: null,
  lastSyncError: null,
  head: null,
};

function server(
  extra: (method: string, path: string, init?: RequestInit) => ApiAnswer | undefined = () => undefined,
  current: Note = note,
) {
  return stubApi((method, path, init) => {
    const special = extra(method, path, init);
    if (special) {
      return special;
    }
    if (method === 'GET' && (path === '/notes' || path.startsWith('/notes?'))) {
      return {
        body: {
          notes: [{ sourceId: S1, sourceName: 'cours', path: 'devops/docker.md', title: 'Docker', topic: 'devops' }],
          total: 1,
        },
      };
    }
    if (method === 'GET' && path === '/notes/tags') {
      return { body: { tags: [], total: 0 } };
    }
    if (method === 'GET' && path === '/sources') {
      return { body: [writableSource] };
    }
    if (method === 'GET' && path.startsWith(`/notes/${S1}/`)) {
      return { body: current };
    }
    return undefined;
  });
}

const callOf = (fetchMock: ReturnType<typeof server>, method: string, url: string) =>
  findCall(fetchMock, ([u, init]) => u === `/api/v1${url}` && (init as RequestInit | undefined)?.method === method);

describe('writing notes', () => {
  it('offers to edit, move and remove a note only when it is writable', async () => {
    server(() => undefined, { ...note, writable: false });
    renderWithProviders(<Notes />, open, readerSession);

    expect(await screen.findByRole('heading', { name: 'Docker' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rename' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
  });

  it('edits the whole text of a note and sends back the version that was read', async () => {
    const fetchMock = server((method) =>
      method === 'PUT' ? { body: { path: 'devops/docker.md', version: 'v2', git: null } } : undefined,
    );
    renderWithProviders(<Notes />, open);

    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const text = screen.getByRole('textbox', { name: 'Note text' });
    expect(text).toHaveValue(note.raw);
    await userEvent.clear(text);
    await userEvent.type(text, 'changed');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(callOf(fetchMock, 'PUT', `/notes/${S1}/devops/docker.md`)).toBeDefined());
    expect(sentBody(callOf(fetchMock, 'PUT', `/notes/${S1}/devops/docker.md`))).toEqual({
      content: 'changed',
      version: 'v1',
    });
    await waitFor(() => expect(screen.queryByRole('textbox', { name: 'Note text' })).not.toBeInTheDocument());
  });

  it('keeps what was typed and says why when the note changed since it was read', async () => {
    server((method) =>
      method === 'PUT'
        ? { status: 409, body: { title: 'The note changed since it was read: read it again before changing it.' } }
        : undefined,
    );
    renderWithProviders(<Notes />, open);

    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const text = screen.getByRole('textbox', { name: 'Note text' });
    await userEvent.type(text, ' more words');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/changed since it was read/u)).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Note text' })).toHaveValue(`${note.raw} more words`);
  });

  it('says a change was committed but not pushed, and that a later sync pushes it', async () => {
    server((method) =>
      method === 'PUT'
        ? { body: { path: 'devops/docker.md', version: 'v2', git: { pushed: false, error: 'push-rejected' } } }
        : undefined,
    );
    renderWithProviders(<Notes />, open);

    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText(/Saved and committed, but not pushed yet: the host refused the push/u),
    ).toBeInTheDocument();
  });

  it('previews the text as the server would draw it, and hides the preview again', async () => {
    const fetchMock = server((method, path) =>
      method === 'POST' && path === `/notes/${S1}/preview` ? { body: { rendered: 'A **drawn** preview' } } : undefined,
    );
    renderWithProviders(<Notes />, open);

    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    await userEvent.click(screen.getByRole('button', { name: 'Preview' }));

    const preview = await screen.findByRole('region', { name: 'Preview' });
    expect(within(preview).getByText('drawn')).toBeInTheDocument();
    expect(sentBody(callOf(fetchMock, 'POST', `/notes/${S1}/preview`))).toEqual({
      path: 'devops/docker.md',
      content: note.raw,
    });
    await userEvent.click(screen.getByRole('button', { name: 'Hide preview' }));
    expect(screen.queryByRole('region', { name: 'Preview' })).not.toBeInTheDocument();
  });

  it('gives the edit up', async () => {
    server();
    renderWithProviders(<Notes />, open);

    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByRole('textbox', { name: 'Note text' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument();
  });

  it('moves a note to a new path and shows the note where it is now', async () => {
    const fetchMock = server((method, path) =>
      method === 'POST' && path === `/notes/${S1}/rename`
        ? {
            body: {
              path: 'ops/engine.md',
              version: 'v2',
              linksRewritten: 3,
              notesRewritten: 2,
              notesNotRewritten: 0,
              git: null,
            },
          }
        : undefined,
    );
    renderWithProviders(<Notes />, open);

    await userEvent.click(await screen.findByRole('button', { name: 'Rename' }));
    const field = screen.getByLabelText('New path');
    await userEvent.clear(field);
    await userEvent.type(field, 'ops/engine.md');
    await userEvent.click(within(screen.getByRole('form', { name: 'Rename' })).getByRole('button', { name: 'Rename' }));

    await waitFor(() => expect(callOf(fetchMock, 'POST', `/notes/${S1}/rename`)).toBeDefined());
    expect(sentBody(callOf(fetchMock, 'POST', `/notes/${S1}/rename`))).toEqual({
      from: 'devops/docker.md',
      to: 'ops/engine.md',
    });
  });

  it('removes a note only after a second press that names it, and sends the version it read', async () => {
    const fetchMock = server((method) => (method === 'DELETE' ? { body: { git: null } } : undefined));
    renderWithProviders(<Notes />, open);

    await userEvent.click(await screen.findByRole('button', { name: 'Remove' }));
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE')).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Confirm: remove Docker' }));

    await waitFor(() => expect(callOf(fetchMock, 'DELETE', `/notes/${S1}/devops/docker.md?version=v1`)).toBeDefined());
  });

  it('lets the person who manages sources start a note in a source that is open for writing', async () => {
    const fetchMock = server((method, path) =>
      method === 'POST' && path === `/notes/${S1}/Topic/My%20note.md`
        ? { status: 201, body: { path: 'Topic/My note.md', version: 'v1', git: null } }
        : undefined,
    );
    renderWithProviders(<Notes />, '/notes');

    await userEvent.click(await screen.findByText('New note'));
    await userEvent.type(screen.getByLabelText('Path'), 'Topic/My note.md');
    await userEvent.click(screen.getByRole('button', { name: 'Create note' }));

    await waitFor(() => expect(callOf(fetchMock, 'POST', `/notes/${S1}/Topic/My%20note.md`)).toBeDefined());
    expect(sentBody(callOf(fetchMock, 'POST', `/notes/${S1}/Topic/My%20note.md`))).toEqual({
      content: '# My note\n\n',
    });
  });

  it('offers no new note to someone who may not write, nor when no source is open for writing', async () => {
    server();
    const { unmount } = renderWithProviders(<Notes />, '/notes', readerSession);
    await screen.findByText('Docker');
    expect(screen.queryByText('New note')).not.toBeInTheDocument();
    unmount();

    server((method, path) =>
      method === 'GET' && path === '/sources' ? { body: [{ ...writableSource, mode: 'ReadOnly' }] } : undefined,
    );
    renderWithProviders(<Notes />, '/notes');
    await screen.findByText('Docker');
    expect(screen.queryByText('New note')).not.toBeInTheDocument();
  });
});

describe('links to a heading', () => {
  it('draws headings with ids and turns a link to a heading into an address that leads to it', async () => {
    server(() => undefined, {
      ...note,
      writable: false,
      rendered: `# Docker\n\n## Part One\n\nSee [Part](mnemarr-note:${S1}/devops%2Fdocker.md#part-one).`,
    });
    renderWithProviders(<Notes />, open);

    const heading = await screen.findByRole('heading', { name: 'Part One' });
    expect(heading).toHaveAttribute('id', 'part-one');
    expect(screen.getByRole('link', { name: 'Part' })).toHaveAttribute('href', expect.stringContaining('#part-one'));
  });
});

describe('headingSlug', () => {
  it.each([
    ['Part One', 'part-one'],
    ['  Ça va ?  ', 'ça-va-'],
    ['Under_score and-dash', 'under_score-and-dash'],
    ['Code `x` & more', 'code-x--more'],
    ['', ''],
  ])('writes %j as %j, as the server does', (heading, slug) => {
    expect(headingSlug(heading)).toBe(slug);
  });
});
