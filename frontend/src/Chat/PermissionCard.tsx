import { icons } from 'Helpers/Props/icons';
import { type TranslationKey } from 'Utilities/String/en';
import translate from 'Utilities/String/translate';

import { type PermissionState, shortToolName } from './chatItems';
import styles from './ChatMessages.module.css';
import formatArguments from './formatArguments';

interface PermissionCardProps {
  toolName: string;
  input: unknown;
  state: PermissionState;
  busy: boolean;
  onAnswer: (approve: boolean) => void;
}

const stateText: Record<Exclude<PermissionState, 'pending'>, TranslationKey> = {
  approved: 'ChangeApproved',
  rejected: 'ChangeRejected',
  cancelled: 'ChangeCancelled',
};

/**
 * "Apply this change?" (ADR 0035): a change the agent proposes (a note, a source), with the tool and its arguments, waiting for the person. Nothing is changed before Approve.
 */
export default function PermissionCard({ toolName, input, state, busy, onAnswer }: Readonly<PermissionCardProps>) {
  const title = translate('ApplyThisChange');
  return (
    <section
      aria-label={title}
      className={state === 'pending' ? `${styles.permission} ${styles.pending}` : styles.permission}
    >
      <div className={styles.permissionTitle}>
        <icons.SECRET aria-hidden="true" size={16} strokeWidth={2.25} />
        <span>{title}</span>
        <code className={styles.permissionTool}>{shortToolName(toolName)}</code>
      </div>
      <pre className={styles.pre}>{formatArguments(input)}</pre>
      {state === 'pending' ? (
        <div className={styles.permissionActions}>
          <button className={styles.approve} disabled={busy} onClick={() => onAnswer(true)} type="button">
            <icons.APPROVE aria-hidden="true" size={14} strokeWidth={2.5} />
            {translate('Approve')}
          </button>
          <button className={styles.reject} disabled={busy} onClick={() => onAnswer(false)} type="button">
            <icons.REJECT aria-hidden="true" size={14} strokeWidth={2.5} />
            {translate('Reject')}
          </button>
        </div>
      ) : (
        <div className={state === 'approved' ? styles.approved : styles.rejected}>{translate(stateText[state])}</div>
      )}
    </section>
  );
}
