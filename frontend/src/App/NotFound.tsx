import { Link } from 'react-router-dom';

import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import translate from 'Utilities/String/translate';

export default function NotFound() {
  return (
    <PageContent title={translate('PageNotFound')}>
      <PageContentBody>
        <PageHeading scope="404" title={translate('PageNotFound')} />
        <p>{translate('PageNotFoundHelp')}</p>
        <Link to="/">{translate('BackToOverview')}</Link>
      </PageContentBody>
    </PageContent>
  );
}
