import { act, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { type ApiAnswer, findCall, readerSession, renderWithProviders, sentBody, stubApi } from '__tests__/render';
import Page from 'Components/Page/Page';
import type { Session } from 'Session/session';

import { resetChatStore } from '../chatStore';
import type { ChatAgent, ChatEvent, ChatStatus, ConversationSummary } from '../typings';

/** Only what the panel reads of an EventSource: the stream of one conversation, which the test feeds. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  readonly url: string;
  private readonly listeners = new Map<string, (message: MessageEvent<string>) => void>();

  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: (message: MessageEvent<string>) => void) {
    this.listeners.set(type, listener);
  }

  close() {
    FakeEventSource.instances = FakeEventSource.instances.filter((instance) => instance !== this);
  }

  emit(event: ChatEvent) {
    this.listeners.get('chat')?.({ data: JSON.stringify(event) } as MessageEvent<string>);
  }
}

const at = '2026-11-08T10:00:00Z';

const agents: ChatAgent[] = [
  {
    id: 'claude-code',
    name: 'Claude Code',
    available: true,
    defaultModel: 'sonnet',
    models: [
      { id: 'sonnet', label: 'Sonnet' },
      { id: 'opus', label: 'Opus' },
    ],
  },
  {
    id: 'openai',
    name: 'OpenAI-compatible',
    available: true,
    defaultModel: 'gpt-4.1',
    models: [{ id: 'gpt-4.1', label: 'gpt-4.1' }],
  },
];

const conversation: ConversationSummary = {
  id: 'c1',
  title: 'Backups',
  agent: 'claude-code',
  model: 'sonnet',
  createdAt: at,
  updatedAt: at,
  running: false,
  busy: false,
  pendingPermissions: 0,
};

interface Options {
  status?: Partial<ChatStatus>;
  conversations?: ConversationSummary[];
  createFails?: boolean;
}

function server(options: Options = {}) {
  const answers: Record<string, ApiAnswer> = {
    'GET /chat': { body: { enabled: true, reachable: true, agents, mcpWriteTools: true, ...options.status } },
    'GET /chat/conversations': { body: options.conversations ?? [] },
    'POST /chat/conversations': options.createFails
      ? { status: 502 }
      : { status: 201, body: { ...conversation, id: 'c9', title: '' } },
  };
  return stubApi((method, path) => answers[`${method} ${path}`] ?? { status: 202, body: {} });
}

async function openPanel(session?: Session) {
  renderWithProviders(<Page>content</Page>, '/', session);
  await userEvent.click(await screen.findByRole('button', { name: 'Chat' }));
  return screen.findByRole('complementary', { name: 'Chat' });
}

function stream(): FakeEventSource {
  const found = FakeEventSource.instances.at(-1);
  if (!found) {
    throw new Error('No stream is open.');
  }
  return found;
}

describe('ChatPanel', () => {
  beforeEach(() => {
    vi.stubGlobal('EventSource', FakeEventSource);
    FakeEventSource.instances = [];
    localStorage.clear();
    resetChatStore();
  });

  it('shows no chat at all when the operator gave no runner', async () => {
    server({ status: { enabled: false, reachable: false, agents: null } });
    renderWithProviders(<Page>content</Page>);

    expect(await screen.findByRole('navigation', { name: 'Main menu' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Chat' })).not.toBeInTheDocument();
  });

  it('asks nothing and shows nothing to a person whose role does not give the chat', async () => {
    const fetchMock = server();
    renderWithProviders(<Page>content</Page>, '/', { ...readerSession, permissions: ['notes.read', 'search'] });

    expect(await screen.findByRole('navigation', { name: 'Main menu' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Chat' })).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/api/v1/chat'))).toBe(false);
  });

  it('opens beside the page, says what it can do and that every change waits for approval', async () => {
    server();

    const panel = await openPanel();

    expect(within(panel).getByText(/Every change waits for your approval/)).toBeInTheDocument();
    expect(within(panel).getByRole('log', { name: 'Conversation' })).toBeInTheDocument();
    expect(screen.getByText('content')).toBeInTheDocument();
  });

  it('says that it only reads when the operator did not turn the writing tools on, and when the runner does not answer', async () => {
    server({ status: { mcpWriteTools: false, reachable: false, agents: null } });

    const panel = await openPanel();

    expect(within(panel).getByText(/the chat only reads/)).toBeInTheDocument();
    expect(within(panel).getByText(/runner does not answer/)).toBeInTheDocument();
  });

  it('warns when the agent has no credentials on the runner and sends nothing to it', async () => {
    server({ status: { agents: agents.slice(0, 1).map((agent) => ({ ...agent, available: false })) } });

    const panel = await openPanel();

    expect(within(panel).getByText(/Claude Code has no credentials/)).toBeInTheDocument();
    expect(within(panel).getByRole('button', { name: 'Send' })).toBeDisabled();
  });

  it('starts a conversation with the first message and sends it with the model chosen', async () => {
    const fetchMock = server();
    const panel = await openPanel();

    await userEvent.selectOptions(within(panel).getByLabelText('Agent'), 'openai');
    await userEvent.type(within(panel).getByLabelText('Your message'), 'What did we decide about backups?');
    await userEvent.click(within(panel).getByRole('button', { name: 'Send' }));

    const created = await vi.waitFor(() =>
      findCall(
        fetchMock,
        ([url, init]) => url === '/api/v1/chat/conversations' && (init as RequestInit | undefined)?.method === 'POST',
      ),
    );
    expect(sentBody(created)).toEqual({ agent: 'openai', model: 'gpt-4.1' });
    const message = await vi.waitFor(() =>
      findCall(fetchMock, ([url]) => url === '/api/v1/chat/conversations/c9/messages'),
    );
    expect(sentBody(message)).toEqual({ text: 'What did we decide about backups?', model: 'gpt-4.1' });
  });

  it('sends on Enter, breaks the line on Shift+Enter, and keeps what was typed when the composition of a character is not over', async () => {
    const fetchMock = server({ conversations: [conversation] });
    localStorage.setItem(
      'mnemarr.chat',
      JSON.stringify({ open: true, conversationId: 'c1', agent: 'claude-code', model: 'sonnet' }),
    );
    resetChatStore({ open: true, conversationId: 'c1', agent: 'claude-code', model: 'sonnet' });
    renderWithProviders(<Page>content</Page>);
    const box = await screen.findByLabelText('Your message');

    await userEvent.type(box, 'one{Shift>}{Enter}{/Shift}two');
    expect(box).toHaveValue('one\ntwo');
    await userEvent.type(box, '{Enter}');

    const message = await vi.waitFor(() =>
      findCall(fetchMock, ([url]) => url === '/api/v1/chat/conversations/c1/messages'),
    );
    expect(sentBody(message)).toEqual({ text: 'one\ntwo', model: 'sonnet' });
  });

  it('shows the answer being written, then the tool calls, and a change that waits for the person, who approves it', async () => {
    const fetchMock = server({ conversations: [conversation] });
    resetChatStore({ open: true, conversationId: 'c1', agent: 'claude-code', model: 'sonnet' });
    renderWithProviders(<Page>content</Page>);
    await screen.findByLabelText('Your message');
    const source = stream();

    act(() => {
      source.emit({ seq: 1, at, type: 'user', text: 'Add this to my courses' });
      source.emit({ seq: 0, at, type: 'status', state: 'running' });
      source.emit({ seq: 0, at, type: 'text-delta', text: 'Let me look' });
    });
    expect(await screen.findByText('Add this to my courses')).toBeInTheDocument();
    expect(screen.getByText('Let me look')).toBeInTheDocument();

    act(() => {
      source.emit({
        seq: 2,
        at,
        type: 'tool-call',
        id: 't1',
        name: 'mcp__mnemarr__read_note',
        input: { path: 'a.md' },
      });
      source.emit({ seq: 3, at, type: 'tool-result', id: 't1', isError: false, summary: 'the note' });
      source.emit({
        seq: 4,
        at,
        type: 'permission',
        requestId: 'r1',
        toolName: 'mcp__mnemarr__append_to_note',
        input: { path: 'a.md', text: 'new line' },
      });
    });
    const card = await screen.findByRole('region', { name: 'Apply this change?' });
    expect(within(card).getByText('append_to_note')).toBeInTheDocument();
    expect(within(card).getByText(/new line/)).toBeInTheDocument();

    await userEvent.click(within(card).getByRole('button', { name: 'Approve' }));

    const answered = await vi.waitFor(() =>
      findCall(fetchMock, ([url]) => url === '/api/v1/chat/conversations/c1/permissions/r1'),
    );
    expect(sentBody(answered)).toEqual({ approve: true });
    act(() => source.emit({ seq: 5, at, type: 'permission-resolved', requestId: 'r1', approved: true }));
    expect(await screen.findByText(/Approved: the change was made/)).toBeInTheDocument();
  });

  it('rejects a change, and stops a turn', async () => {
    const fetchMock = server({ conversations: [conversation] });
    resetChatStore({ open: true, conversationId: 'c1', agent: 'claude-code', model: 'sonnet' });
    renderWithProviders(<Page>content</Page>);
    await screen.findByLabelText('Your message');
    const source = stream();
    act(() => {
      source.emit({
        seq: 1,
        at,
        type: 'permission',
        requestId: 'r2',
        toolName: 'mcp__mnemarr__delete_note',
        input: { path: 'a.md' },
      });
      source.emit({ seq: 0, at, type: 'status', state: 'running' });
    });

    await userEvent.click(await screen.findByRole('button', { name: 'Reject' }));
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));

    expect(
      sentBody(
        await vi.waitFor(() => findCall(fetchMock, ([url]) => url === '/api/v1/chat/conversations/c1/permissions/r2')),
      ),
    ).toEqual({ approve: false });
    await vi.waitFor(() => findCall(fetchMock, ([url]) => url === '/api/v1/chat/conversations/c1/cancel'));
  });

  it('lists the past conversations, opens one and deletes one', async () => {
    const fetchMock = server({
      conversations: [conversation, { ...conversation, id: 'c2', title: 'Courses', pendingPermissions: 1 }],
    });
    const panel = await openPanel();

    await userEvent.click(within(panel).getByRole('button', { name: 'Past conversations' }));
    const list = await within(panel).findByRole('list', { name: 'Past conversations' });
    expect(within(list).getByText('waiting for your approval')).toBeInTheDocument();
    await userEvent.click(within(list).getByRole('button', { name: /^Courses/ }));
    expect(stream().url).toBe('/api/v1/chat/conversations/c2/events');

    await userEvent.click(within(panel).getByRole('button', { name: 'Past conversations' }));
    await userEvent.click(
      within(await within(panel).findByRole('list', { name: 'Past conversations' })).getByRole('button', {
        name: 'Delete the conversation Backups',
      }),
    );
    await vi.waitFor(() =>
      findCall(
        fetchMock,
        ([url, init]) =>
          url === '/api/v1/chat/conversations/c1' && (init as RequestInit | undefined)?.method === 'DELETE',
      ),
    );
  });

  it('tells a person when the chat could not do what they asked', async () => {
    server({ createFails: true });
    const panel = await openPanel();

    await userEvent.type(within(panel).getByLabelText('Your message'), 'Hello');
    await userEvent.click(within(panel).getByRole('button', { name: 'Send' }));

    expect(await within(panel).findByRole('alert')).toHaveTextContent('the server answered 502');
  });

  it('renders what the model wrote as Markdown, never as HTML', async () => {
    server({ conversations: [conversation] });
    resetChatStore({ open: true, conversationId: 'c1', agent: 'claude-code', model: 'sonnet' });
    renderWithProviders(<Page>content</Page>);
    await screen.findByLabelText('Your message');

    act(() =>
      stream().emit({
        seq: 1,
        at,
        type: 'text',
        text: 'A **bold** word <img src=x onerror=alert(1)> and [a link](https://example.org/page) or [a script](javascript:alert(1)).',
      }),
    );

    const log = await screen.findByRole('log', { name: 'Conversation' });
    expect(within(log).getByText('bold').tagName).toBe('STRONG');
    expect(log.querySelector('img')).toBeNull();
    const link = within(log).getByRole('link', { name: 'a link' });
    expect(link).toHaveAttribute('rel', 'noreferrer noopener');
    expect(link).toHaveAttribute('title', 'https://example.org/page');
    expect(log).toHaveTextContent('a link (example.org)');
    expect(log.querySelector('a[href^="javascript"]')).toBeNull();
  });
});
