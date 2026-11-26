import { type KeyboardEvent, useId, useRef, useState } from 'react';

import { icons } from 'Helpers/Props/icons';
import translate from 'Utilities/String/translate';

import DictationButton from './DictationButton';
import styles from './PromptBox.module.css';
import type { ChatAgent } from './typings';

export const maxMessageLength = 20_000;

interface PromptBoxProps {
  agents: ChatAgent[];
  agent: string;
  model: string;
  running: boolean;
  disabled: boolean;
  onAgentChange: (agent: string) => void;
  onModelChange: (model: string) => void;
  onSend: (text: string) => void;
  onStop: () => void;
}

/**
 * The prompt (ADR 0035): a textarea that takes typing and pasting (dictation tools paste), several lines; Enter sends,
 * Shift+Enter breaks the line. Agent and model under it; Stop while a turn runs. The microphone (ADR 0047) puts what was said where the cursor is,
 * and never sends it: the person reads it first.
 */
export default function PromptBox({
  agents,
  agent,
  model,
  running,
  disabled,
  onAgentChange,
  onModelChange,
  onSend,
  onStop,
}: Readonly<PromptBoxProps>) {
  const [text, setText] = useState('');
  const field = useRef<HTMLTextAreaElement>(null);
  const id = useId();
  const models = agents.find((option) => option.id === agent)?.models ?? [];
  const canSend = !running && !disabled && text.trim().length > 0;

  const send = () => {
    if (canSend) {
      onSend(text);
      setText('');
    }
  };

  /** What was dictated goes where the cursor is (or at the end), with a space between it and the words around it. */
  // The text comes back seconds after the recording, and the person may have typed meanwhile: it goes into the field as it is now (the field's own value, not the
  // text of the render the recording ended in), at the cursor as it is now.
  const insert = (dictated: string) => {
    const area = field.current;
    const current = area?.value ?? text;
    const start = area?.selectionStart ?? current.length;
    const end = area?.selectionEnd ?? current.length;
    const before = current.slice(0, start);
    const after = current.slice(end);
    const left = before.length > 0 && !/\s$/.test(before) ? ' ' : '';
    // Before a word, a space; before punctuation or a space, none.
    const right = /^[\p{L}\p{N}]/u.test(after) ? ' ' : '';
    const next = `${before}${left}${dictated}${right}${after}`.slice(0, maxMessageLength);
    setText(next);
    const cursor = Math.min(before.length + left.length + dictated.length, next.length);
    requestAnimationFrame(() => {
      area?.focus();
      area?.setSelectionRange(cursor, cursor);
    });
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      send();
    }
  };

  return (
    <form
      className={styles.prompt}
      onSubmit={(event) => {
        event.preventDefault();
        send();
      }}
    >
      <label className={styles.hidden} htmlFor={`${id}-text`}>
        {translate('ChatPrompt')}
      </label>
      <textarea
        className={styles.textarea}
        id={`${id}-text`}
        maxLength={maxMessageLength}
        onChange={(event) => setText(event.target.value)}
        onKeyDown={onKeyDown}
        placeholder={translate('ChatPromptPlaceholder')}
        ref={field}
        rows={3}
        value={text}
      />
      <div className={styles.bar}>
        <label className={styles.hidden} htmlFor={`${id}-agent`}>
          {translate('Agent')}
        </label>
        <select
          className={styles.select}
          disabled={running || agents.length < 2}
          id={`${id}-agent`}
          onChange={(event) => onAgentChange(event.target.value)}
          value={agent}
        >
          {agents.map((option) => (
            <option disabled={!option.available} key={option.id} value={option.id}>
              {option.name}
            </option>
          ))}
        </select>
        <label className={styles.hidden} htmlFor={`${id}-model`}>
          {translate('Model')}
        </label>
        <select
          className={styles.select}
          disabled={running}
          id={`${id}-model`}
          onChange={(event) => onModelChange(event.target.value)}
          value={model}
        >
          {models.map((option) => (
            <option key={option.id} value={option.id}>
              {option.label}
            </option>
          ))}
        </select>
        <DictationButton disabled={disabled} onText={insert} />
        {running ? (
          <button className={styles.stop} onClick={onStop} type="button">
            <icons.STOP aria-hidden="true" size={14} strokeWidth={2.5} />
            {translate('Stop')}
          </button>
        ) : (
          <button className={styles.send} disabled={!canSend} type="submit">
            <icons.SEND aria-hidden="true" size={14} strokeWidth={2.5} />
            {translate('Send')}
          </button>
        )}
      </div>
    </form>
  );
}
