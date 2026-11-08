import ChatToggle from 'Chat/ChatToggle';
import { Link } from 'react-router-dom';

import BrandLogo from 'Components/BrandLogo';
import translate from 'Utilities/String/translate';

import AccountMenu from './AccountMenu';
import styles from './PageHeader.module.css';

export default function PageHeader() {
  return (
    <header className={styles.header}>
      <Link className={styles.brand} to="/">
        <BrandLogo size={36} />
        <span className={styles.name}>{translate('AppName')}</span>
      </Link>
      <div className={styles.actions}>
        <ChatToggle />
        <AccountMenu />
      </div>
    </header>
  );
}
