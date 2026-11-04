import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import TextField from 'Components/Form/TextField';
import LoadingIndicator from 'Components/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import translate from 'Utilities/String/translate';

import { useNoteTree, useTags } from './notes';
import styles from './Notes.module.css';
import NoteTree from './NoteTree';
import NoteView from './NoteView';

/**
 * The notes reader (D-37): the notes a person may read as a tree, and the one they chose rendered. Read-only. The note and the tag
 * are in the address, so that a note can be linked to and the back button works.
 */
export default function Notes() {
  const [params] = useSearchParams();
  const source = params.get('source');
  const path = params.get('path');
  const tag = params.get('tag');
  const [filter, setFilter] = useState('');
  const tree = useNoteTree(tag);
  const tags = useTags();

  const words = filter.trim().toLowerCase();
  const entries = (tree.data?.notes ?? []).filter(
    (entry) => words === '' || `${entry.title} ${entry.path}`.toLowerCase().includes(words),
  );

  return (
    <PageContent title={translate('Notes')}>
      <PageContentBody>
        <PageHeading scope={translate('Notes')} title={tag ? `#${tag}` : translate('Notes')} />
        <div className={styles.layout}>
          <div className={styles.side}>
            <TextField
              label={translate('FilterNotes')}
              onChange={(event) => setFilter(event.target.value)}
              value={filter}
            />
            {tag && (
              <p>
                <Link to="/notes">{translate('AllNotes')}</Link>
              </p>
            )}
            {tree.isPending && <LoadingIndicator />}
            {tree.isError && <Alert>{translate('NotesError')}</Alert>}
            {tree.data && (
              <NoteTree
                entries={entries}
                filtering={words !== ''}
                tag={tag}
                selected={source && path ? `${source}/${path}` : null}
              />
            )}
            {tree.data && tree.data.total > tree.data.notes.length && (
              <p className={styles.hint}>
                {translate('TreeCut', { shown: tree.data.notes.length, total: tree.data.total })}
              </p>
            )}
            {tags.data && tags.data.tags.length > 0 && (
              <section aria-label={translate('Tags')}>
                <h2 className={styles.sourceName}>{translate('Tags')}</h2>
                <ul className={styles.tags}>
                  {tags.data.tags.map((entry) => (
                    <li key={entry.tag}>
                      <Link to={`/notes?tag=${encodeURIComponent(entry.tag)}`}>
                        #{entry.tag} <span className={styles.hint}>{entry.count}</span>
                      </Link>
                    </li>
                  ))}
                </ul>
                {tags.data.total > tags.data.tags.length && (
                  <p className={styles.hint}>
                    {translate('TagsCut', { shown: tags.data.tags.length, total: tags.data.total })}
                  </p>
                )}
              </section>
            )}
          </div>
          <div className={styles.main}>
            {source && path ? (
              <NoteView key={`${source}/${path}`} path={path} sourceId={source} />
            ) : (
              <p>{translate('SelectANote')}</p>
            )}
          </div>
        </div>
      </PageContentBody>
    </PageContent>
  );
}
