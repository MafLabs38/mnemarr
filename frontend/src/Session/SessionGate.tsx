import type { ReactNode } from 'react';

import Alert from 'Components/Alert/Alert';
import LoadingIndicator from 'Components/LoadingIndicator';
import Login from 'Login/Login';
import translate from 'Utilities/String/translate';

import { useSessionQuery } from './session';
import { SessionProvider } from './SessionContext';

/**
 * Nothing of the application is shown, and nothing is asked of the API, until somebody is signed in. The session is asked once;
 * any answer 401 afterwards (an expired cookie, a disabled account) puts it back to "nobody" and the sign-in page returns.
 */
export default function SessionGate({ children }: Readonly<{ children: ReactNode }>) {
  const { data, isError, isPending } = useSessionQuery();

  if (isPending) {
    return <LoadingIndicator />;
  }
  if (isError) {
    return <Alert>{translate('SessionError')}</Alert>;
  }
  return data ? <SessionProvider session={data}>{children}</SessionProvider> : <Login />;
}
