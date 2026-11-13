import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import translate from 'Utilities/String/translate';

import IdentityCard from './IdentityCard';

/** The settings that an administrator changes from the interface: how this Mnemarr presents itself, for now (ADR 0044). */
export default function Settings() {
  return (
    <PageContent title={translate('Settings')}>
      <PageContentBody>
        <PageHeading scope={translate('Settings')} title={translate('General')} />
        <IdentityCard />
      </PageContentBody>
    </PageContent>
  );
}
