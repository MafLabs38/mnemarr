import Card from 'Components/Card';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import AccountList from './AccountList';
import Activity from './Activity';
import CreateAccount from './CreateAccount';

/** Who can sign in, with what role, and who holds keys. Only an administrator reaches it. */
export default function Accounts() {
  const { can } = usePermissions();
  return (
    <PageContent title={translate('Accounts')}>
      <PageContentBody>
        <PageHeading scope={translate('Settings')} title={translate('Accounts')} />
        <Card title={translate('Accounts')}>
          <AccountList />
        </Card>
        <Card title={translate('CreateAccount')}>
          <CreateAccount />
        </Card>
        {can(permissions.auditRead) && (
          <Card title={translate('RecentActivity')}>
            <Activity />
          </Card>
        )}
      </PageContentBody>
    </PageContent>
  );
}
