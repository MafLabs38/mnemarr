import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import Button from 'Components/Form/Button';
import TextField from 'Components/Form/TextField';
import LoadingIndicator from 'Components/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { apiGet, apiSend } from 'Helpers/Api/apiGet';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import type { AccountItem, ApiKeyItem, CreatedApiKey } from './accounts';
import styles from './Accounts.module.css';

/** The secret of a key that has just been created. This is the only time it is shown: it is kept nowhere. */
function NewKey({ created, onDismiss }: Readonly<{ created: CreatedApiKey; onDismiss: () => void }>) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(created.secret);
    setCopied(true);
  }

  return (
    <section aria-label={translate('NewKey')} className={styles.newKey}>
      <p>{translate('NewKeyWarning', { name: created.key.name })}</p>
      <code className={styles.secret}>{created.secret}</code>
      <div className={styles.inline}>
        <Button onClick={copy}>{translate(copied ? 'Copied' : 'Copy')}</Button>
        <Button onClick={onDismiss} variant="quiet">
          {translate('Done')}
        </Button>
      </div>
    </section>
  );
}

function keyState(key: ApiKeyItem): string {
  if (key.revokedAt) {
    return translate('Revoked');
  }
  if (key.expiresAt && new Date(key.expiresAt) <= new Date()) {
    return translate('Expired');
  }
  return translate('Active');
}

export default function AccountKeys() {
  const { id = '' } = useParams();
  const client = useQueryClient();
  const keysKey = ['api', `/accounts/${id}/keys`];
  const account = useQuery({
    queryKey: ['api', `/accounts/${id}`],
    queryFn: ({ signal }) => apiGet<AccountItem>(`/accounts/${id}`, signal),
  });
  const keys = useQuery({
    queryKey: keysKey,
    queryFn: ({ signal }) => apiGet<ApiKeyItem[]>(`/accounts/${id}/keys`, signal),
  });
  const [name, setName] = useState('');
  const [expires, setExpires] = useState('');
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const create = useMutation({
    mutationFn: () =>
      apiSend<CreatedApiKey>('POST', `/accounts/${id}/keys`, {
        name,
        expiresAt: expires ? new Date(`${expires}T00:00:00Z`).toISOString() : undefined,
      }),
    onSuccess: (result) => {
      setCreated(result);
      setName('');
      setExpires('');
      return client.invalidateQueries({ queryKey: keysKey });
    },
  });
  const revoke = useMutation({
    mutationFn: (keyId: string) => apiSend('DELETE', `/accounts/${id}/keys/${keyId}`),
    onSuccess: () => client.invalidateQueries({ queryKey: keysKey }),
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    create.mutate();
  }

  if (account.isError || keys.isError) {
    return (
      <PageContent title={translate('Keys')}>
        <PageContentBody>
          <Alert>{translate('AccountsError')}</Alert>
        </PageContentBody>
      </PageContent>
    );
  }
  if (!account.data || !keys.data) {
    return <LoadingIndicator />;
  }

  return (
    <PageContent title={translate('KeysOf', { name: account.data.name })}>
      <PageContentBody>
        <PageHeading scope={translate('Accounts')} title={translate('KeysOf', { name: account.data.name })} />
        <p>
          <Link to="/accounts">{translate('BackToAccounts')}</Link>
        </p>
        {created && <NewKey created={created} onDismiss={() => setCreated(null)} />}
        <Card title={translate('Keys')}>
          {keys.data.length === 0 ? (
            <p>{translate('NoKeys')}</p>
          ) : (
            <table className={styles.table}>
              <thead>
                <tr>
                  <th scope="col">{translate('Name')}</th>
                  <th scope="col">{translate('Prefix')}</th>
                  <th scope="col">{translate('State')}</th>
                  <th scope="col">{translate('LastUsed')}</th>
                  <th scope="col">{translate('ExpiresOn')}</th>
                  <th scope="col">{translate('Actions')}</th>
                </tr>
              </thead>
              <tbody>
                {keys.data.map((key) => (
                  <tr key={key.id}>
                    <th scope="row">{key.name}</th>
                    <td>
                      <code>{key.prefix}</code>
                    </td>
                    <td>{keyState(key)}</td>
                    <td>{key.lastUsedAt ? formatDateTime(key.lastUsedAt) : translate('NeverUsed')}</td>
                    <td>{formatDateTime(key.expiresAt)}</td>
                    <td>
                      {!key.revokedAt && (
                        <Button
                          aria-label={translate('RevokeKey', { name: key.name })}
                          disabled={revoke.isPending}
                          onClick={() => revoke.mutate(key.id)}
                          variant="danger"
                        >
                          {translate('Revoke')}
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Card>
        <Card title={translate('CreateKey')}>
          <form className={styles.form} onSubmit={submit}>
            {create.error && <Alert>{create.error.message}</Alert>}
            <TextField
              label={translate('KeyName')}
              onChange={(event) => setName(event.target.value)}
              required
              value={name}
            />
            <TextField
              label={translate('ExpiresOn')}
              onChange={(event) => setExpires(event.target.value)}
              type="date"
              value={expires}
            />
            <Button disabled={create.isPending} type="submit">
              {translate('Create')}
            </Button>
          </form>
        </Card>
      </PageContentBody>
    </PageContent>
  );
}
