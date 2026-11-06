import { Link } from 'react-router-dom';

import translate from 'Utilities/String/translate';

import { RightsChip } from './KindBadge';
import { kindLook, kindOrder } from './kinds';
import { noteAddress, type NoteEntry, type NoteSource } from './notes';
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

/** One source of the tree: its name, what the person may do in it, and its folders and notes. */
function SourceTree({
  source,
  entries,
  selected,
  open,
  filtering,
  tag,
}: Readonly<{
  source: NoteSource;
  entries: ReadonlyArray<NoteEntry>;
  selected: string | null;
  open: Set<string>;
  filtering: boolean;
  tag: string | null;
}>) {
  return (
    <section aria-label={source.name} className={styles.treeSource}>
      <h3 className={styles.sourceName}>
        {source.name} <RightsChip rights={source.rights} />
      </h3>
      {entries.length === 0 ? (
        <p className={styles.hint}>{translate('EmptySource')}</p>
      ) : (
        <Contents
          folder={build(entries)}
          forceOpen={filtering}
          open={open}
          path={source.id}
          selected={selected}
          tag={tag}
        />
      )}
    </section>
  );
}

/**
 * The notes the person may read, grouped by what they are: the memory, the notes written by hand, the documentation of applications. Each kind has
 * its colour and its icon (the same on the note itself), each source says whether it is open for writing, and the folders inside look like a vault.
 */
export default function NoteTree({
  entries,
  sources,
  selected,
  filtering = false,
  tag = null,
}: Readonly<{
  entries: ReadonlyArray<NoteEntry>;
  sources: ReadonlyArray<NoteSource>;
  selected: string | null;
  filtering?: boolean;
  tag?: string | null;
}>) {
  const open = ancestorsOf(selected);
  // While looking for something (a filter, a tag), a source with nothing to show stays out; otherwise every source is there, even one with no note yet.
  const narrowed = filtering || tag !== null;
  const shown = sources.filter((source) => !narrowed || entries.some((entry) => entry.sourceId === source.id));
  if (shown.length === 0) {
    return <p>{translate('NoNotes')}</p>;
  }
  return (
    <nav aria-label={translate('Notes')}>
      {kindOrder
        .filter((kind) => shown.some((source) => source.kind === kind))
        .map((kind) => {
          const look = kindLook[kind];
          const Icon = look.icon;
          const ofKind = shown.filter((source) => source.kind === kind);
          const count = entries.filter((entry) => ofKind.some((source) => source.id === entry.sourceId)).length;
          return (
            <section
              aria-label={translate(look.title)}
              className={`${styles.kindSection} ${styles[look.className] ?? ''}`}
              key={kind}
            >
              <h2 className={styles.kindTitle}>
                <Icon aria-hidden="true" size={18} />
                {translate(look.title)}
                <span className={styles.kindCount}>{translate('NotesCount', { count })}</span>
              </h2>
              <p className={styles.hint}>{translate(look.purpose)}</p>
              {ofKind.map((source) => (
                <SourceTree
                  entries={entries.filter((entry) => entry.sourceId === source.id)}
                  filtering={filtering}
                  key={source.id}
                  open={open}
                  selected={selected}
                  source={source}
                  tag={tag}
                />
              ))}
            </section>
          );
        })}
    </nav>
  );
}
