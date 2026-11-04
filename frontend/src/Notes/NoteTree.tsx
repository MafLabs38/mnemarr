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

function Contents({
  folder,
  selected,
  path,
  open,
  forceOpen,
  tag,
}: Readonly<{
  folder: Folder;
  selected: string | null;
  path: string;
  open: Set<string>;
  forceOpen: boolean;
  tag: string | null;
}>) {
  return (
    <ul className={styles.tree}>
      {[...folder.folders.values()].map((child) => (
        <li key={`${path}/${child.name}`}>
          <details open={forceOpen || open.has(`${path}/${child.name}`)}>
            <summary>{child.name}</summary>
            <Contents
              folder={child}
              forceOpen={forceOpen}
              open={open}
              tag={tag}
              path={`${path}/${child.name}`}
              selected={selected}
            />
          </details>
        </li>
      ))}
      {folder.notes.map((note) => (
        <li key={note.path}>
          <Link
            aria-current={selected === `${note.sourceId}/${note.path}` ? 'page' : undefined}
            className={styles.noteLink}
            to={noteAddress(note, tag)}
          >
            {note.title}
          </Link>
        </li>
      ))}
    </ul>
  );
}

/** The folders on the way to the chosen note (as `source/folder/subfolder`): the ones that are open when the tree is drawn. */
function ancestorsOf(selected: string | null): Set<string> {
  const open = new Set<string>();
  const parts = selected?.split('/') ?? [];
  for (let end = 1; end < parts.length; end++) {
    open.add(parts.slice(0, end).join('/'));
  }
  return open;
}

/** The notes the person may read, as one tree per source and folders inside: what a vault looks like in Obsidian. */
export default function NoteTree({
  entries,
  selected,
  filtering = false,
  tag = null,
}: Readonly<{ entries: ReadonlyArray<NoteEntry>; selected: string | null; filtering?: boolean; tag?: string | null }>) {
  const grouped = new Map<string, NoteEntry[]>();
  for (const entry of entries) {
    const list = grouped.get(entry.sourceId) ?? [];
    list.push(entry);
    grouped.set(entry.sourceId, list);
  }
  const sources = [...grouped.entries()];
  const open = ancestorsOf(selected);
  if (sources.length === 0) {
    return <p>{translate('NoNotes')}</p>;
  }
  return (
    <nav aria-label={translate('Notes')}>
      {sources.map(([sourceId, notes]) => (
        <section aria-label={notes[0]?.sourceName} key={sourceId}>
          <h2 className={styles.sourceName}>{notes[0]?.sourceName}</h2>
          <Contents
            folder={build(notes)}
            forceOpen={filtering}
            open={open}
            tag={tag}
            path={sourceId}
            selected={selected}
          />
        </section>
      ))}
    </nav>
  );
}
