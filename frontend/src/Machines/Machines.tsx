import Card from 'Components/Card';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import AddMachine from './AddMachine';
import MachineList from './MachineList';
import NotificationsCard from './NotificationsCard';
import Pairings from './Pairings';
import Requests from './Requests';
import TitlesCard from './TitlesCard';

/** The machines that send conversations: what each is doing, who asked to add one, and how to add one (ADR 0027). */
export default function Machines() {
  const { can } = usePermissions();
  const canManage = can(permissions.collectorsManage);
  return (
    <PageContent title={translate('Machines')}>
      <PageContentBody>
        <PageHeading scope={translate('Settings')} title={translate('Machines')} />
        <p>{translate('MachinesIntro')}</p>
        <Card title={translate('Machines')}>
          <MachineList />
        </Card>
        <Card title={translate('ConversationTitles')}>
          <TitlesCard />
        </Card>
        {canManage && (
          <>
            <Card title={translate('AskingToBeLinked')}>
              <Pairings />
            </Card>
            <Card title={translate('Notifications')}>
              <NotificationsCard />
            </Card>
            <Card title={translate('Requests')}>
              <Requests />
            </Card>
            <Card title={translate('AddAMachine')}>
              <AddMachine />
            </Card>
          </>
        )}
      </PageContentBody>
    </PageContent>
  );
}
