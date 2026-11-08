import { type KeyboardEvent, useId, useState } from 'react';

import { icons } from 'Helpers/Props/icons';
import translate from 'Utilities/String/translate';

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
 * The prompt (D-90): a textarea that takes typing and pasting (dictation tools paste), several lines; Enter sends,
 * Shift+Enter breaks the line. Agent and model under it; Stop while a turn runs.
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
  const id = useId();
  const models = agents.find((option) => option.id === agent)?.models ?? [];
  const canSend = !running && !disabled && text.trim().length > 0;

  const send = () => {
    if (canSend) {
      onSend(text);
      setText('');
    }
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
