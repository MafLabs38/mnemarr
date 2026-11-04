import { Link } from 'react-router-dom';

import translate from 'Utilities/String/translate';

import { noteAddress, type NoteEntry } from './notes';
import styles from './Notes.module.css';

interface Folder {
  name: string;
  folders: Map<string, Folder>;
  notes: NoteEntry[];
}

function build(entries: ReadonlyArray<NoteEntry>): Folder {
  const root: Folder = { name: '', folders: new Map(), notes: [] };
  for (const entry of entries) {
    const parts = entry.path.split('/').slice(0, -1);
    let folder = root;
    for (const part of parts) {
      let next = folder.folders.get(part);
      if (!next) {
        next = { name: part, folders: new Map(), notes: [] };
        folder.folders.set(part, next);
      }
      folder = next;
    }
    folder.notes.push(entry);
  }
  return root;
}

function Contents({ folder, selected, path }: Readonly<{ folder: Folder; selected: string | null; path: string }>) {
  return (
    <ul className={styles.tree}>
      {[...folder.folders.values()].map((child) => (
        <li key={`${path}/${child.name}`}>
          <details open={hasSelected(child, selected)}>
            <summary>{child.name}</summary>
            <Contents folder={child} path={`${path}/${child.name}`} selected={selected} />
          </details>
        </li>
      ))}
      {folder.notes.map((note) => (
        <li key={note.path}>
          <Link
            aria-current={selected === `${note.sourceId}/${note.path}` ? 'page' : undefined}
            className={styles.noteLink}
            to={noteAddress(note)}
          >
            {note.title}
          </Link>
        </li>
      ))}
    </ul>
  );
}

function hasSelected(folder: Folder, selected: string | null): boolean {
  return (
    selected !== null &&
    (folder.notes.some((n) => `${n.sourceId}/${n.path}` === selected) ||
      [...folder.folders.values()].some((f) => hasSelected(f, selected)))
  );
}

/** The notes the person may read, as one tree per source and folders inside: what a vault looks like in Obsidian. */
export default function NoteTree({
  entries,
  selected,
}: Readonly<{ entries: ReadonlyArray<NoteEntry>; selected: string | null }>) {
  const grouped = new Map<string, NoteEntry[]>();
  for (const entry of entries) {
    grouped.set(entry.sourceId, [...(grouped.get(entry.sourceId) ?? []), entry]);
  }
  const sources = [...grouped.entries()];
  if (sources.length === 0) {
    return <p>{translate('NoNotes')}</p>;
  }
  return (
    <nav aria-label={translate('Notes')}>
      {sources.map(([sourceId, notes]) => (
        <section aria-label={notes[0]?.sourceName} key={sourceId}>
          <h3 className={styles.sourceName}>{notes[0]?.sourceName}</h3>
          <Contents folder={build(notes)} path={sourceId} selected={selected} />
        </section>
      ))}
    </nav>
  );
}
