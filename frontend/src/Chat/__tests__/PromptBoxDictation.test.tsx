import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import PromptBox, { maxMessageLength } from 'Chat/PromptBox';

// The microphone is replaced by a button that "hears" a sentence: what is tested here is where the text goes. The first handler it was given is kept, as a
// recording that ended several renders ago keeps it.
const heard = vi.hoisted(() => ({ first: undefined as ((text: string) => void) | undefined }));
vi.mock('Chat/DictationButton', () => ({
  default: ({ onText }: { onText: (text: string) => void }) => {
    heard.first ??= onText;
    return (
      <button onClick={() => onText('the incubator')} type="button">
        Dictate
      </button>
    );
  },
}));

beforeEach(() => {
  heard.first = undefined;
});

function renderBox(onSend = vi.fn()) {
  render(
    <PromptBox
      agent="claude-code"
      agents={[{ id: 'claude-code', name: 'Claude Code', available: true, defaultModel: 'haiku', models: [] }]}
      disabled={false}
      model="haiku"
      onAgentChange={vi.fn()}
      onModelChange={vi.fn()}
      onSend={onSend}
      onStop={vi.fn()}
      running={false}
    />,
  );
  return { field: screen.getByLabelText('Your message'), onSend };
}

describe('PromptBox dictation', () => {
  it('puts what was dictated in an empty field and never sends it by itself', async () => {
    const { field, onSend } = renderBox();

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect(field).toHaveValue('the incubator');
    expect(onSend).not.toHaveBeenCalled();
  });

  it('puts it where the cursor is, with a space on each side', async () => {
    const { field } = renderBox();
    await userEvent.type(field, 'Put this idea in.');
    (field as HTMLTextAreaElement).setSelectionRange(16, 16);

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect(field).toHaveValue('Put this idea in the incubator.');
  });

  it('replaces what is selected', async () => {
    const { field } = renderBox();
    await userEvent.type(field, 'Put X idea');
    (field as HTMLTextAreaElement).setSelectionRange(4, 5);

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect(field).toHaveValue('Put the incubator idea');
  });

  it('adds a space before a word that follows, and leaves the cursor after what was dictated', async () => {
    const { field } = renderBox();
    await userEvent.type(field, 'idea.');
    (field as HTMLTextAreaElement).setSelectionRange(0, 0);

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect(field).toHaveValue('the incubator idea.');
    await waitFor(() => expect((field as HTMLTextAreaElement).selectionStart).toBe('the incubator'.length));
  });

  it('keeps what was typed while the recording was turned into text', async () => {
    const { field } = renderBox();
    await userEvent.type(field, 'Typed meanwhile:');

    heard.first?.('the incubator');

    await waitFor(() => expect(field).toHaveValue('Typed meanwhile: the incubator'));
  });

  it('never makes the message longer than a message may be', async () => {
    const { field } = renderBox();
    fireEvent.change(field, { target: { value: 'a'.repeat(maxMessageLength - 5) } });

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect((field as HTMLTextAreaElement).value).toHaveLength(maxMessageLength);
  });

  it('adds a space after words that end without one', async () => {
    const { field } = renderBox();
    await userEvent.type(field, 'Note:');

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect(field).toHaveValue('Note: the incubator');
  });
});
