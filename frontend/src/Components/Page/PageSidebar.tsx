import { NavLink } from 'react-router-dom';

import { menuEntries } from 'App/navigation';
import translate from 'Utilities/String/translate';

import styles from './PageSidebar.module.css';

export default function PageSidebar() {
  return (
    <aside className={styles.sidebar}>
      <nav aria-label={translate('MainMenu')} className={styles.menu}>
        {menuEntries.map(({ to, label, icon: Icon, available }) =>
          available ? (
            <NavLink
              className={({ isActive }) => (isActive ? styles.activeLink : styles.link)}
              end={to === '/'}
              key={to}
              to={to}
            >
              <Icon aria-hidden="true" size={18} strokeWidth={2.25} />
              <span>{translate(label)}</span>
            </NavLink>
          ) : (
            <span aria-disabled="true" className={styles.soonEntry} key={to}>
              <Icon aria-hidden="true" size={18} strokeWidth={2.25} />
              <span>{translate(label)}</span>
              <span className={styles.soonBadge}>{translate('Soon')}</span>
            </span>
          ),
        )}
      </nav>
    </aside>
  );
}
