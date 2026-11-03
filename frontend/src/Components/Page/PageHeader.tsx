import { Link } from 'react-router-dom';

import BrandLogo from 'Components/BrandLogo';

import styles from './PageHeader.module.css';

export default function PageHeader() {
  return (
    <header className={styles.header}>
      <Link className={styles.brand} to="/">
        <BrandLogo size={36} />
        <span className={styles.name}>Mnemarr</span>
      </Link>
    </header>
  );
}
