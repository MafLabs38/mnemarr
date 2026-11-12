import { Link } from 'react-router-dom';

import translate from 'Utilities/String/translate';

import type { Note } from './notes';
import styles from './Notes.module.css';

/** The address of a page of a note. The first page has no `page`, so that the plain address of a note is still the note. */
function pageAddress(note: Note, page: number) {
  const base = `/notes?source=${encodeURIComponent(note.sourceId)}&path=${encodeURIComponent(note.path)}`;
  return page > 0 ? `${base}&page=${page}` : base;
}

/**
 * A note that is longer than a page comes in pages (the server cuts them at line ends): which one is shown, and the way to the others. A conversation can be
 * many pages long, and its latest part is at its end, so the last page is one click away.
 */
export default function NotePages({ note, label }: Readonly<{ note: Note; label: string }>) {
  if (note.pages <= 1) {
    return null;
  }
  const last = note.pages - 1;
  return (
    <nav aria-label={label} className={styles.pages}>
      {note.page > 0 ? (
        <Link to={pageAddress(note, 0)}>{translate('FirstPage')}</Link>
      ) : (
        <span>{translate('FirstPage')}</span>
      )}
      {note.page > 0 ? (
        <Link to={pageAddress(note, note.page - 1)}>{translate('PreviousPage')}</Link>
      ) : (
        <span>{translate('PreviousPage')}</span>
      )}
      <span aria-current="page">{translate('PageOf', { page: note.page + 1, pages: note.pages })}</span>
      {note.page < last ? (
        <Link to={pageAddress(note, note.page + 1)}>{translate('NextPage')}</Link>
      ) : (
        <span>{translate('NextPage')}</span>
      )}
      {note.page < last ? (
        <Link to={pageAddress(note, last)}>{translate('LastPage')}</Link>
      ) : (
        <span>{translate('LastPage')}</span>
      )}
    </nav>
  );
}
