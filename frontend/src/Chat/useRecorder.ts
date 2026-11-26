import { useCallback, useEffect, useRef, useState } from 'react';

import { recordingType } from './dictation';

/** Where a recording stands: nothing yet, the microphone is being asked for, it records. */
export type RecorderState = 'idle' | 'starting' | 'recording';

/** A recording stops by itself after this long: the server takes at most a few megabytes, and a forgotten microphone must not stay open. */
export const MAX_RECORDING_SECONDS = 10 * 60;

interface Recorder {
  state: RecorderState;
  /** Seconds since the recording began. */
  elapsed: number;
  /** Asks for the microphone and records; throws when the person refuses it or there is none. */
  start: () => Promise<void>;
  /** Stops and hands the recording to `onRecorded`. */
  stop: () => void;
  /** Stops and drops what was recorded. */
  cancel: () => void;
}

/**
 * Records the microphone in the browser (`MediaRecorder`), in the format the browser gives. The sound stays in memory until it is handed over;
 * the microphone is released as soon as the recording ends, whatever ends it (stop, cancel, a page that closes the panel).
 */
export function useRecorder(onRecorded: (recording: Blob) => void): Recorder {
  const [state, setState] = useState<RecorderState>('idle');
  const [elapsed, setElapsed] = useState(0);
  const recorder = useRef<MediaRecorder | null>(null);
  const keep = useRef(true);
  const timer = useRef<ReturnType<typeof setInterval> | null>(null);
  const handOver = useRef(onRecorded);
  useEffect(() => {
    handOver.current = onRecorded;
  }, [onRecorded]);

  const end = useCallback((keepIt: boolean) => {
    keep.current = keepIt;
    if (recorder.current?.state === 'recording') {
      recorder.current.stop();
    }
  }, []);

  const start = useCallback(async () => {
    setState('starting');
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (error) {
      setState('idle');
      throw error;
    }

    const type = recordingType();
    const media = new MediaRecorder(stream, type ? { mimeType: type } : undefined);
    const chunks: Blob[] = [];
    media.addEventListener('dataavailable', (event) => {
      if (event.data.size > 0) {
        chunks.push(event.data);
      }
    });
    media.addEventListener('stop', () => {
      for (const track of stream.getTracks()) {
        track.stop();
      }
      if (timer.current) {
        clearInterval(timer.current);
        timer.current = null;
      }
      recorder.current = null;
      setState('idle');
      if (keep.current && chunks.length > 0) {
        handOver.current(new Blob(chunks, { type: media.mimeType || type || 'audio/webm' }));
      }
    });

    recorder.current = media;
    keep.current = true;
    const began = Date.now();
    setElapsed(0);
    timer.current = setInterval(() => {
      const seconds = Math.floor((Date.now() - began) / 1000);
      setElapsed(seconds);
      if (seconds >= MAX_RECORDING_SECONDS) {
        media.stop();
      }
    }, 250);
    media.start();
    setState('recording');
  }, []);

  // A panel that closes while it records lets go of the microphone and keeps nothing.
  useEffect(() => () => end(false), [end]);

  return { state, elapsed, start, stop: () => end(true), cancel: () => end(false) };
}
