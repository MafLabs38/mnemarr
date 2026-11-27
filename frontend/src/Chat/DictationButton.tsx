import { useState } from 'react';

import { ApiError } from 'Helpers/Api/apiGet';
import { icons } from 'Helpers/Props/icons';
import translate from 'Utilities/String/translate';

import { canRecord, transcribe, useDictationStatus } from './dictation';
import styles from './DictationButton.module.css';
import { useRecorder } from './useRecorder';

interface DictationButtonProps {
  disabled: boolean;
  /** The text of a recording, to put in the field: it is never sent by itself. */
  onText: (text: string) => void;
}

function formatElapsed(seconds: number): string {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

function reasonOf(error: unknown): string {
  if (error instanceof DOMException && (error.name === 'NotAllowedError' || error.name === 'SecurityError')) {
    return translate('DictationMicrophoneRefused');
  }
  if (error instanceof DOMException && error.name === 'NotFoundError') {
    return translate('DictationNoMicrophone');
  }
  return error instanceof ApiError ? error.message : translate('DictationFailed');
}

/**
 * The chat's microphone (ADR 0047): a tap records, a second tap stops and sends the sound to Mnemarr, which gives the text back; a cross drops the
 * recording. Shown only when the server turned dictation on and the page may use the microphone (HTTPS).
 */
export default function DictationButton({ disabled, onText }: Readonly<DictationButtonProps>) {
  const { data } = useDictationStatus();
  const [sending, setSending] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const maxBytes = (data?.maxMegabytes ?? 0) * 1024 * 1024;

  const recorder = useRecorder((recording) => {
    if (recording.size > maxBytes) {
      setProblem(translate('DictationTooLong'));
      return;
    }
    setSending(true);
    transcribe(recording)
      .then((text) => {
        if (text.length > 0) {
          onText(text);
        } else {
          setProblem(translate('DictationNothingHeard'));
        }
      })
      .catch((error: unknown) => setProblem(reasonOf(error)))
      .finally(() => setSending(false));
  });

  if (!data?.enabled || !canRecord()) {
    return null;
  }

  const begin = () => {
    setProblem(null);
    recorder.start().catch((error: unknown) => setProblem(reasonOf(error)));
  };

  if (recorder.state === 'recording') {
    return (
      <span className={styles.recording}>
        <button className={styles.stopRecording} onClick={recorder.stop} type="button">
          <span aria-hidden="true" className={styles.dot} />
          {translate('DictationStop', { elapsed: formatElapsed(recorder.elapsed) })}
        </button>
        <button
          aria-label={translate('DictationCancel')}
          className={styles.cancel}
          onClick={recorder.cancel}
          title={translate('DictationCancel')}
          type="button"
        >
          <icons.CLOSE aria-hidden="true" size={14} strokeWidth={2.5} />
        </button>
      </span>
    );
  }

  return (
    <>
      <button
        aria-label={sending ? translate('DictationSending') : translate('Dictate')}
        className={styles.mic}
        disabled={disabled || sending || recorder.state === 'starting'}
        onClick={begin}
        title={sending ? translate('DictationSending') : translate('Dictate')}
        type="button"
      >
        {sending ? (
          <icons.LOADING aria-hidden="true" className={styles.spin} size={16} strokeWidth={2.5} />
        ) : (
          <icons.MIC aria-hidden="true" size={16} strokeWidth={2.5} />
        )}
      </button>
      {problem && (
        <span className={styles.problem} role="alert">
          {problem}
        </span>
      )}
    </>
  );
}
