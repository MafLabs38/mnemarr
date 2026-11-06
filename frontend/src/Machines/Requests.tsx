import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import { type EnrolmentItem, useApproveEnrolment, useEnrolments, useRejectEnrolment } from './machines';
import styles from './Machines.module.css';

const kindText = { 'always-on': 'KindAlwaysOn', intermittent: 'KindIntermittent' } as const;
const stateText = { pending: 'RequestPending', approved: 'RequestApproved', expired: 'RequestExpired' } as const;

function RequestRow({ request }: Readonly<{ request: EnrolmentItem }>) {
  const approve = useApproveEnrolment();
  const reject = useRejectEnrolment();
  const busy = approve.isPending || reject.isPending;
  return (
    <tr>
      <th scope="row">{request.machine}</th>
      <td>{translate(kindText[request.kind])}</td>
      <td>{translate(stateText[request.state])}</td>
      <td>{request.requestedBy}</td>
      <td>{formatDateTime(request.expiresAt)}</td>
      <td>
        <div className={styles.inline}>
          {request.state === 'pending' && (
            <Button
              aria-label={translate('ApproveRequest', { name: request.machine })}
              disabled={busy}
              onClick={() => approve.mutate(request.id)}
            >
              {translate('Approve')}
            </Button>
          )}
          <Button
            aria-label={translate('RejectRequest', { name: request.machine })}
            disabled={busy}
            onClick={() => reject.mutate(request.id)}
            variant="danger"
          >
            {translate('Reject')}
          </Button>
        </div>
      </td>
    </tr>
  );
}

/** The codes that are not over: asked for by an assistant and waiting for a person, or approved and not yet used. */
export default function Requests() {
  const requests = useEnrolments(true);
  if (requests.isError) {
    return <Alert>{translate('MachinesError')}</Alert>;
  }
  if (!requests.data || requests.data.length === 0) {
    return <p>{translate('NoRequests')}</p>;
  }
  return (
    <table className={styles.table}>
      <thead>
        <tr>
          <th scope="col">{translate('Machine')}</th>
          <th scope="col">{translate('Kind')}</th>
          <th scope="col">{translate('State')}</th>
          <th scope="col">{translate('RequestedBy')}</th>
          <th scope="col">{translate('ExpiresOn')}</th>
          <th scope="col">{translate('Actions')}</th>
        </tr>
      </thead>
      <tbody>
        {requests.data.map((request) => (
          <RequestRow key={request.id} request={request} />
        ))}
      </tbody>
    </table>
  );
}
