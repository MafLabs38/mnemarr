import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import PromptBox from 'Chat/PromptBox';

// The microphone is replaced by a button that "hears" a sentence: what is tested here is where the text goes.
vi.mock('Chat/DictationButton', () => ({
  default: ({ onText }: { onText: (text: string) => void }) => (
    <button onClick={() => onText('the incubator')} type="button">
      Dictate
    </button>
  ),
}));

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

  it('adds a space after words that end without one', async () => {
    const { field } = renderBox();
    await userEvent.type(field, 'Note:');

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect(field).toHaveValue('Note: the incubator');
  });
});
