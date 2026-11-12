import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import { useRewriteAllTitles, useTitleCounts } from 'Notes/conversationTitles';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import styles from './Machines.module.css';

/** How the conversations are titled, and, where a model writes titles, the way to have it write them again. */
export default function TitlesCard() {
  const { can } = usePermissions();
  const counts = useTitleCounts();
  const rewrite = useRewriteAllTitles();
  if (!counts.data) {
    return null;
  }
  const { modelEnabled, firstSentence, summary, person, sender, lastError, lastErrorAt } = counts.data;
  return (
    <>
      <p>{translate('TitleCounts', { summary, person, sender, firstSentence })}</p>
      {modelEnabled ? (
        <>
          <p className={styles.hint}>{translate('TitlesModelOn')}</p>
          {lastError && (
            <Alert>{translate('TitlesLastError', { error: lastError, at: formatDateTime(lastErrorAt) })}</Alert>
          )}
          {rewrite.error && <Alert>{rewrite.error.message}</Alert>}
          {rewrite.data && <output>{translate('TitlesQueued', { count: rewrite.data.queued })}</output>}
          {can(permissions.conversationsWrite) && (
            <Button disabled={rewrite.isPending} onClick={() => rewrite.mutate()}>
              {translate('RewriteAllTitles')}
            </Button>
          )}
        </>
      ) : (
        <p className={styles.hint}>{translate('TitlesModelOff')}</p>
      )}
    </>
  );
}
