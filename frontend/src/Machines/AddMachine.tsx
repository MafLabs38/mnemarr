import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import translate from 'Utilities/String/translate';

import { type CreatedEnrolment, type MachineKindName, machineNameFor, useAddMachine } from './machines';
import styles from './Machines.module.css';
import NewCode from './NewCode';

/** Adds a machine: a code, shown once, that the machine exchanges for its own key. */
export default function AddMachine() {
  const [machine, setMachine] = useState('');
  const [kind, setKind] = useState<MachineKindName>('intermittent');
  const [created, setCreated] = useState<CreatedEnrolment | null>(null);
  const add = useAddMachine();
  const name = machineNameFor(machine);

  function submit(event: FormEvent) {
    event.preventDefault();
    add.mutate(
      { machine: name, kind },
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
        {name !== '' && name !== machine && <p className={styles.hint}>{translate('MachineNameUsed', { name })}</p>}
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
        <p className={styles.hint}>{translate('AlreadyHasACollector')}</p>
        <Button disabled={add.isPending || name === ''} type="submit">
          {translate('AddMachine')}
        </Button>
      </form>
    </>
  );
}
