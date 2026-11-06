import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { findCall, renderWithProviders, sentBody, stubApi } from '__tests__/render';
import TitlesCard from 'Machines/TitlesCard';
import ConversationTitle from 'Notes/ConversationTitle';
import type { ConversationTitleItem, TitleCounts } from 'Notes/conversationTitles';
import type { Session } from 'Session/session';

const info = (over: Partial<ConversationTitleItem> = {}): ConversationTitleItem => ({
  path: 'pc/app/2026-11-04-s1.md',
  title: 'How do I rotate the tokens?',
  source: 'first-sentence',
  summary: null,
  modelEnabled: true,
  ...over,
});

function server(item: ConversationTitleItem, counts?: TitleCounts) {
  return stubApi((method, path) => {
    if (method === 'GET' && path.startsWith('/conversations/title?')) {
      return { body: item };
    }
    if (method === 'GET' && path === '/conversations/titles') {
      return { body: counts };
    }
    if (method === 'PUT') {
      return { body: { ...item, title: 'Mine', source: 'person' } };
    }
    if (path === '/conversations/titles/rewrite') {
      return { status: 202, body: { queued: 4 } };
    }
    return { body: { ...item, title: "The model's title", source: 'summary' } };
  });
}

const reader: Session = {
  accountId: 'a2',
  name: 'chat-runner',
  kind: 'Service',
  role: 'Assistant',
  permissions: ['conversations.read', 'notes.read'],
};

describe('ConversationTitle', () => {
  it('shows the title and where it comes from', async () => {
    server(info({ source: 'summary' }));

    renderWithProviders(<ConversationTitle path="pc/app/2026-11-04-s1.md" />);

    expect(await screen.findByText('How do I rotate the tokens?')).toBeInTheDocument();
    expect(screen.getByText(/written by a model/)).toBeInTheDocument();
  });

  it('lets a person who may write conversations type a title of their own', async () => {
    const fetchMock = server(info());
    renderWithProviders(<ConversationTitle path="pc/app/2026-11-04-s1.md" />);

    await userEvent.click(await screen.findByRole('button', { name: 'Change the title' }));
    const field = screen.getByRole('textbox', { name: 'Title' });
    await userEvent.clear(field);
    await userEvent.type(field, 'Mine');
    await userEvent.click(screen.getByRole('button', { name: 'Save the title' }));

    const call = findCall(
      fetchMock,
      ([url, init]) => url === '/api/v1/conversations/title' && (init as RequestInit | undefined)?.method === 'PUT',
    );
    expect(sentBody(call)).toEqual({ path: 'pc/app/2026-11-04-s1.md', title: 'Mine' });
  });

  it('has the model write the title again, and offers it only where a model is turned on', async () => {
    const fetchMock = server(info());
    const { unmount } = renderWithProviders(<ConversationTitle path="pc/app/2026-11-04-s1.md" />);

    await userEvent.click(await screen.findByRole('button', { name: 'Rewrite with the model' }));

    expect(findCall(fetchMock, ([url]) => url === '/api/v1/conversations/title/rewrite')).toBeDefined();
    unmount();

    server(info({ modelEnabled: false }));
    renderWithProviders(<ConversationTitle path="pc/app/2026-11-04-s1.md" />);
    expect(await screen.findByRole('button', { name: 'Change the title' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rewrite with the model' })).not.toBeInTheDocument();
  });

  it('shows the title to a reader of conversations and offers nothing to change it', async () => {
    server(info({ source: 'person' }));

    renderWithProviders(<ConversationTitle path="pc/app/2026-11-04-s1.md" />, '/', reader);

    expect(await screen.findByText(/typed by a person/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});

describe('TitlesCard', () => {
  const counts: TitleCounts = { modelEnabled: true, firstSentence: 30, summary: 5, person: 1 };

  it('counts the titles by origin, and has the model write the first sentences again', async () => {
    const fetchMock = server(info(), counts);
    renderWithProviders(<TitlesCard />);

    expect(
      await screen.findByText('5 written by a model, 1 typed by a person, 30 with only the first sentence.'),
    ).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', { name: 'Have the model write the titles that are only a first sentence' }),
    );

    expect(findCall(fetchMock, ([url]) => url === '/api/v1/conversations/titles/rewrite')).toBeDefined();
    expect(
      await screen.findByText('4 conversation(s) are queued: the model writes their titles in the background.'),
    ).toBeInTheDocument();
  });

  it('says that no model writes titles, and what that means, when it is off', async () => {
    server(info(), { ...counts, modelEnabled: false });
    renderWithProviders(<TitlesCard />);

    expect(await screen.findByText(/No model writes titles on this Mnemarr/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('offers nothing to change to a reader of conversations', async () => {
    server(info(), counts);
    renderWithProviders(<TitlesCard />, '/', reader);

    expect(await screen.findByText(/written by a model/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});
