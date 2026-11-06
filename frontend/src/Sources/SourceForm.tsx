import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import translate from 'Utilities/String/translate';

import {
  type GitRepository,
  type SourceItem,
  type SourceKindItem,
  type SourceMode,
  useCreateSource,
  useSourceRoots,
  useUpdateSource,
} from './sources';
import styles from './Sources.module.css';

const modeOptions = (['ReadOnly', 'ReadWrite'] as const).map((mode) => ({ value: mode, label: translate(mode) }));
const originOptions = [
  { value: 'folder', label: translate('OriginFolder') },
  { value: 'git', label: translate('OriginGit') },
];

/** The repository the fields describe, with what is left empty left out. */
function repositoryOf(url: string, branch: string, credential: string): GitRepository {
  return { url: url.trim(), branch: branch.trim() || null, credential: credential.trim() || null };
}

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
  const [origin, setOrigin] = useState(source?.repository ? 'git' : 'folder');
  const [url, setUrl] = useState(source?.repository?.url ?? '');
  const [branch, setBranch] = useState(source?.repository?.branch ?? '');
  const [credential, setCredential] = useState(source?.repository?.credential ?? '');
  const mutation = source ? update : create;
  const git = origin === 'git';

  function submit(event: FormEvent) {
    event.preventDefault();
    const done = {
      onSuccess: () => {
        if (!source) {
          setName(suggestedName);
          setPath('');
          setUrl('');
          setBranch('');
          setCredential('');
        }
        onDone?.();
      },
    };
    const repository = git ? repositoryOf(url, branch, credential) : undefined;
    if (source) {
      update.mutate({ name, mode, path, repository }, done);
    } else {
      create.mutate({ name, kind: kind.kind, mode, path, repository }, done);
    }
  }

  return (
    <form
      aria-label={translate(source ? 'ChangeSource' : 'DeclareSourceOf', { kind: translate(`${kind.kind}Title`) })}
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
      <SelectField
        label={translate('Origin')}
        onChange={(event) => setOrigin(event.target.value)}
        options={originOptions}
        value={origin}
      />
      {git && (
        <>
          <TextField
            label={translate('RepositoryUrl')}
            onChange={(event) => setUrl(event.target.value)}
            placeholder="https://github.com/org/notes"
            required
            value={url}
          />
          <p className={styles.hint}>{translate('RepositoryUrlHint')}</p>
          <TextField label={translate('Branch')} onChange={(event) => setBranch(event.target.value)} value={branch} />
          <p className={styles.hint}>{translate('BranchHint')}</p>
          <TextField
            label={translate('Credential')}
            onChange={(event) => setCredential(event.target.value)}
            value={credential}
          />
          <p className={styles.hint}>{translate('CredentialHint')}</p>
        </>
      )}
      <TextField
        label={translate(git ? 'CloneFolder' : 'Folder')}
        onChange={(event) => setPath(event.target.value)}
        placeholder={roots[0] ?? ''}
        required
        value={path}
      />
      {git && <p className={styles.hint}>{translate('CloneFolderHint')}</p>}
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
