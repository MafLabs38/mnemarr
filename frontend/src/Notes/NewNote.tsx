import { type FormEvent, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import { noteAddress, type NoteSource, useCreateNote } from './notes';
import styles from './Notes.module.css';

/** The title a new note starts with: the name of its file, without the folder and the extension. */
function titleOf(path: string): string {
  const name = path.split('/').pop() ?? '';
  return name.replace(/\.(md|markdown)$/iu, '');
}

function Form({ sources, onCreated }: Readonly<{ sources: ReadonlyArray<NoteSource>; onCreated: () => void }>) {
  const navigate = useNavigate();
  const [sourceId, setSourceId] = useState(sources[0]?.id ?? '');
  const [path, setPath] = useState('');
  const create = useCreateNote(sourceId);

  function submit(event: FormEvent) {
    event.preventDefault();
    const clean = path.trim();
    create.mutate(
      { path: clean, content: `# ${titleOf(clean)}\n\n` },
      {
        onSuccess: (result) => {
          setPath('');
          onCreated();
          navigate(noteAddress({ sourceId, path: result.path }), { state: { outcome: { git: result.git } } });
        },
      },
    );
  }

  return (
    <form aria-label={translate('NewNote')} className={styles.form} onSubmit={submit}>
      {create.error && <Alert>{create.error.message}</Alert>}
      <SelectField
        label={translate('NewNoteSource')}
        onChange={(event) => setSourceId(event.target.value)}
        options={sources.map((source) => ({ value: source.id, label: source.name }))}
        value={sourceId}
      />
      <TextField
        label={translate('NewNotePath')}
        onChange={(event) => setPath(event.target.value)}
        required
        value={path}
      />
      <p className={styles.hint}>{translate('NewNotePathHint')}</p>
      <Button disabled={create.isPending} type="submit">
        {translate('CreateNote')}
      </Button>
    </form>
  );
}

/**
 * Starts a note in a source that is open for writing. When there is none, it says so and says what to do, instead of disappearing: a missing
 * button that nobody explains is what made the notes look read-only for no reason.
 */
export default function NewNote({ sources }: Readonly<{ sources: ReadonlyArray<NoteSource> }>) {
  const { can } = usePermissions();
  const [open, setOpen] = useState(false);
  const writable = sources.filter((source) => source.rights.canWrite);
  return (
    <details onToggle={(event) => setOpen(event.currentTarget.open)} open={open}>
      <summary>{translate('NewNote')}</summary>
      {writable.length > 0 ? (
        <Form onCreated={() => setOpen(false)} sources={writable} />
      ) : (
        <p className={styles.hint}>
          {translate('NoWritableSource')}{' '}
          {can(permissions.sourcesManage) ? (
            <Link to="/sources">{translate('OpenSourcesToWrite')}</Link>
          ) : (
            translate('AskWhoManagesSources')
          )}
        </p>
      )}
    </details>
  );
}
