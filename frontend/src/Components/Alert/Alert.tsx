import type { ReactNode } from 'react';

import styles from './Alert.module.css';

interface AlertProps {
  children: ReactNode;
}

/** An error the user should know about; announced by screen readers. */
export default function Alert({ children }: Readonly<AlertProps>) {
  return (
    <p className={styles.alert} role="alert">
      {children}
    </p>
  );
}
