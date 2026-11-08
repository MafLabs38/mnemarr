import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import { type ConsolidationState, useConsolidateNow, useConsolidation } from './consolidation';
import styles from './Sources.module.css';

const notReady: Partial<Record<ConsolidationState, Parameters<typeof translate>[0]>> = {
  'no-runner': 'ConsolidationNoRunner',
  'no-memory-vault': 'ConsolidationNoMemoryVault',
  'memory-vault-read-only': 'ConsolidationMemoryVaultReadOnly',
};

/** How the conversations become notes of the memory vault: on or off, what waits, what is done, what failed, and a way to run it now (ADR 0036). */
export default function ConsolidationCard() {
  const { can } = usePermissions();
  const status = useConsolidation(can(permissions.systemRead));
  const consolidate = useConsolidateNow();
  if (!status.data) {
    return null;
  }
  const {
    enabled,
    state,
    model,
    memoryVault,
    waiting,
    done,
    notes,
    today,
    perDay,
    lastFailure,
    lastFailureAt,
    problem,
  } = status.data;
  if (!enabled) {
    return <p className={styles.hint}>{translate('ConsolidationOff')}</p>;
  }
  const reason = notReady[state];
  return (
    <>
      <p>{translate('ConsolidationCounts', { waiting, done, notes, today, perDay })}</p>
      <p className={styles.hint}>{translate('ConsolidationOn', { model, vault: memoryVault ?? '' })}</p>
      {reason && <Alert>{translate(reason)}</Alert>}
      {problem && <Alert>{problem}</Alert>}
      {lastFailure && (
        <Alert>
          {translate('ConsolidationLastFailure', { error: lastFailure, at: formatDateTime(lastFailureAt) })}
        </Alert>
      )}
      {consolidate.error && <Alert>{consolidate.error.message}</Alert>}
      {consolidate.isSuccess && <output>{translate('ConsolidationStarted')}</output>}
      {state === 'running' && <output>{translate('ConsolidationRunning')}</output>}
      {can(permissions.sourcesManage) && state === 'ready' && (
        <Button disabled={consolidate.isPending || waiting === 0} onClick={() => consolidate.mutate()}>
          {translate('ConsolidateNow')}
        </Button>
      )}
    </>
  );
}
