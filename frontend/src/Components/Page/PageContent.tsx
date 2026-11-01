import { type ReactNode, useEffect } from 'react';

import styles from './PageContent.module.css';

interface PageContentProps {
  /** Browser tab title. */
  title: string;
  children: ReactNode;
}

export default function PageContent({ title, children }: Readonly<PageContentProps>) {
  useEffect(() => {
    document.title = `${title} - Mnemarr`;
  }, [title]);

  return (
    <main className={styles.content} id="main" tabIndex={-1}>
      {children}
    </main>
  );
}
