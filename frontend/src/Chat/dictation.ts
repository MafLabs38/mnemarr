import { ApiError, apiUpload } from 'Helpers/Api/apiGet';
import { useApiQuery } from 'Helpers/Hooks/useApiQuery';

/** Whether the chat offers the microphone (ADR 0047), and the largest recording the server takes. */
export interface DictationStatus {
  enabled: boolean;
  maxMegabytes: number;
}

/** The server turns it on or off with its settings: asked once a session. */
export function useDictationStatus() {
  return useApiQuery<DictationStatus>('/chat/transcriptions/status', { staleTime: Infinity });
}

/** The formats a browser may record in, best first: the server takes each of them (webm on Chrome and Firefox, mp4 on Safari). */
const RECORDING_TYPES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];

/** The format to record in, or undefined to let the browser choose. */
export function recordingType(): string | undefined {
  return RECORDING_TYPES.find((type) => MediaRecorder.isTypeSupported(type));
}

/** A browser gives the microphone only to a secure page (HTTPS, or localhost) that can record. */
export function canRecord(): boolean {
  return (
    globalThis.isSecureContext &&
    typeof MediaRecorder !== 'undefined' &&
    typeof navigator.mediaDevices?.getUserMedia === 'function'
  );
}

const EXTENSIONS: Record<string, string> = { 'audio/webm': 'webm', 'audio/mp4': 'm4a', 'audio/ogg': 'ogg' };

/** The longest wait the recording is kept for: the server is busy with other recordings for a few seconds (the daily cap asks for hours). */
const SHORT_WAIT_SECONDS = 10;
/** How many times a recording is sent while the server is busy: about half a minute in all. */
const MAX_SENDS = 6;

/** The seconds to wait before sending again, when the server is only busy for a moment; otherwise nothing. */
function shortWait(error: unknown): number | undefined {
  return error instanceof ApiError &&
    error.status === 429 &&
    error.retryAfter !== undefined &&
    error.retryAfter <= SHORT_WAIT_SECONDS
    ? error.retryAfter
    : undefined;
}

/**
 * Sends a recording and gets its text back; the text is never sent to the chat by the server. While the server transcribes other recordings (a 429 for a
 * few seconds), the recording is kept and sent again, so that nobody has to speak twice.
 */
export async function transcribe(recording: Blob): Promise<string> {
  const extension = EXTENSIONS[recording.type.split(';')[0] ?? ''] ?? 'webm';
  const send = async () => {
    const { text } = await apiUpload<{ text: string }>('/chat/transcriptions', recording, {
      method: 'POST',
      fileName: `recording.${extension}`,
    });
    return text;
  };
  for (let sends = 1; sends < MAX_SENDS; sends++) {
    try {
      return await send();
    } catch (error) {
      const seconds = shortWait(error);
      if (seconds === undefined) {
        throw error;
      }
      await new Promise((resolve) => setTimeout(resolve, seconds * 1000));
    }
  }
  return send();
}
