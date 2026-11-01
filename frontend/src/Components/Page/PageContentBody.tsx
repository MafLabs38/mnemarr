import type { ReactNode } from 'react';

import styles from './PageContentBody.module.css';

interface PageContentBodyProps {
  children: ReactNode;
}

export default function PageContentBody({ children }: Readonly<PageContentBodyProps>) {
  return <div className={styles.body}>{children}</div>;
}
