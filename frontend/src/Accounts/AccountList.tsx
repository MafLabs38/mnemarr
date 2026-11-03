import { useMutation, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';
import { Link } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import LoadingIndicator from 'Components/LoadingIndicator';
import StatusLabel from 'Components/Status/StatusLabel';
import { apiSend } from 'Helpers/Api/apiGet';
import { kinds } from 'Helpers/Props/kinds';
import { useSession } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import { type AccountItem, accountsKey, type RoleItem, useAccounts, useRoles } from './accounts';
import styles from './Accounts.module.css';

function PasswordForm({ account, onDone }: Readonly<{ account: AccountItem; onDone: () => void }>) {
  const [password, setPassword] = useState('');
  const save = useMutation({
    mutationFn: () => apiSend('PUT', `/accounts/${account.id}/password`, { password }),
    onSuccess: onDone,
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    save.mutate();
  }

  return (
    <form className={styles.inline} onSubmit={submit}>
      <TextField
        autoComplete="new-password"
        label={translate('NewPasswordFor', { name: account.name })}
        onChange={(event) => setPassword(event.target.value)}
        required
        type="password"
        value={password}
      />
      <Button disabled={save.isPending} type="submit">
        {translate('Save')}
      </Button>
      <Button onClick={onDone} variant="quiet">
        {translate('Cancel')}
      </Button>
      {save.error && <Alert>{save.error.message}</Alert>}
    </form>
  );
}

interface RowProps {
  account: AccountItem;
  roles: ReadonlyArray<RoleItem>;
  isSelf: boolean;
  onError: (error: Error) => void;
}

function AccountRow({ account, roles, isSelf, onError }: Readonly<RowProps>) {
  const client = useQueryClient();
  const [editingPassword, setEditingPassword] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const done = () => client.invalidateQueries({ queryKey: accountsKey });
  const update = useMutation({
    mutationFn: (change: { role: string; enabled: boolean }) => apiSend('PUT', `/accounts/${account.id}`, change),
    onSuccess: done,
    onError,
  });
  const remove = useMutation({
    mutationFn: () => apiSend('DELETE', `/accounts/${account.id}`),
    onSuccess: done,
    onError,
  });
  const roleOptions = roles
    .filter((role) => (account.kind === 'User' ? role.forUsers : role.forServices))
    .map((role) => ({ value: role.name, label: role.name }));

  return (
    <>
      <tr>
        <th scope="row">{account.name}</th>
        <td>{translate(account.kind === 'User' ? 'User' : 'Service')}</td>
        <td>
          <SelectField
            aria-label={translate('RoleOf', { name: account.name })}
            disabled={update.isPending}
            label=""
            onChange={(event) => update.mutate({ role: event.target.value, enabled: account.enabled })}
            options={roleOptions}
            value={account.role}
          />
        </td>
        <td>
          <StatusLabel kind={account.enabled ? kinds.SUCCESS : kinds.DANGER}>
            {translate(account.enabled ? 'Enabled' : 'Disabled')}
          </StatusLabel>
        </td>
        <td className={styles.actions}>
          <Button
            disabled={update.isPending}
            onClick={() => update.mutate({ role: account.role, enabled: !account.enabled })}
            variant="quiet"
          >
            {translate(account.enabled ? 'Disable' : 'Enable')}
          </Button>
          {account.kind === 'User' && (
            <Button onClick={() => setEditingPassword(true)} variant="quiet">
              {translate('SetPassword')}
            </Button>
          )}
          <Link aria-label={translate('KeysOf', { name: account.name })} to={`/accounts/${account.id}/keys`}>
            {translate('Keys')}
          </Link>
          {!isSelf && (
            <Button
              disabled={remove.isPending}
              onClick={() => (confirmingDelete ? remove.mutate() : setConfirmingDelete(true))}
              variant="danger"
            >
              {confirmingDelete ? translate('ConfirmDelete', { name: account.name }) : translate('Delete')}
            </Button>
          )}
        </td>
      </tr>
      {editingPassword && (
        <tr>
          <td colSpan={5}>
            <PasswordForm account={account} onDone={() => setEditingPassword(false)} />
          </td>
        </tr>
      )}
    </>
  );
}

export default function AccountList() {
  const { data: accounts, isPending, isError } = useAccounts();
  const { data: roles } = useRoles();
  const session = useSession();
  const [error, setError] = useState<Error | null>(null);

  if (isPending) {
    return <LoadingIndicator />;
  }
  if (isError || !roles) {
    return <Alert>{translate('AccountsError')}</Alert>;
  }

  return (
    <>
      {error && <Alert>{error.message}</Alert>}
      <table className={styles.table}>
        <thead>
          <tr>
            <th scope="col">{translate('Name')}</th>
            <th scope="col">{translate('Kind')}</th>
            <th scope="col">{translate('Role')}</th>
            <th scope="col">{translate('State')}</th>
            <th scope="col">{translate('Actions')}</th>
          </tr>
        </thead>
        <tbody>
          {accounts.map((account) => (
            <AccountRow
              account={account}
              isSelf={account.id === session.accountId}
              key={account.id}
              onError={setError}
              roles={roles}
            />
          ))}
        </tbody>
      </table>
    </>
  );
}
