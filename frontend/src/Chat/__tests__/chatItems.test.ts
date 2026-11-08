import chatItems, { shortToolName } from '../chatItems';
import type { ChatEvent } from '../typings';

const at = '2026-11-08T10:00:00Z';

describe('chatItems', () => {
  it('joins the result of a tool to its call and the answer of a person to its card', () => {
    const events: ChatEvent[] = [
      { seq: 1, at, type: 'user', text: 'What do my notes say?' },
      { seq: 2, at, type: 'tool-call', id: 't1', name: 'mcp__mnemarr__search', input: { query: 'backup' } },
      { seq: 3, at, type: 'tool-result', id: 't1', isError: false, summary: 'a note says so' },
      {
        seq: 4,
        at,
        type: 'permission',
        requestId: 'r1',
        toolName: 'mcp__mnemarr__update_note',
        input: { path: 'a.md' },
      },
      { seq: 5, at, type: 'permission-resolved', requestId: 'r1', approved: false, reason: 'rejected' },
      { seq: 6, at, type: 'text', text: 'Done.' },
      { seq: 7, at, type: 'turn-end', isError: false },
    ];

    const items = chatItems(events);

    expect(items.map((item) => item.kind)).toEqual(['user', 'tool', 'permission', 'text', 'turn-end']);
    expect(items[1]).toMatchObject({ result: { isError: false, summary: 'a note says so' } });
    expect(items[2]).toMatchObject({ state: 'rejected' });
  });

  it.each([
    [{ approved: true }, 'approved'],
    [{ approved: false, reason: 'cancelled' }, 'cancelled'],
    [{ approved: false }, 'rejected'],
  ] as const)('tells how a card ended: %j', (answer, state) => {
    const events: ChatEvent[] = [
      { seq: 1, at, type: 'permission', requestId: 'r', toolName: 'mcp__mnemarr__delete_note', input: {} },
      { seq: 2, at, type: 'permission-resolved', requestId: 'r', ...answer },
    ];

    expect(chatItems(events)[0]).toMatchObject({ kind: 'permission', state });
  });

  it('leaves out what is live only and keeps the errors', () => {
    const events: ChatEvent[] = [
      { seq: 1, at, type: 'status', state: 'running' },
      { seq: 1, at, type: 'text-delta', text: 'Hel' },
      { seq: 2, at, type: 'error', message: 'The model kept asking for tools.' },
    ];

    expect(chatItems(events)).toEqual([{ kind: 'error', key: '2', message: 'The model kept asking for tools.' }]);
  });

  it('names a tool without the name of its server', () => {
    expect(shortToolName('mcp__mnemarr__create_note')).toBe('create_note');
    expect(shortToolName('plain')).toBe('plain');
  });
});
