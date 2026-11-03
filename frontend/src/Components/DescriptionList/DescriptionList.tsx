import type { ReactNode } from 'react';

import styles from './DescriptionList.module.css';

export interface DescriptionListItem {
  title: string;
  data: ReactNode;
  /** Technical values (versions, timestamps) are set in the monospace font. */
  mono?: boolean;
}

interface DescriptionListProps {
  items: ReadonlyArray<DescriptionListItem>;
}

export default function DescriptionList({ items }: Readonly<DescriptionListProps>) {
  return (
    <dl className={styles.list}>
      {items.map(({ title, data, mono }) => (
        <div className={styles.row} key={title}>
          <dt className={styles.title}>{title}</dt>
          <dd className={mono ? `${styles.data} ${styles.mono}` : styles.data}>{data}</dd>
        </div>
      ))}
    </dl>
  );
}
