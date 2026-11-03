import Alert from 'Components/Alert/Alert';
import LoadingIndicator from 'Components/LoadingIndicator';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import { useAudit } from './accounts';
import styles from './Accounts.module.css';

const AUDIT_LIMIT = 20;

/** The last things that were done to accounts and keys, and the sign-ins: who, when, to what. */
export default function Activity() {
  const { data, isPending, isError } = useAudit(AUDIT_LIMIT);

  if (isPending) {
    return <LoadingIndicator />;
  }
  if (isError) {
    return <Alert>{translate('AccountsError')}</Alert>;
  }
  return (
    <table className={styles.table}>
      <thead>
        <tr>
          <th scope="col">{translate('When')}</th>
          <th scope="col">{translate('Who')}</th>
          <th scope="col">{translate('Action')}</th>
          <th scope="col">{translate('Target')}</th>
        </tr>
      </thead>
      <tbody>
        {data.map((event) => (
          <tr key={event.id}>
            <td>{formatDateTime(event.at)}</td>
            <td>{event.actor}</td>
            <td>{event.action}</td>
            <td>{event.target}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
