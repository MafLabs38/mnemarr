import { canRecord, recordingType, transcribe } from 'Chat/dictation';

const upload = vi.hoisted(() => vi.fn<(path: string, file: Blob, options: object) => Promise<{ text: string }>>());
vi.mock('Helpers/Api/apiGet', () => ({ apiUpload: upload }));
vi.mock('Helpers/Hooks/useApiQuery', () => ({ useApiQuery: vi.fn() }));

afterEach(() => {
  vi.unstubAllGlobals();
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
