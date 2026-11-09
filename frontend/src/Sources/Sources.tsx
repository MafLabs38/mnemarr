import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import LoadingIndicator from 'Components/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import translate from 'Utilities/String/translate';

import ConfigurationCard from './ConfigurationCard';
import ConsolidationCard from './ConsolidationCard';
import KindSection from './KindSection';
import { useSourceKinds, useSources } from './sources';

/**
 * The sources of Mnemarr, by kind: the base that must be there (the memory vault) and what may be added (knowledge vaults, the
 * documentation of applications). Each kind follows its rule; the page shows the obligations instead of leaving them to errors.
 * Only an operator reaches it.
 */
export default function Sources() {
  const kinds = useSourceKinds();
  const sources = useSources();

  return (
    <PageContent title={translate('Sources')}>
      <PageContentBody>
        <PageHeading scope={translate('Settings')} title={translate('Sources')} />
        {(kinds.isPending || sources.isPending) && <LoadingIndicator />}
        {(kinds.isError || sources.isError) && <Alert>{translate('SourcesError')}</Alert>}
        {kinds.data &&
          sources.data &&
          kinds.data.map((kind) => <KindSection key={kind.kind} kind={kind} sources={sources.data} />)}
        <Card title={translate('Consolidation')}>
          <ConsolidationCard />
        </Card>
        <Card title={translate('ConfigurationAsCode')}>
          <ConfigurationCard />
        </Card>
      </PageContentBody>
    </PageContent>
  );
}
