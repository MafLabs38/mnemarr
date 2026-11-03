import { useMutation, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import BrandLogo from 'Components/BrandLogo';
import Button from 'Components/Form/Button';
import TextField from 'Components/Form/TextField';
import { ApiError, apiSend } from 'Helpers/Api/apiGet';
import { type Session, sessionKey } from 'Session/session';
import translate from 'Utilities/String/translate';

import styles from './Login.module.css';

/** What to tell a person who could not sign in. A wrong name and a wrong password are told the same, as the server does. */
function failureText(error: Error): string {
  if (error instanceof ApiError && error.status === 401) {
    return translate('SignInFailed');
  }
  if (error instanceof ApiError && error.status === 429) {
    return translate('TooManyAttempts');
  }
  return translate('SignInUnavailable');
}

export default function Login() {
  const client = useQueryClient();
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const signIn = useMutation({
    mutationFn: () => apiSend<Session>('POST', '/auth/login', { name, password }),
    onSuccess: (session) => client.setQueryData(sessionKey, session),
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    signIn.mutate();
  }

  return (
    <main className={styles.page} id="main">
      <form className={styles.card} onSubmit={submit}>
        <div className={styles.brand}>
          <BrandLogo size={72} />
          <h1 className={styles.title}>{translate('SignInTitle')}</h1>
        </div>
        {signIn.error && <Alert>{failureText(signIn.error)}</Alert>}
        <TextField
          autoComplete="username"
          label={translate('Name')}
          onChange={(event) => setName(event.target.value)}
          required
          value={name}
        />
        <TextField
          autoComplete="current-password"
          label={translate('Password')}
          onChange={(event) => setPassword(event.target.value)}
          required
          type="password"
          value={password}
        />
        <Button disabled={signIn.isPending} type="submit">
          {translate('SignIn')}
        </Button>
      </form>
    </main>
  );
}
