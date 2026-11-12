import { useEffect } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import LoadingIndicator from 'Components/LoadingIndicator';
import { ApiError } from 'Helpers/Api/apiGet';
import translate from 'Utilities/String/translate';

import ConversationTitle from './ConversationTitle';
import { KindBadge } from './KindBadge';
import { kindLook } from './kinds';
import NoteActions from './NoteActions';
import NoteMarkdown from './NoteMarkdown';
import NotePages from './NotePages';
import { noteAddress, noteToRead, useNote } from './notes';
import styles from './Notes.module.css';
import RightsBanner from './RightsBanner';

/** Whether the text of the note already shows its title as a level-one heading (that is where the title comes from). */
function hasOwnTitle(markdown: string, title: string): boolean {
  return markdown.split('\n').some((line) => line.trim() === `# ${title}`);
}

/** The page asked for in the address: a number from 0, or the first page for anything else (the address is typed by whoever sent the link). */
function pageOf(value: string | null): number {
  const page = Number(value);
  return Number.isInteger(page) && page > 0 ? page : 0;
}

/** One note: its title, where it is, its tags, its text, and the notes that link to it. */
export default function NoteView({ sourceId, path }: Readonly<{ sourceId: string; path: string }>) {
  const [params] = useSearchParams();
  const { data: note, isPending, error } = useNote(sourceId, path, pageOf(params.get('page')));
  const { hash } = useLocation();

  // A link to a heading (`[[note#Heading]]`) leads to it, once the note is drawn.
  useEffect(() => {
    if (note && hash.length > 1) {
      try {
        document.getElementById(decodeURIComponent(hash.slice(1)))?.scrollIntoView?.();
      } catch {
        // A fragment that anyone can write, and that is not a valid address (`#%E0%A4%A`): there is nothing to scroll to.
      }
    }
  }, [note, hash]);

  if (noteToRead(sourceId, path) === null) {
    return <Alert>{translate('NoteNotFound')}</Alert>;
  }
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
    <article className={`${styles.noteCard} ${styles[kindLook[note.sourceKind].className] ?? ''}`}>
      <header className={styles.noteHeader}>
        <KindBadge kind={note.sourceKind} />
        <p className={styles.noteWhere}>
          {note.sourceName} · {note.path}
        </p>
      </header>
      {!hasOwnTitle(note.markdown, note.title) && <h2 className={styles.noteTitle}>{note.title}</h2>}
      <RightsBanner note={note} />
      {note.cut && note.rights.reason !== 'note-too-long' && <p role="status">{translate('LongNoteNotice')}</p>}
      <NotePages label={translate('PagesOfTheNote')} note={note} />
      {note.tags.length > 0 && (
        <ul aria-label={translate('Tags')} className={styles.tags}>
          {note.tags.map((tag) => (
            <li key={tag}>
              <Link to={`/notes?tag=${encodeURIComponent(tag)}`}>#{tag}</Link>
            </li>
          ))}
        </ul>
      )}
      {note.sourceKind === 'Conversations' && <ConversationTitle path={note.path} />}
      <NoteActions key={note.version} note={note} />
      <NoteMarkdown note={note} />
      <NotePages label={translate('PagesOfTheNoteEnd')} note={note} />
      <section aria-label={translate('Backlinks')} className={styles.backlinks}>
        <h2 className={styles.backlinksTitle}>{translate('Backlinks')}</h2>
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
        {note.backlinksTotal > note.backlinks.length && (
          <p className={styles.hint}>
            {translate('BacklinksCut', { shown: note.backlinks.length, total: note.backlinksTotal })}
          </p>
        )}
      </section>
    </article>
  );
}
