import { type FormEvent, useState } from 'react';
import { useNavigate } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import TextField from 'Components/Form/TextField';
import translate from 'Utilities/String/translate';

import NoteEditor from './NoteEditor';
import { type GitPublish, type Note, noteAddress, useDeleteNote, useRenameNote } from './notes';
import styles from './Notes.module.css';

type Mode = 'view' | 'edit' | 'rename';

/** What to tell when a change was committed to a Git source and could not be pushed: the note is written and committed, and a later sync pushes it. */
export function PushNotice({ git }: Readonly<{ git: GitPublish | null | undefined }>) {
  return git && !git.pushed ? (
    <Alert>{translate('NotePushFailed', { reason: translate(git.error ?? 'sync-failed') })}</Alert>
  ) : null;
}

function RenameForm({ note, onDone }: Readonly<{ note: Note; onDone: () => void }>) {
  const navigate = useNavigate();
  const rename = useRenameNote(note.sourceId);
  const [to, setTo] = useState(note.path);

  function submit(event: FormEvent) {
    event.preventDefault();
    rename.mutate(
      { from: note.path, to: to.trim() },
      {
        onSuccess: (result) => {
          onDone();
          navigate(noteAddress({ sourceId: note.sourceId, path: result.path }), { state: { renamed: result } });
        },
      },
    );
  }

  return (
    <form aria-label={translate('RenameNote')} className={styles.form} onSubmit={submit}>
      {rename.error && <Alert>{rename.error.message}</Alert>}
      <TextField label={translate('NewPath')} onChange={(event) => setTo(event.target.value)} required value={to} />
      <div className={styles.actions}>
        <Button disabled={rename.isPending} type="submit">
          {translate('RenameNote')}
        </Button>
        <Button onClick={onDone} variant="quiet">
          {translate('Cancel')}
        </Button>
      </div>
    </form>
  );
}

/** What a person may do to a note that is open for writing: edit it, move it (its links follow), remove it. */
export default function NoteActions({ note }: Readonly<{ note: Note }>) {
  const navigate = useNavigate();
  const [mode, setMode] = useState<Mode>('view');
  const [confirming, setConfirming] = useState(false);
  const [notice, setNotice] = useState<GitPublish | null>(null);
  const remove = useDeleteNote(note.sourceId, note.path);

  if (!note.writable) {
    return null;
  }
  if (mode === 'edit') {
    return (
      <NoteEditor
        note={note}
        onDone={(result) => {
          setNotice(result?.git ?? null);
          setMode('view');
        }}
      />
    );
  }
  if (mode === 'rename') {
    return <RenameForm note={note} onDone={() => setMode('view')} />;
  }
  return (
    <>
      <PushNotice git={notice} />
      {remove.error && <Alert>{remove.error.message}</Alert>}
      <div className={styles.actions}>
        <Button onClick={() => setMode('edit')} variant="quiet">
          {translate('EditNote')}
        </Button>
        <Button onClick={() => setMode('rename')} variant="quiet">
          {translate('RenameNote')}
        </Button>
        <Button
          disabled={remove.isPending}
          onClick={() =>
            confirming ? remove.mutate(note.version, { onSuccess: () => navigate('/notes') }) : setConfirming(true)
          }
          variant="danger"
        >
          {confirming ? translate('ConfirmRemoveNote', { title: note.title }) : translate('RemoveNote')}
        </Button>
        {confirming && (
          <Button onClick={() => setConfirming(false)} variant="quiet">
            {translate('Cancel')}
          </Button>
        )}
      </div>
    </>
  );
}
