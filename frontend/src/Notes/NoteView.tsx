import { Link } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import LoadingIndicator from 'Components/LoadingIndicator';
import { ApiError } from 'Helpers/Api/apiGet';
import translate from 'Utilities/String/translate';

import NoteMarkdown from './NoteMarkdown';
import { noteAddress, useNote } from './notes';
import styles from './Notes.module.css';

/** One note: its title, where it is, its tags, its text, and the notes that link to it. */
export default function NoteView({ sourceId, path }: Readonly<{ sourceId: string; path: string }>) {
  const { data: note, isPending, error } = useNote(sourceId, path);

  if (isPending) {
    return <LoadingIndicator />;
  }
  if (error || !note) {
    return (
      <Alert>
        {error instanceof ApiError && error.status === 404 ? translate('NoteNotFound') : translate('NoteError')}
      </Alert>
    );
  }
  return (
    <article>
      {!/^\s*# /.test(note.markdown) && <h2 className={styles.noteTitle}>{note.title}</h2>}
      <p className={styles.noteWhere}>
        {note.sourceName} · {note.path}
      </p>
      {note.tags.length > 0 && (
        <ul aria-label={translate('Tags')} className={styles.tags}>
          {note.tags.map((tag) => (
            <li key={tag}>
              <Link to={`/notes?tag=${encodeURIComponent(tag)}`}>#{tag}</Link>
            </li>
          ))}
        </ul>
      )}
      <NoteMarkdown note={note} />
      <section aria-label={translate('Backlinks')} className={styles.backlinks}>
        <h3>{translate('Backlinks')}</h3>
        {note.backlinks.length === 0 ? (
          <p className={styles.hint}>{translate('NoBacklinks')}</p>
        ) : (
          <ul>
            {note.backlinks.map((backlink) => (
              <li key={`${backlink.sourceId}/${backlink.path}`}>
                <Link to={noteAddress(backlink)}>{backlink.title}</Link>
                <span className={styles.hint}>
                  {' '}
                  · {backlink.sourceName} · {backlink.path}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </article>
  );
}
