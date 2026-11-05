import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import translate from 'Utilities/String/translate';

import { type CreatedEnrolment, type MachineKindName, useAddMachine } from './machines';
import styles from './Machines.module.css';

/** The code of a machine that has just been added, and the command that uses it. Shown once: it is kept nowhere. */
function NewCode({ created, onDismiss }: Readonly<{ created: CreatedEnrolment; onDismiss: () => void }>) {
  const [copied, setCopied] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(created.command);
      setCopied(true);
    } catch {
      // No clipboard on a page served over plain HTTP: the command is selected with one click, and the person is told so.
      setCopyFailed(true);
    }
  }

  return (
    <section aria-label={translate('NewCode')} className={styles.newCode}>
      <p>{translate('NewCodeWarning', { name: created.enrolment.machine })}</p>
      <code className={styles.secret}>{created.code}</code>
      <p>{translate('NewCodeCommand')}</p>
      <code className={styles.secret}>{created.command}</code>
      {copyFailed && <output>{translate('CopyUnavailable')}</output>}
      <div className={styles.inline}>
        <Button onClick={copy}>{translate(copied ? 'Copied' : 'CopyCommand')}</Button>
        <Button onClick={onDismiss} variant="quiet">
          {translate('Done')}
        </Button>
      </div>
    </section>
  );
}

/** Adds a machine: a code, shown once, that the machine exchanges for its own key. */
export default function AddMachine() {
  const [machine, setMachine] = useState('');
  const [kind, setKind] = useState<MachineKindName>('intermittent');
  const [created, setCreated] = useState<CreatedEnrolment | null>(null);
  const add = useAddMachine();

  function submit(event: FormEvent) {
    event.preventDefault();
    add.mutate(
      { machine, kind },
      {
        onSuccess: (result) => {
          setCreated(result);
          setMachine('');
        },
      },
    );
  }

  return (
    <>
      {created && (
        <NewCode
          created={created}
          onDismiss={() => {
            setCreated(null);
            add.reset();
          }}
        />
      )}
      <form className={styles.form} onSubmit={submit}>
        {add.error && <Alert>{add.error.message}</Alert>}
        <TextField
          label={translate('MachineName')}
          onChange={(event) => setMachine(event.target.value)}
          required
          value={machine}
        />
        <SelectField
          label={translate('Kind')}
          onChange={(event) => setKind(event.target.value as MachineKindName)}
          options={[
            { value: 'intermittent', label: translate('KindIntermittent') },
            { value: 'always-on', label: translate('KindAlwaysOn') },
          ]}
          value={kind}
        />
        <p className={styles.hint}>{translate('AddMachineHelp')}</p>
        <Button disabled={add.isPending} type="submit">
          {translate('AddMachine')}
        </Button>
      </form>
    </>
  );
}
