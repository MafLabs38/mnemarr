import { type FormEvent, useState } from 'react';
import { useNavigate } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import { useSources } from 'Sources/sources';
import translate from 'Utilities/String/translate';

import { PushNotice } from './NoteActions';
import { noteAddress, useCreateNote } from './notes';
import styles from './Notes.module.css';

/** The title a new note starts with: the name of its file, without the folder and the extension. */
function titleOf(path: string): string {
  const name = path.split('/').pop() ?? '';
  return name.replace(/\.(md|markdown)$/iu, '');
}

function Form({
  sources,
  onCreated,
}: Readonly<{ sources: ReadonlyArray<{ id: string; name: string }>; onCreated: () => void }>) {
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
          navigate(noteAddress({ sourceId, path: result.path }));
        },
      },
    );
  }

  return (
    <form aria-label={translate('NewNote')} className={styles.form} onSubmit={submit}>
      {create.error && <Alert>{create.error.message}</Alert>}
      <PushNotice git={create.data?.git} />
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

/** Starts a note in a source that is open for writing. Offered to whoever may manage sources (they are the ones who know which are open). */
export default function NewNote() {
  const { data: sources } = useSources();
  const [open, setOpen] = useState(false);
  const writable = (sources ?? []).filter((source) => source.mode === 'ReadWrite');
  return writable.length === 0 ? null : (
    <details onToggle={(event) => setOpen(event.currentTarget.open)} open={open}>
      <summary>{translate('NewNote')}</summary>
      <Form onCreated={() => setOpen(false)} sources={writable} />
    </details>
  );
}
