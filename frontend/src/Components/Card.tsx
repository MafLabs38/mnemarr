import { type ReactNode, useId } from 'react';

import styles from './Card.module.css';

interface CardProps {
  /** Heading of the card; it also names the region, so a screen reader can find it. */
  title?: string;
  children: ReactNode;
}

export default function Card({ title, children }: Readonly<CardProps>) {
  const headingId = useId();

  return (
    <section aria-labelledby={title ? headingId : undefined} className={styles.card}>
      {title && (
        <h2 className={styles.title} id={headingId}>
          {title}
        </h2>
      )}
      {children}
    </section>
  );
}
