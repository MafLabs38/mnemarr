import { useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import LoadingIndicator from 'Components/LoadingIndicator';
import StatusLabel from 'Components/Status/StatusLabel';
import { kinds } from 'Helpers/Props/kinds';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import MachineManage from './MachineManage';
import { type MachineItem, stateLook, useForgetMachine, useMachines, useRevokeMachine } from './machines';
import styles from './Machines.module.css';

const kindText = { 'always-on': 'KindAlwaysOn', intermittent: 'KindIntermittent' } as const;

function MachineRow({
  machine,
  others,
  canManage,
}: Readonly<{ machine: MachineItem; others: ReadonlyArray<MachineItem>; canManage: boolean }>) {
  const [managing, setManaging] = useState(false);
  const revoke = useRevokeMachine();
  const forget = useForgetMachine();
  const look = stateLook[machine.state];
  const canRevoke = canManage && machine.kind !== null && machine.state !== 'refused';
  return (
    <>
      <tr>
        <th scope="row">{machine.machine}</th>
        <td>{machine.kind ? translate(kindText[machine.kind]) : ''}</td>
        <td>
          <StatusLabel kind={look.kind}>{translate(look.text)}</StatusLabel>
          {machine.error && <p className={styles.error}>{machine.error}</p>}
        </td>
        <td>{machine.lastSeen ? formatDateTime(machine.lastSeen) : translate('NeverSeen')}</td>
        <td>{machine.conversations}</td>
        <td>
          {machine.version ?? ''}
          {machine.updateAvailable && <StatusLabel kind={kinds.WARNING}>{translate('UpdateAvailable')}</StatusLabel>}
        </td>
        <td>
          {canManage && (
            <Button
              aria-expanded={managing}
              aria-label={translate('ManageMachine', { name: machine.machine })}
              onClick={() => setManaging(!managing)}
              variant="quiet"
            >
              {translate('Manage')}
            </Button>
          )}
          {canRevoke && (
            <Button
              aria-label={translate('RevokeMachine', { name: machine.machine })}
              disabled={revoke.isPending}
              onClick={() => revoke.mutate(machine.machine)}
              variant="danger"
            >
              {translate('Revoke')}
            </Button>
          )}
          {canManage && machine.state === 'refused' && (
            <Button
              aria-label={translate('RemoveMachine', { name: machine.machine })}
              disabled={forget.isPending}
              onClick={() => forget.mutate(machine.machine)}
              variant="quiet"
            >
              {translate('Remove')}
            </Button>
          )}
        </td>
      </tr>
      {managing && (
        <tr>
          <td colSpan={7}>
            <MachineManage machine={machine} onClose={() => setManaging(false)} others={others} />
          </td>
        </tr>
      )}
    </>
  );
}

/** The machines that joined, and the names that sent conversations without ever joining: what each is doing, from facts. */
export default function MachineList() {
  const machines = useMachines();
  const { can } = usePermissions();
  if (machines.isError) {
    return <Alert>{translate('MachinesError')}</Alert>;
  }
  if (!machines.data) {
    return <LoadingIndicator />;
  }
  if (machines.data.length === 0) {
    return <p>{translate('NoMachines')}</p>;
  }
  return (
    <table className={styles.table}>
      <thead>
        <tr>
          <th scope="col">{translate('Machine')}</th>
          <th scope="col">{translate('Kind')}</th>
          <th scope="col">{translate('State')}</th>
          <th scope="col">{translate('LastSeen')}</th>
          <th scope="col">{translate('ConversationsCount')}</th>
          <th scope="col">{translate('Version')}</th>
          <th scope="col">{translate('Actions')}</th>
        </tr>
      </thead>
      <tbody>
        {machines.data.map((machine) => (
          <MachineRow
            canManage={can(permissions.collectorsManage)}
            key={machine.machine}
            machine={machine}
            others={machines.data.filter(
              (other) => other.machine !== machine.machine && other.kind !== null && other.state !== 'refused',
            )}
          />
        ))}
      </tbody>
    </table>
  );
}
