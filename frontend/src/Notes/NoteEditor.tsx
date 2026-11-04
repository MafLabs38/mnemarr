import { useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import translate from 'Utilities/String/translate';

import NoteMarkdown from './NoteMarkdown';
import { type Note, usePreviewNote, useUpdateNote, type WriteResult } from './notes';
import styles from './Notes.module.css';

interface NoteEditorProps {
  note: Note;
  onDone: (result: WriteResult | null) => void;
}

/**
 * Changes the text of a note (the whole file, front matter included). The version that was read goes back with the change: a note that
 * changed since is refused, and what was typed stays on the screen, so that nothing is lost.
 */
export default function NoteEditor({ note, onDone }: Readonly<NoteEditorProps>) {
  const [text, setText] = useState(note.raw);
  const update = useUpdateNote(note.sourceId, note.path);
  const preview = usePreviewNote(note.sourceId);
  const failure = update.error ?? preview.error;

  function save() {
    update.mutate({ content: text, version: note.version }, { onSuccess: onDone });
  }

  return (
    <form
      aria-label={translate('EditNote')}
      className={styles.form}
      onSubmit={(event) => {
        event.preventDefault();
        save();
      }}
    >
      {failure && <Alert>{failure.message}</Alert>}
      <textarea
        aria-label={translate('NoteText')}
        className={styles.editor}
        onChange={(event) => setText(event.target.value)}
        value={text}
      />
      <div className={styles.actions}>
        <Button disabled={update.isPending} type="submit">
          {translate('SaveNote')}
        </Button>
        <Button
          disabled={preview.isPending}
          onClick={() => (preview.data ? preview.reset() : preview.mutate({ path: note.path, content: text }))}
          variant="quiet"
        >
          {translate(preview.data ? 'HidePreview' : 'PreviewNote')}
        </Button>
        <Button onClick={() => onDone(null)} variant="quiet">
          {translate('Cancel')}
        </Button>
      </div>
      {preview.data && (
        <section aria-label={translate('PreviewNote')} className={styles.preview}>
          <NoteMarkdown note={{ ...note, rendered: preview.data.rendered }} />
        </section>
      )}
    </form>
  );
}
