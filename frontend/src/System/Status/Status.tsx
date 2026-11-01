import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import translate from 'Utilities/String/translate';

import About from './About';

export default function Status() {
  return (
    <PageContent title={translate('Status')}>
      <PageContentBody>
        <PageHeading scope={translate('System')} title={translate('Status')} />
        <About />
      </PageContentBody>
    </PageContent>
  );
}
