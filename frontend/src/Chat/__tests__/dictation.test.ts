import { canRecord, recordingType, transcribe } from 'Chat/dictation';

import { ApiError } from 'Helpers/Api/apiGet';

const upload = vi.hoisted(() => vi.fn<(path: string, file: Blob, options: object) => Promise<{ text: string }>>());
vi.mock('Helpers/Api/apiGet', async (actual) => ({ ...(await actual<object>()), apiUpload: upload }));
vi.mock('Helpers/Hooks/useApiQuery', () => ({ useApiQuery: vi.fn() }));

afterEach(() => {
  vi.useRealTimers();
  upload.mockReset();
  vi.unstubAllGlobals();
  // jsdom has no microphone: the one a test gave is taken back, so that no other test sees it.
  Reflect.deleteProperty(navigator, 'mediaDevices');
});

describe('dictation', () => {
  it.each([
    ['audio/mp4;codecs=mp4a', 'recording.m4a'],
    ['audio/webm;codecs=opus', 'recording.webm'],
    ['audio/ogg', 'recording.ogg'],
    ['audio/x-unknown', 'recording.webm'],
  ])('sends a recording of %s as %s, by POST, and gives its text back', async (type, name) => {
    upload.mockResolvedValue({ text: 'the incubator' });

    const text = await transcribe(new Blob(['sound'], { type }));

    expect(text).toBe('the incubator');
    expect(upload).toHaveBeenLastCalledWith('/chat/transcriptions', expect.any(Blob), {
      method: 'POST',
      fileName: name,
    });
  });

  it('keeps a recording while the server is busy for a few seconds, and sends it again', async () => {
    vi.useFakeTimers();
    upload.mockRejectedValueOnce(new ApiError(429, 'Other recordings are being turned into text.', [], 5));
    upload.mockResolvedValueOnce({ text: 'the incubator' });

    const text = transcribe(new Blob(['sound'], { type: 'audio/webm' }));
    await vi.advanceTimersByTimeAsync(5000);

    await expect(text).resolves.toBe('the incubator');
    expect(upload).toHaveBeenCalledTimes(2);
  });

  it.each([
    ['the daily cap', new ApiError(429, 'You have sent 100 recordings in 24 hours.', [], 3600)],
    ['a refusal', new ApiError(422, 'The service refused the recording.')],
  ])('does not send a recording again after %s', async (_, error) => {
    upload.mockRejectedValue(error);

    await expect(transcribe(new Blob(['sound'], { type: 'audio/webm' }))).rejects.toBe(error);
    expect(upload).toHaveBeenCalledTimes(1);
  });

  it('gives up after about half a minute of a busy server', async () => {
    vi.useFakeTimers();
    const busy = new ApiError(429, 'Other recordings are being turned into text.', [], 5);
    upload.mockRejectedValue(busy);

    const text = transcribe(new Blob(['sound'], { type: 'audio/webm' }));
    const outcome = expect(text).rejects.toBe(busy);
    await vi.advanceTimersByTimeAsync(30_000);

    await outcome;
    expect(upload).toHaveBeenCalledTimes(6);
  });

  it('records in the best format the browser has, or lets the browser choose', () => {
    vi.stubGlobal('MediaRecorder', { isTypeSupported: (type: string) => type === 'audio/mp4' });
    expect(recordingType()).toBe('audio/mp4');

    vi.stubGlobal('MediaRecorder', { isTypeSupported: () => false });
    expect(recordingType()).toBeUndefined();
  });

  it('records only on a secure page of a browser that can', () => {
    vi.stubGlobal('MediaRecorder', function MediaRecorder() {});
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia: vi.fn() } });

    vi.stubGlobal('isSecureContext', true);
    expect(canRecord()).toBe(true);

    vi.stubGlobal('isSecureContext', false);
    expect(canRecord()).toBe(false);

    vi.stubGlobal('isSecureContext', true);
    vi.stubGlobal('MediaRecorder', undefined);
    expect(canRecord()).toBe(false);
  });
});
