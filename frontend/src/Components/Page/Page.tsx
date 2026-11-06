import type { ReactNode } from 'react';

import translate from 'Utilities/String/translate';

import styles from './Page.module.css';
import PageHeader from './PageHeader';
import PageSidebar from './PageSidebar';

interface PageProps {
  children: ReactNode;
}

export default function Page({ children }: Readonly<PageProps>) {
  return (
    <div className={styles.page}>
      <a className={styles.skipLink} href="#main">
        {translate('SkipToContent')}
      </a>
      <PageHeader />
      <div className={styles.main}>
        <PageSidebar />
        {children}
      </div>
    </div>
  );
}
