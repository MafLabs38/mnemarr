import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { type ApiAnswer, readerSession, renderWithProviders, stubApi } from '__tests__/render';
import Notes from 'Notes/Notes';
import type { Note, NoteEntry } from 'Notes/notes';

// The drawing is replaced by what the test says it does. It is a plain function, not a spy: a spy re-throws the rejection it records.
const drawing = vi.hoisted(() => ({ run: (): Promise<string> => Promise.resolve('') }));
vi.mock('Notes/mermaid', () => ({
  drawDiagram: (source: string) => (drawing.run as (source: string) => Promise<string>)(source),
  MAX_DIAGRAM_CHARS: 20_000,
}));

const entries: NoteEntry[] = [
  { sourceId: 's1', sourceName: 'cours', path: 'devops/docker.md', title: 'Docker', topic: 'devops' },
  { sourceId: 's1', sourceName: 'cours', path: 'devops/kubernetes.md', title: 'Kubernetes', topic: 'devops' },
  { sourceId: 's2', sourceName: 'memoire', path: 'decisions/reseau.md', title: 'Choix du réseau', topic: 'decisions' },
];

function noteWith(markdown: string, extra: Partial<Note> = {}): Note {
  return {
    sourceId: 's1',
    sourceName: 'cours',
    path: 'devops/docker.md',
    title: 'Docker',
    markdown,
    tags: [],
    links: [],
    backlinks: [],
    embeds: {},
    ...extra,
  };
}

function server(note: Note, extra: (path: string) => ApiAnswer | undefined = () => undefined) {
  return stubApi((method, path) => {
    const special = extra(path);
    if (special) {
      return special;
    }
    if (method === 'GET' && (path === '/notes' || path.startsWith('/notes?'))) {
      return { body: entries };
    }
    if (method === 'GET' && path === '/notes/tags') {
      return { body: [{ tag: 'infra', count: 2 }] };
    }
    if (method === 'GET' && path.startsWith('/notes/s1/')) {
      return { body: note };
    }
    return undefined;
  });
}

const open = '/notes?source=s1&path=devops%2Fdocker.md';

