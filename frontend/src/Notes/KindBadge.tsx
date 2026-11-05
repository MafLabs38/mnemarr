import { icons } from 'Helpers/Props/icons';
import type { SourceKind } from 'Sources/sources';
import translate from 'Utilities/String/translate';

import { kindLook } from './kinds';
import type { NoteRights } from './notes';
import styles from './Notes.module.css';

/** The kind of a note, as a coloured badge with its icon: the same colour as its section in the tree and the frame of the note. */
export function KindBadge({ kind }: Readonly<{ kind: SourceKind }>) {
  const look = kindLook[kind];
  const Icon = look.icon;
  return (
    <span className={`${styles.badge} ${styles[look.className] ?? ''}`}>
      <Icon aria-hidden="true" size={14} />
      {translate(look.title)}
    </span>
  );
}

/** What the person may do in a source, in one small chip: editable, read-only, not written here, or not their role. */
export function RightsChip({ rights }: Readonly<{ rights: NoteRights }>) {
  const Icon = rights.canWrite ? icons.EDITABLE : icons.LOCKED;
  return (
    <span
      className={`${styles.chip} ${rights.canWrite ? styles.chipWritable : styles.chipLocked}`}
      title={translate(`RightsShort_${rights.reason}`)}
    >
      <Icon aria-hidden="true" size={12} />
      {translate(rights.canWrite ? 'ChipEditable' : 'ChipReadOnly')}
    </span>
  );
}
