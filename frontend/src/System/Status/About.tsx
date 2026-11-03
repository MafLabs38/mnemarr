import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import LoadingIndicator from 'Components/LoadingIndicator';
import { formatUptime } from 'Utilities/Date/formatUptime';
import translate from 'Utilities/String/translate';

import { useSystemStatus } from './useSystemStatus';

export default function About() {
  const { data, error, isPending } = useSystemStatus();

  return (
    <Card title={translate('About')}>
      {isPending && <LoadingIndicator />}
      {/* A failed refresh keeps the last good data in the query: show the error, never the stale values under it. */}
      {error && <Alert>{translate('SystemStatusError', { message: error.message })}</Alert>}
      {data && !error && (
        <DescriptionList
          items={[
            { title: translate('Version'), data: data.version, mono: true },
            { title: translate('Runtime'), data: data.runtimeVersion, mono: true },
            { title: translate('Started'), data: new Date(data.startedAt).toLocaleString(), mono: true },
            { title: translate('Uptime'), data: formatUptime(data.uptimeSeconds), mono: true },
          ]}
        />
      )}
    </Card>
  );
}
