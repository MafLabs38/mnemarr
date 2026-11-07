import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import translate from 'Utilities/String/translate';

import {
  type CreatedEnrolment,
  type MachineItem,
  machineNameFor,
  useAddMachine,
  useEraseMachine,
  useMergeMachine,
  useRemoveMachine,
  useRenameMachine,
} from './machines';
import styles from './Machines.module.css';
import NewCode from './NewCode';

interface ManageProps {
  machine: MachineItem;
  /** The machines that can receive what this one sent: the ones that are enrolled and not revoked. */
  others: ReadonlyArray<MachineItem>;
  onClose: () => void;
}

function Rename({ machine, onClose }: Readonly<Pick<ManageProps, 'machine' | 'onClose'>>) {
  const [typed, setTyped] = useState(machine.machine);
  const rename = useRenameMachine();
  const name = machineNameFor(typed);

  function submit(event: FormEvent) {
    event.preventDefault();
    rename.mutate({ machine: machine.machine, name }, { onSuccess: onClose });
  }

  return (
    <form className={styles.form} onSubmit={submit}>
      <h4>{translate('RenameTitle')}</h4>
      {rename.error && <Alert>{rename.error.message}</Alert>}
      <TextField
        label={translate('MachineName')}
        onChange={(event) => setTyped(event.target.value)}
        required
        value={typed}
      />
      {name !== '' && name !== typed && <p className={styles.hint}>{translate('MachineNameUsed', { name })}</p>}
      <p className={styles.hint}>{translate('RenameHelp')}</p>
      <Button disabled={rename.isPending || name === '' || name === machine.machine} type="submit">
        {translate('Rename')}
      </Button>
    </form>
  );
}

function Merge({ machine, others, onClose }: Readonly<ManageProps>) {
  const [into, setInto] = useState('');
  const merge = useMergeMachine();
  if (others.length === 0) {
    return null;
  }
  return (
    <form
      className={styles.form}
      onSubmit={(event) => {
        event.preventDefault();
        merge.mutate({ machine: machine.machine, into }, { onSuccess: onClose });
      }}
    >
      <h4>{translate('MergeTitle')}</h4>
      <p className={styles.hint}>{translate('MergeHelp', { name: machine.machine })}</p>
      {merge.error && <Alert>{merge.error.message}</Alert>}
      <SelectField
        label={translate('MergeInto')}
        onChange={(event) => setInto(event.target.value)}
        options={[{ value: '', label: '' }, ...others.map((other) => ({ value: other.machine, label: other.machine }))]}
        value={into}
      />
      <Button disabled={merge.isPending || into === ''} type="submit">
        {translate('Merge')}
      </Button>
    </form>
  );
}

function Reinstall({ machine }: Readonly<Pick<ManageProps, 'machine'>>) {
  const add = useAddMachine();
  const [created, setCreated] = useState<CreatedEnrolment | null>(null);
  return (
    <div className={styles.form}>
      <h4>{translate('ReinstallTitle')}</h4>
      <p className={styles.hint}>{translate('ReinstallHelp', { name: machine.machine })}</p>
      {add.error && <Alert>{add.error.message}</Alert>}
      {created ? (
        <NewCode
          created={created}
          onDismiss={() => {
            setCreated(null);
            add.reset();
          }}
        />
      ) : (
        <Button
          disabled={add.isPending}
          onClick={() =>
            add.mutate({ machine: machine.machine, kind: machine.kind ?? 'intermittent' }, { onSuccess: setCreated })
          }
        >
          {translate('Reinstall')}
        </Button>
      )}
    </div>
  );
}

/** Removing a machine, and deleting it with everything it sent: the second says what it does, and asks. */
function Removal({ machine, onClose }: Readonly<Pick<ManageProps, 'machine' | 'onClose'>>) {
  const [confirming, setConfirming] = useState(false);
  const remove = useRemoveMachine();
  const erase = useEraseMachine();
  const error = remove.error ?? erase.error;
  const enrolled = machine.kind !== null;
  return (
    <div className={styles.form}>
      <h4>{translate('RemovalTitle')}</h4>
      {error && <Alert>{error.message}</Alert>}
      {enrolled && (
        <>
          <p className={styles.hint}>{translate('RemoveHelp')}</p>
          <Button
            aria-label={translate('RemoveMachineKeep', { name: machine.machine })}
            disabled={remove.isPending}
            onClick={() => remove.mutate(machine.machine, { onSuccess: onClose })}
            variant="quiet"
          >
            {translate('RemoveKeep')}
          </Button>
        </>
      )}
      <p className={styles.hint}>{translate('EraseHelp')}</p>
      {confirming ? (
        <section aria-label={translate('EraseConfirmTitle')} className={styles.newCode}>
          <p>{translate('EraseConfirm', { name: machine.machine, count: String(machine.conversations) })}</p>
          <div className={styles.inline}>
            <Button
              aria-label={translate('EraseNow', { name: machine.machine })}
              disabled={erase.isPending}
              onClick={() => erase.mutate(machine.machine, { onSuccess: onClose })}
              variant="danger"
            >
              {translate('EraseYes')}
            </Button>
            <Button onClick={() => setConfirming(false)} variant="quiet">
              {translate('Cancel')}
            </Button>
          </div>
        </section>
      ) : (
        <Button
          aria-label={translate('EraseMachine', { name: machine.machine })}
          onClick={() => setConfirming(true)}
          variant="danger"
        >
          {translate('Erase')}
        </Button>
      )}
    </div>
  );
}

/** What a person can do with a machine, or with a name that conversations came from (ADR 0031). */
export default function MachineManage({ machine, others, onClose }: Readonly<ManageProps>) {
  return (
    <section aria-label={translate('ManageMachine', { name: machine.machine })} className={styles.manage}>
      <Rename machine={machine} onClose={onClose} />
      <Merge machine={machine} onClose={onClose} others={others} />
      {machine.kind !== null && machine.state !== 'refused' && <Reinstall machine={machine} />}
      <Removal machine={machine} onClose={onClose} />
    </section>
  );
}
