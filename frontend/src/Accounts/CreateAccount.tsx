import { useMutation, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import { apiSend } from 'Helpers/Api/apiGet';
import translate from 'Utilities/String/translate';

import { accountsKey, useRoles } from './accounts';
import styles from './Accounts.module.css';

export default function CreateAccount() {
  const client = useQueryClient();
  const { data: roles = [] } = useRoles();
  const [name, setName] = useState('');
  const [kind, setKind] = useState<'User' | 'Service'>('User');
  const [role, setRole] = useState('Reader');
  const [password, setPassword] = useState('');
  const roleOptions = roles
    .filter((item) => (kind === 'User' ? item.forUsers : item.forServices))
    .map((item) => ({ value: item.name, label: item.name }));
  const create = useMutation({
    mutationFn: () =>
      apiSend('POST', '/accounts', { name, kind, role, password: kind === 'User' ? password : undefined }),
    onSuccess: () => {
      setName('');
      setPassword('');
      return client.invalidateQueries({ queryKey: accountsKey });
    },
  });

  function chooseKind(next: 'User' | 'Service') {
    setKind(next);
    setRole(next === 'User' ? 'Reader' : 'Assistant');
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    create.mutate();
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
        onChange={(event) => setRole(event.target.value)}
        options={roleOptions}
        value={role}
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
