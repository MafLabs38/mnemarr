import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import translate from 'Utilities/String/translate';

import { leastPrivilegedRole, useCreateAccount, useRoles } from './accounts';
import styles from './Accounts.module.css';

export default function CreateAccount() {
  const { data: roles = [] } = useRoles();
  const [name, setName] = useState('');
  const [kind, setKind] = useState<'User' | 'Service'>('User');
  const [chosenRole, setChosenRole] = useState<string | null>(null);
  const [password, setPassword] = useState('');
  const rolesOfKind = roles.filter((item) => (kind === 'User' ? item.forUsers : item.forServices));
  const roleOptions = rolesOfKind.map((item) => ({ value: item.name, label: item.name }));
  const role = rolesOfKind.some((item) => item.name === chosenRole) ? chosenRole : leastPrivilegedRole(rolesOfKind);
  const create = useCreateAccount();

  function chooseKind(next: 'User' | 'Service') {
    setKind(next);
    setChosenRole(null);
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    create.mutate(
      { name, kind, role: role ?? '', password: kind === 'User' ? password : undefined },
      {
        onSuccess: () => {
          setName('');
          setPassword('');
        },
      },
    );
  }

  return (
    <form className={styles.form} onSubmit={submit}>
      {create.error && <Alert>{create.error.message}</Alert>}
      <TextField label={translate('Name')} onChange={(event) => setName(event.target.value)} required value={name} />
      <SelectField
        label={translate('Kind')}
        onChange={(event) => chooseKind(event.target.value as 'User' | 'Service')}
        options={[
          { value: 'User', label: translate('User') },
          { value: 'Service', label: translate('Service') },
        ]}
        value={kind}
      />
      <SelectField
        label={translate('Role')}
        onChange={(event) => setChosenRole(event.target.value)}
        options={roleOptions}
        value={role ?? ''}
      />
      {kind === 'User' && (
        <TextField
          autoComplete="new-password"
          label={translate('Password')}
          onChange={(event) => setPassword(event.target.value)}
          required
          type="password"
          value={password}
        />
      )}
      <Button disabled={create.isPending} type="submit">
        {translate('Create')}
      </Button>
    </form>
  );
}
