import { type ReactNode, useEffect } from 'react';

import translate from 'Utilities/String/translate';

import styles from './PageContent.module.css';

interface PageContentProps {
  /** Browser tab title. */
  title: string;
  children: ReactNode;
}

export default function PageContent({ title, children }: Readonly<PageContentProps>) {
  useEffect(() => {
    document.title = `${title} - ${translate('AppName')}`;
  }, [title]);

  return (
    <main className={styles.content} id="main" tabIndex={-1}>
      {children}
    </main>
  );
}
