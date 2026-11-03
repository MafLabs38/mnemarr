import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';

import Button from 'Components/Form/Button';
import { apiSend } from 'Helpers/Api/apiGet';
import { endSession } from 'Session/session';
import { useSession } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import styles from './AccountMenu.module.css';

/** Who is signed in, a way to their own password, and the way out. Signing out forgets everything the session had read. */
export default function AccountMenu() {
  const client = useQueryClient();
  const session = useSession();
  const signOut = useMutation({
    mutationFn: () => apiSend('POST', '/auth/logout'),
    onSettled: () => endSession(client),
  });

  return (
    <div className={styles.menu}>
      <Link className={styles.account} to="/account">
        <span className={styles.name}>{session.name}</span>
        <span className={styles.role}>{session.role}</span>
      </Link>
      <Button disabled={signOut.isPending} onClick={() => signOut.mutate()} variant="quiet">
        {translate('SignOut')}
      </Button>
    </div>
  );
}
