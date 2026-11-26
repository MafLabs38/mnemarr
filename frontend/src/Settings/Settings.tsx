import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import translate from 'Utilities/String/translate';

import DictationCard from './DictationCard';
import IdentityCard from './IdentityCard';

/** The settings that an administrator changes from the interface: how this Mnemarr presents itself (ADR 0044) and the chat's dictation (ADR 0047). */
export default function Settings() {
  return (
    <PageContent title={translate('Settings')}>
      <PageContentBody>
        <PageHeading scope={translate('Settings')} title={translate('General')} />
        <IdentityCard />
        <DictationCard />
      </PageContentBody>
    </PageContent>
  );
}
