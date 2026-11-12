import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import { useNotifications, useSendTestMail } from './machines';
import styles from './Machines.module.css';

/** Whether Mnemarr can write to a person when a machine needs them, and a way to try it (ADR 0042). The settings are in the configuration, not here. */
export default function NotificationsCard() {
  const status = useNotifications();
  const test = useSendTestMail();
  if (status.error) {
    return <Alert>{status.error.message}</Alert>;
  }
  if (!status.data) {
    return null;
  }
  const { enabled, provider, recipient, lastAt, lastDelivered, lastProblem } = status.data;
  if (!enabled) {
    return <p className={styles.hint}>{translate('NotificationsOff')}</p>;
  }
  return (
    <>
      <p>{translate('NotificationsOn', { recipient: recipient ?? '', provider: provider ?? '' })}</p>
      {lastAt && lastDelivered && (
        <p className={styles.hint}>{translate('NotificationsLast', { at: formatDateTime(lastAt) })}</p>
      )}
      {lastProblem && <Alert>{translate('NotificationsLastProblem', { problem: lastProblem })}</Alert>}
      {test.error && <Alert>{test.error.message}</Alert>}
      {test.data?.delivered && <output>{translate('TestMailSent', { recipient: recipient ?? '' })}</output>}
      {test.data && !test.data.delivered && (
        <Alert>{translate('TestMailFailed', { problem: test.data.problem ?? '' })}</Alert>
      )}
      <Button disabled={test.isPending} onClick={() => test.mutate()}>
        {translate('SendTestMail')}
      </Button>
    </>
  );
}