describe('Notes', () => {
  it('lists the notes by source and folder and opens the one in the address', async () => {
    server(noteWith('# Docker\n\nUne image est faite de couches.'));
    renderWithProviders(<Notes />, open, readerSession);

    const tree = await screen.findByRole('navigation', { name: 'Notes' });

    expect(within(tree).getByText('cours')).toBeInTheDocument();
    expect(within(tree).getByText('memoire')).toBeInTheDocument();
    expect(within(tree).getByRole('link', { name: 'Docker' })).toHaveAttribute('aria-current', 'page');
    expect(within(tree).getByRole('link', { name: 'Kubernetes' })).toHaveAttribute(
      'href',
      '/notes?source=s1&path=devops%2Fkubernetes.md',
    );
    expect(await screen.findByText('Une image est faite de couches.')).toBeInTheDocument();
  });

  it('filters the tree by title or path', async () => {
    server(noteWith('# Docker'));
    renderWithProviders(<Notes />, '/notes', readerSession);
    const tree = await screen.findByRole('navigation', { name: 'Notes' });

    await userEvent.type(screen.getByLabelText('Filter the notes'), 'réseau');

    expect(within(tree).queryByRole('link', { name: 'Docker' })).not.toBeInTheDocument();
    expect(within(tree).getByRole('link', { name: 'Choix du réseau' })).toBeInTheDocument();
  });

  it('asks the person to choose a note when none is open', async () => {
    server(noteWith('# Docker'));
    renderWithProviders(<Notes />, '/notes', readerSession);

    expect(await screen.findByText('Choose a note in the list.')).toBeInTheDocument();
  });

  it('follows a wikilink to a note the person may read, and shows one that leads nowhere as plain text', async () => {
    server(
      noteWith('See [[kubernetes]] and [[ghost|the ghost]].', {
        links: [
          {
            target: 'kubernetes',
            note: { sourceId: 's1', sourceName: 'cours', path: 'devops/kubernetes.md', title: 'Kubernetes' },
          },
          { target: 'ghost', note: null },
        ],
      }),
    );
    renderWithProviders(<Notes />, open, readerSession);

    const body = await screen.findByText(/See/);

    expect(within(body).getByRole('link', { name: 'kubernetes' })).toHaveAttribute(
      'href',
      '/notes?source=s1&path=devops%2Fkubernetes.md',
    );
    expect(within(body).queryByRole('link', { name: 'the ghost' })).not.toBeInTheDocument();
    expect(within(body).getByText('the ghost')).toHaveAttribute('title', 'No note of this name that you may read');
  });

  it('shows the tags, which lead to the notes that carry them, and the backlinks', async () => {
    server(
      noteWith('# Docker', {
        tags: ['infra'],
        backlinks: [{ sourceId: 's2', sourceName: 'memoire', path: 'decisions/reseau.md', title: 'Choix du réseau' }],
      }),
    );
    renderWithProviders(<Notes />, open, readerSession);

    const tags = await screen.findByRole('list', { name: 'Tags' });
    const backlinks = screen.getByRole('region', { name: 'Backlinks' });

    expect(within(tags).getByRole('link', { name: '#infra' })).toHaveAttribute('href', '/notes?tag=infra');
    expect(within(backlinks).getByRole('link', { name: 'Choix du réseau' })).toHaveAttribute(
      'href',
      '/notes?source=s2&path=decisions%2Freseau.md',
    );
  });

  it('says when no note links to this one', async () => {
    server(noteWith('# Docker'));
    renderWithProviders(<Notes />, open, readerSession);

    expect(await screen.findByText('No note links to this one.')).toBeInTheDocument();
  });

  it('asks only for the notes of a tag when one is chosen', async () => {
    const mock = server(noteWith('# Docker'));
    renderWithProviders(<Notes />, '/notes?tag=infra', readerSession);

    await screen.findByRole('navigation', { name: 'Notes' });

    expect(mock.mock.calls.map((call) => call[0])).toContain('/api/v1/notes?tag=infra');
    expect(screen.getByRole('heading', { level: 1, name: '#infra' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'All notes' })).toHaveAttribute('href', '/notes');
  });

  describe('treats a note as untrusted text', () => {
    it('does not render raw HTML or run a script', async () => {
      server(noteWith('<script>window.hacked = true</script>\n\n<img src=x onerror="window.hacked = true">\n\nplain'));
      const { container } = renderWithProviders(<Notes />, open, readerSession);

      await screen.findByText('plain');

      expect(container.querySelector('script')).toBeNull();
      expect(container.querySelector('img[src="x"]')).toBeNull();
      expect((globalThis as unknown as { hacked?: boolean }).hacked).toBeUndefined();
    });

    it('does not make a link of a javascript: or data: address, and opens a web link without giving the opener away', async () => {
      server(noteWith('[bad](javascript:alert(1)) [data](data:text/html,x) [web](https://example.com/page)'));
      renderWithProviders(<Notes />, open, readerSession);

      await screen.findByText('bad');

      expect(screen.queryByRole('link', { name: 'bad' })).not.toBeInTheDocument();
      expect(screen.queryByRole('link', { name: 'data' })).not.toBeInTheDocument();
      const web = screen.getByRole('link', { name: 'web' });
      expect(web).toHaveAttribute('href', 'https://example.com/page');
      expect(web).toHaveAttribute('rel', 'noopener noreferrer nofollow');
      expect(web).toHaveAttribute('target', '_blank');
    });

    it('fetches an image of the note from its own source only, and shows another address as text', async () => {
      server(
        noteWith(
          '![[plan.png]] ![relative](img/a.png) ![outside](../../x.png) ![remote](https://tracker.example/pixel.gif) ![[missing.png]]',
          {
            embeds: { 'plan.png': 'devops/plan.png' },
          },
        ),
      );
      const { container } = renderWithProviders(<Notes />, open, readerSession);

      await screen.findByText(/Image not shown: remote/);

      const sources = [...container.querySelectorAll('img')].map((image) => image.getAttribute('src'));
      expect(sources).toEqual(['/api/v1/files/s1/devops/plan.png', '/api/v1/files/s1/devops/img/a.png']);
      expect(screen.getByText('Image not shown: outside')).toBeInTheDocument();
      expect(screen.getByText('Image not shown: missing.png')).toBeInTheDocument();
    });

    it('follows a relative link to a Markdown file inside the source and refuses one that leaves it', async () => {
      server(noteWith('[next](kubernetes.md) [up](../../x.md) [file](paper.pdf)'));
      renderWithProviders(<Notes />, open, readerSession);

      await screen.findByText('up');

      expect(screen.getByRole('link', { name: 'next' })).toHaveAttribute(
        'href',
        '/notes?source=s1&path=devops%2Fkubernetes.md',
      );
      expect(screen.queryByRole('link', { name: 'up' })).not.toBeInTheDocument();
      expect(screen.queryByRole('link', { name: 'file' })).not.toBeInTheDocument();
    });
  });

  it('turns a <br> into a line break, in a table cell too, and renders no other HTML', async () => {
    server(noteWith('| a | b |\n|---|---|\n| one<br>two | x<br/>y |\n\nline<br />break <b>bold</b> <u>under</u>'));
    const { container } = renderWithProviders(<Notes />, open, readerSession);

    await screen.findByRole('table');

    expect(container.querySelectorAll('td br')).toHaveLength(2);
    expect(container.querySelectorAll('p br')).toHaveLength(1);
    expect(container.querySelector('b')).toBeNull();
    expect(container.querySelector('u')).toBeNull();
    expect(container.textContent).not.toContain('<br');
  });

  it('renders the tables of GitHub Markdown and the callouts of Obsidian', async () => {
    server(
      noteWith('| a | b |\n|---|---|\n| 1 | 2 |\n\n> [!warning] Careful\n> This is a callout.\n\n> a plain quote'),
    );
    const { container } = renderWithProviders(<Notes />, open, readerSession);

    await screen.findByRole('table');

    const callout = container.querySelector('.callout-warning');
    expect(callout).not.toBeNull();
    expect(within(callout as HTMLElement).getByText('Careful')).toBeInTheDocument();
    expect(within(callout as HTMLElement).getByText('This is a callout.')).toBeInTheDocument();
    expect(screen.getByText('a plain quote').closest('blockquote')).not.toBeNull();
  });

  it('folds a callout whose type ends with a dash, keeps the formatting of its title, and opens one that ends with a plus', async () => {
    server(
      noteWith(
        '> [!question]- Is `echo x | base64` **encryption**?\n> No: it is an encoding.\n\n> [!info]+ Open\n> Visible.',
      ),
    );
    const { container } = renderWithProviders(<Notes />, open, readerSession);

    await screen.findByText('No: it is an encoding.');

    const folded = container.querySelector('details.callout-question');
    expect(folded).not.toBeNull();
    expect(folded).not.toHaveAttribute('open');
    expect(within(folded as HTMLElement).getByText('echo x | base64').tagName).toBe('CODE');
    expect(within(folded as HTMLElement).getByText('encryption').tagName).toBe('STRONG');
    expect(container.querySelector('details.callout-info')).toHaveAttribute('open');
  });

  describe('Mermaid diagrams', () => {
    it('draws a mermaid block as an image, with its text available, and leaves other code blocks as code', async () => {
      const draw = vi.fn(() => Promise.resolve('data:image/svg+xml;charset=utf-8,%3Csvg%2F%3E'));
      drawing.run = draw;
      server(noteWith('```mermaid\nflowchart TB\n  A --> B\n```\n\n```yaml\nkey: value\n```'));
      const { container } = renderWithProviders(<Notes />, open, readerSession);

      const image = await screen.findByRole('img', { name: 'Diagram' });

      expect(image).toHaveAttribute('src', 'data:image/svg+xml;charset=utf-8,%3Csvg%2F%3E');
      expect(draw).toHaveBeenCalledWith('flowchart TB\n  A --> B');
      expect(screen.getByText('Text of the diagram')).toBeInTheDocument();
      expect(container.querySelector('code.language-yaml')).not.toBeNull();
      expect(container.querySelectorAll('code.language-mermaid')).toHaveLength(0);
    });

    it('says why a diagram cannot be drawn and shows its text, without losing the rest of the note', async () => {
      drawing.run = () => Promise.reject(new Error('Parse error on line 2'));
      server(noteWith('Before\n\n```mermaid\nflowchart ???\n```\n\nAfter'));
      renderWithProviders(<Notes />, open, readerSession);

      const alert = await screen.findByRole('alert');

      expect(alert).toHaveTextContent('This diagram could not be drawn.');
      expect(alert).toHaveTextContent('flowchart ???');
      expect(screen.getByText('Before')).toBeInTheDocument();
      expect(screen.getByText('After')).toBeInTheDocument();
    });
  });

  it('says when a note does not exist or cannot be read, and when the notes cannot be read', async () => {
    server(noteWith('# x'), (path) => (path.startsWith('/notes/s1/') ? { status: 404 } : undefined));
    renderWithProviders(<Notes />, open, readerSession);

    expect(await screen.findByText('There is no such note, or you may not read it.')).toBeInTheDocument();
  });

  it('says when the notes cannot be listed', async () => {
    stubApi((_method, path) => (path === '/notes' ? { status: 500 } : { body: [] }));
    renderWithProviders(<Notes />, '/notes', readerSession);

    expect(await screen.findByText('Could not read the notes.')).toBeInTheDocument();
  });
});
