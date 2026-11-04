import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import translate from 'Utilities/String/translate';

import {
  type SourceItem,
  type SourceKindItem,
  type SourceMode,
  useCreateSource,
  useSourceRoots,
  useUpdateSource,
} from './sources';
import styles from './Sources.module.css';

const modeOptions = (['ReadOnly', 'ReadWrite'] as const).map((mode) => ({ value: mode, label: translate(mode) }));

interface SourceFormProps {
  kind: SourceKindItem;
  /** The source to change; none to declare a new one. */
  source?: SourceItem;
  /** What the form proposes for a name that is not given. */
  suggestedName?: string;
  onDone?: () => void;
}

/** Declares a source of a kind, or changes one. The kind is not a choice: it is the section the form is in. */
export default function SourceForm({ kind, source, suggestedName = '', onDone }: Readonly<SourceFormProps>) {
  const { data: roots = [] } = useSourceRoots();
  const create = useCreateSource();
  const update = useUpdateSource(source?.id ?? '');
  const [name, setName] = useState(source?.name ?? suggestedName);
  const [mode, setMode] = useState<SourceMode>(source?.mode ?? kind.defaultMode);
  const [path, setPath] = useState(source?.path ?? '');
  const mutation = source ? update : create;

  function submit(event: FormEvent) {
    event.preventDefault();
    const done = {
      onSuccess: () => {
        if (!source) {
          setName(suggestedName);
          setPath('');
        }
        onDone?.();
      },
    };
    if (source) {
      update.mutate({ name, mode, path }, done);
    } else {
      create.mutate({ name, kind: kind.kind, mode, path }, done);
    }
  }

  return (
    <form
      aria-label={translate(source ? 'ChangeSource' : 'DeclareSourceOf', { kind: kind.title })}
      className={styles.form}
      onSubmit={submit}
    >
      {mutation.error && <Alert>{mutation.error.message}</Alert>}
      <TextField label={translate('Name')} onChange={(event) => setName(event.target.value)} required value={name} />
      <SelectField
        label={translate('Mode')}
        onChange={(event) => setMode(event.target.value as SourceMode)}
        options={modeOptions}
        value={mode}
      />
      <TextField
        label={translate('Folder')}
        onChange={(event) => setPath(event.target.value)}
        placeholder={roots[0] ?? ''}
        required
        value={path}
      />
      {roots.length > 0 && <p className={styles.hint}>{translate('SourceRoots', { roots: roots.join(', ') })}</p>}
      <div className={styles.actions}>
        <Button disabled={mutation.isPending} type="submit">
          {translate(source ? 'Save' : 'Create')}
        </Button>
        {source && onDone && (
          <Button onClick={onDone} variant="quiet">
            {translate('Cancel')}
          </Button>
        )}
      </div>
    </form>
  );
}
