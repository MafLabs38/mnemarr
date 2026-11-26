import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import DictationButton from 'Chat/DictationButton';

import { ApiError } from 'Helpers/Api/apiGet';

const state = vi.hoisted(() => ({
  status: { enabled: true, maxMegabytes: 1 } as { enabled: boolean; maxMegabytes: number } | undefined,
  canRecord: true,
  recorder: { state: 'idle' as 'idle' | 'starting' | 'recording', elapsed: 0 },
  start: vi.fn<() => Promise<void>>(),
  stop: vi.fn(),
  cancel: vi.fn(),
  onRecorded: undefined as ((recording: Blob) => void) | undefined,
  transcribe: vi.fn<(recording: Blob) => Promise<string>>(),
}));

vi.mock('Chat/dictation', () => ({
  useDictationStatus: () => ({ data: state.status }),
  canRecord: () => state.canRecord,
  transcribe: (recording: Blob) => state.transcribe(recording),
}));

vi.mock('Chat/useRecorder', () => ({
  useRecorder: (onRecorded: (recording: Blob) => void) => {
    state.onRecorded = onRecorded;
    return { ...state.recorder, start: state.start, stop: state.stop, cancel: state.cancel };
  },
}));

const recording = new Blob([new Uint8Array(16)], { type: 'audio/webm' });

beforeEach(() => {
  state.status = { enabled: true, maxMegabytes: 1 };
  state.canRecord = true;
  state.recorder = { state: 'idle', elapsed: 0 };
  state.start.mockReset().mockResolvedValue(undefined);
  state.transcribe.mockReset();
});

describe('DictationButton', () => {
  it('is not there when dictation is off or the page cannot use the microphone', () => {
    state.status = { enabled: false, maxMegabytes: 1 };
    const { rerender } = render(<DictationButton disabled={false} onText={vi.fn()} />);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();

    state.status = { enabled: true, maxMegabytes: 1 };
    state.canRecord = false;
    rerender(<DictationButton disabled={false} onText={vi.fn()} />);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('starts recording on a tap', async () => {
    render(<DictationButton disabled={false} onText={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect(state.start).toHaveBeenCalledOnce();
  });

  it('shows the time while it records, and stops or drops the recording', async () => {
    state.recorder = { state: 'recording', elapsed: 75 };
    render(<DictationButton disabled={false} onText={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: 'Stop 1:15' }));
    await userEvent.click(screen.getByRole('button', { name: 'Drop the recording' }));

    expect(state.stop).toHaveBeenCalledOnce();
    expect(state.cancel).toHaveBeenCalledOnce();
  });

  it('hands the text of a recording over', async () => {
    state.transcribe.mockResolvedValue('Put this idea in the incubator.');
    const onText = vi.fn();
    render(<DictationButton disabled={false} onText={onText} />);

    state.onRecorded?.(recording);

    await waitFor(() => expect(onText).toHaveBeenCalledWith('Put this idea in the incubator.'));
    expect(state.transcribe).toHaveBeenCalledWith(recording);
  });

  it('says why when the server refuses, in its words', async () => {
    state.transcribe.mockRejectedValue(new ApiError(502, 'The transcription service refused the key.'));
    render(<DictationButton disabled={false} onText={vi.fn()} />);

    state.onRecorded?.(recording);

    expect(await screen.findByRole('alert')).toHaveTextContent('The transcription service refused the key.');
  });

  it('says that the browser did not give the microphone', async () => {
    state.start.mockRejectedValue(new DOMException('denied', 'NotAllowedError'));
    render(<DictationButton disabled={false} onText={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: 'Dictate' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('did not give the microphone');
  });

  it('does not send a recording larger than the server takes', async () => {
    render(<DictationButton disabled={false} onText={vi.fn()} />);

    state.onRecorded?.(new Blob([new Uint8Array(1024 * 1024 + 1)], { type: 'audio/webm' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('too long');
    expect(state.transcribe).not.toHaveBeenCalled();
  });

  it('says that nothing was heard', async () => {
    state.transcribe.mockResolvedValue('');
    const onText = vi.fn();
    render(<DictationButton disabled={false} onText={onText} />);

    state.onRecorded?.(recording);

    expect(await screen.findByRole('alert')).toHaveTextContent('Nothing was heard');
    expect(onText).not.toHaveBeenCalled();
  });
});
