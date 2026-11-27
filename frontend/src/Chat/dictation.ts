import { apiUpload } from 'Helpers/Api/apiGet';
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

/** Sends a recording and gets its text back; the text is never sent to the chat by the server. */
export async function transcribe(recording: Blob): Promise<string> {
  const extension = EXTENSIONS[recording.type.split(';')[0] ?? ''] ?? 'webm';
  const { text } = await apiUpload<{ text: string }>('/chat/transcriptions', recording, {
    method: 'POST',
    fileName: `recording.${extension}`,
  });
  return text;
}
