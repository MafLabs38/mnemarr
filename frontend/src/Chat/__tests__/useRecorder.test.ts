import { act, renderHook } from '@testing-library/react';
import { MAX_RECORDING_SECONDS, useRecorder } from 'Chat/useRecorder';

/** A microphone and a recorder that do what a test says: every track remembers whether it was stopped. */
class FakeRecorder extends EventTarget {
  static last: FakeRecorder | undefined;
  state: 'inactive' | 'recording' = 'inactive';
  mimeType = 'audio/webm';

  constructor() {
    super();
    FakeRecorder.last = this;
  }

  static isTypeSupported = (type: string) => type === 'audio/webm';

  start() {
    this.state = 'recording';
  }

  stop() {
    this.state = 'inactive';
    this.dispatchEvent(
      Object.assign(new Event('dataavailable'), { data: new Blob(['sound'], { type: 'audio/webm' }) }),
    );
    this.dispatchEvent(new Event('stop'));
  }
}

const tracks: Array<{ stop: ReturnType<typeof vi.fn> }> = [];
let grant: (() => void) | undefined;

beforeEach(() => {
  tracks.length = 0;
  FakeRecorder.last = undefined;
  vi.stubGlobal('MediaRecorder', FakeRecorder);
  Object.defineProperty(navigator, 'mediaDevices', {
    configurable: true,
    value: {
      getUserMedia: vi.fn(
        () =>
          new Promise((resolve) => {
            const track = { stop: vi.fn() };
            tracks.push(track);
            grant = () => resolve({ getTracks: () => [track] });
          }),
      ),
    },
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

async function recording(onRecorded = vi.fn()) {
  const hook = renderHook(() => useRecorder(onRecorded));
  let started: Promise<void> | undefined;
  act(() => {
    started = hook.result.current.start();
  });
  await act(async () => {
    grant?.();
    await started;
  });
  return { hook, onRecorded };
}

describe('useRecorder', () => {
  it('hands the recording over on stop, and releases the microphone', async () => {
    const { hook, onRecorded } = await recording();
    expect(hook.result.current.state).toBe('recording');

    act(() => hook.result.current.stop());

    expect(onRecorded).toHaveBeenCalledWith(expect.any(Blob));
    expect(tracks[0]?.stop).toHaveBeenCalled();
    expect(hook.result.current.state).toBe('idle');
  });

  it('keeps nothing on cancel, and releases the microphone', async () => {
    const { hook, onRecorded } = await recording();

    act(() => hook.result.current.cancel());

    expect(onRecorded).not.toHaveBeenCalled();
    expect(tracks[0]?.stop).toHaveBeenCalled();
  });

  it('lets go of the microphone and keeps nothing when the panel closes while it records', async () => {
    const { hook, onRecorded } = await recording();

    hook.unmount();

    expect(onRecorded).not.toHaveBeenCalled();
    expect(tracks[0]?.stop).toHaveBeenCalled();
  });

  it('records nothing when the panel closes while the browser asks for the microphone', async () => {
    const onRecorded = vi.fn();
    const hook = renderHook(() => useRecorder(onRecorded));
    let started: Promise<void> | undefined;
    act(() => {
      started = hook.result.current.start();
    });

    hook.unmount();
    grant?.();
    await started;

    expect(FakeRecorder.last).toBeUndefined();
    expect(tracks[0]?.stop).toHaveBeenCalled();
    expect(onRecorded).not.toHaveBeenCalled();
  });

  it('stops by itself at the longest a recording may be', async () => {
    vi.useFakeTimers();
    const { hook, onRecorded } = await recording();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(MAX_RECORDING_SECONDS * 1000 + 500);
    });

    expect(onRecorded).toHaveBeenCalledOnce();
    expect(hook.result.current.state).toBe('idle');
  });

  it('counts the seconds while it records', async () => {
    vi.useFakeTimers();
    const { hook } = await recording();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_100);
    });

    expect(hook.result.current.elapsed).toBe(3);
  });

  it('releases the microphone and can start again when the browser cannot record what it gave', async () => {
    vi.stubGlobal(
      'MediaRecorder',
      Object.assign(
        function MediaRecorder() {
          throw new DOMException('unsupported', 'NotSupportedError');
        },
        { isTypeSupported: () => false },
      ),
    );
    const hook = renderHook(() => useRecorder(vi.fn()));
    let started: Promise<void> | undefined;
    act(() => {
      started = hook.result.current.start();
    });

    await act(async () => {
      grant?.();
      await expect(started).rejects.toThrow('unsupported');
    });

    expect(tracks[0]?.stop).toHaveBeenCalled();
    expect(hook.result.current.state).toBe('idle');
  });

  it('goes back to idle and says why when the browser does not give the microphone', async () => {
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: vi.fn(() => Promise.reject(new DOMException('denied', 'NotAllowedError'))) },
    });
    const hook = renderHook(() => useRecorder(vi.fn()));

    await act(async () => {
      await expect(hook.result.current.start()).rejects.toThrow('denied');
    });

    expect(hook.result.current.state).toBe('idle');
  });
});
