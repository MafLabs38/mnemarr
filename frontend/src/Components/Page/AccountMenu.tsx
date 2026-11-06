import { Link } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import { useSignOut } from 'Session/session';
import { useSession } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import styles from './AccountMenu.module.css';

/** Who is signed in, a way to their own password, and the way out. Signing out forgets everything the session had read. */
export default function AccountMenu() {
  const session = useSession();
  const signOut = useSignOut();

  return (
    <div className={styles.menu}>
      <Link className={styles.account} to="/account">
        <span className={styles.name}>{session.name}</span>
        <span className={styles.role}>{session.role}</span>
      </Link>
      <Button disabled={signOut.isPending} onClick={() => signOut.mutate()} variant="quiet">
        {translate('SignOut')}
      </Button>
      {signOut.isError && <Alert>{translate('SignOutFailed')}</Alert>}
    </div>
  );
}
