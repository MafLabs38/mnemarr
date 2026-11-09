import { icons } from 'Helpers/Props/icons';
import translate from 'Utilities/String/translate';

import { shortToolName } from './chatItems';
import styles from './ChatMessages.module.css';
import formatArguments from './formatArguments';

interface ToolCallBlockProps {
  name: string;
  input: unknown;
  result?: { isError: boolean; summary: string };
}

/** One tool call, collapsed: its name and state; open, its arguments and what it answered (ADR 0035). */
export default function ToolCallBlock({ name, input, result }: Readonly<ToolCallBlockProps>) {
  let state = translate('ToolRunning');
  let stateClass = styles.toolRunning;
  if (result) {
    state = result.isError ? translate('ToolFailed') : translate('ToolDone');
    stateClass = result.isError ? styles.toolFailed : styles.toolDone;
  }

  return (
    <details className={styles.tool}>
      <summary className={styles.toolSummary}>
        <icons.TOOL_CALL aria-hidden="true" size={14} strokeWidth={2.25} />
        <code>{shortToolName(name)}</code>
        <span className={stateClass}>{state}</span>
      </summary>
      <div className={styles.toolBody}>
        <div className={styles.toolLabel}>{translate('ToolArguments')}</div>
        <pre className={styles.pre}>{formatArguments(input)}</pre>
        {result ? (
          <>
            <div className={styles.toolLabel}>{translate('ToolResult')}</div>
            <pre className={styles.pre}>{result.summary || translate('ToolNoOutput')}</pre>
          </>
        ) : null}
      </div>
    </details>
  );
}
