import BrandLogo from 'Components/BrandLogo';
import Card from 'Components/Card';
import LoadingIndicator from 'Components/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import StatusLabel from 'Components/Status/StatusLabel';
import { kinds } from 'Helpers/Props/kinds';
import { useSystemStatus } from 'System/Status/useSystemStatus';
import translate from 'Utilities/String/translate';

import styles from './Overview.module.css';

/**
 * Whether the API answers. Not whether Mnemarr can do its job: that is the health of its checks (docs/SPEC.md,
 * F-HLT-01), which replaces this label when it exists. One state at a time: a failed refresh keeps the last data
 * in the query, so the error is looked at first.
 */
function ApiState() {
  const { data, isError } = useSystemStatus();

  if (isError) {
    return (
      <output>
        <StatusLabel kind={kinds.DANGER}>{translate('Unreachable')}</StatusLabel>
      </output>
    );
  }
  if (data) {
    return (
      <output>
        <StatusLabel kind={kinds.SUCCESS}>{translate('ApiReachable')}</StatusLabel>
      </output>
    );
  }
  return <LoadingIndicator />;
}

/** The home page. It is the one place, with the empty states, where the product may be playful. */
export default function Overview() {
  return (
    <PageContent title={translate('Overview')}>
      <PageContentBody>
        <PageHeading scope="Mnemarr" title={translate('Overview')} />
        <Card>
          <div className={styles.hero}>
            <div className={styles.logo}>
              <BrandLogo size={200} />
            </div>
            <div className={styles.text}>
              <ApiState />
              <p className={styles.headline}>{translate('NothingToRememberYet')}</p>
              <p className={styles.intro}>{translate('OverviewIntro')}</p>
            </div>
          </div>
        </Card>
      </PageContentBody>
    </PageContent>
  );
}
