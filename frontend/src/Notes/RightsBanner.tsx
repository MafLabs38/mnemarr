import { Link } from 'react-router-dom';

import { icons } from 'Helpers/Props/icons';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import type { Note, RightsReason } from './notes';
import styles from './Notes.module.css';

const toneOf: Record<RightsReason, string> = {
  writable: 'toneAllowed',
  'source-read-only': 'toneLocked',
  'kind-not-writable': 'toneInfo',
  'no-permission': 'toneDenied',
};

/**
 * What the person may do to this note, said at once and in words: a coloured strip with the verdict, the reason, and, behind "Why?", the
 * rule of the business that decides it. The server decides (`rights`); this only explains, so that nobody has to guess why a button is grey.
 */
export default function RightsBanner({ note }: Readonly<{ note: Note }>) {
  const { can } = usePermissions();
  const { reason, role, canWrite } = note.rights;
  const Icon = canWrite ? icons.ALLOWED : icons.LOCKED;
  return (
    <section aria-label={translate('NoteRights')} className={`${styles.rights} ${styles[toneOf[reason]] ?? ''}`}>
      <Icon aria-hidden="true" className={styles.rightsIcon} size={22} />
      <div className={styles.rightsBody}>
        <p className={styles.rightsTitle}>{translate(`BannerTitle_${reason}`)}</p>
        <p>{translate(`BannerText_${reason}`, { role, source: note.sourceName })}</p>
        {canWrite && note.sourceKind === 'Memory' && <p className={styles.hint}>{translate('MemoryWriteNote')}</p>}
        {reason === 'source-read-only' &&
          (can(permissions.sourcesManage) ? (
            <p>
              <Link to="/sources">{translate('OpenSourcesToWrite')}</Link>
            </p>
          ) : (
            <p className={styles.hint}>{translate('AskWhoManagesSources')}</p>
          ))}
        <details className={styles.why}>
          <summary>
            <icons.INFO aria-hidden="true" size={14} /> {translate('WhyThisRule')}
          </summary>
          <p>{translate(`BannerWhy_${reason}`)}</p>
        </details>
      </div>
    </section>
  );
}
