import { Link } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import styles from './Machines.module.css';
import { usePairings } from './pairings';

/** The computers that asked to be linked and wait for a person (ADR 0030): a person who did not follow the link finds them here. */
export default function Pairings() {
  const pairings = usePairings(true);
  if (pairings.isError) {
    return <Alert>{translate('MachinesError')}</Alert>;
  }
  if (!pairings.data || pairings.data.length === 0) {
    return <p>{translate('NoPairings')}</p>;
  }
  return (
    <table className={styles.table}>
      <thead>
        <tr>
          <th scope="col">{translate('Machine')}</th>
          <th scope="col">{translate('ExpiresOn')}</th>
          <th scope="col">{translate('Actions')}</th>
        </tr>
      </thead>
      <tbody>
        {pairings.data.map((pairing) => (
          <tr key={pairing.id}>
            <th scope="row">{pairing.hostname}</th>
            <td>{formatDateTime(pairing.expiresAt)}</td>
            <td>
              <Link aria-label={translate('ReviewPairing', { name: pairing.hostname })} to={`/link?id=${pairing.id}`}>
                {translate('ReviewRequest')}
              </Link>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
