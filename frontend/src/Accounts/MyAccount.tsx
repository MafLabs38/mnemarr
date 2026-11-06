import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import Button from 'Components/Form/Button';
import TextField from 'Components/Form/TextField';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { useSession } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import { useChangeOwnPassword } from './accounts';
import styles from './Accounts.module.css';

/** The signed-in person, and the one thing they may change about themselves: their password. */
export default function MyAccount() {
  const session = useSession();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const change = useChangeOwnPassword();

  function submit(event: FormEvent) {
    event.preventDefault();
    change.mutate(
      { currentPassword, newPassword },
      {
        onSuccess: () => {
          setCurrentPassword('');
          setNewPassword('');
        },
      },
    );
  }

  return (
    <PageContent title={translate('Account')}>
      <PageContentBody>
        <PageHeading scope={session.name} title={translate('Account')} />
        <Card title={translate('ChangePassword')}>
          <form className={styles.form} onSubmit={submit}>
            {change.error && <Alert>{change.error.message}</Alert>}
            {change.isSuccess && <output>{translate('PasswordChanged')}</output>}
            <TextField
              autoComplete="current-password"
              label={translate('CurrentPassword')}
              onChange={(event) => setCurrentPassword(event.target.value)}
              required
              type="password"
              value={currentPassword}
            />
            <TextField
              autoComplete="new-password"
              label={translate('NewPassword')}
              onChange={(event) => setNewPassword(event.target.value)}
              required
              type="password"
              value={newPassword}
            />
            <Button disabled={change.isPending} type="submit">
              {translate('Save')}
            </Button>
          </form>
        </Card>
      </PageContentBody>
    </PageContent>
  );
}
