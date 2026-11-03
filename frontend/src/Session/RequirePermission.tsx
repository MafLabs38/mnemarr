import type { ReactNode } from 'react';

import NotFound from 'App/NotFound';

import { usePermissions } from './SessionContext';

/** A page the person may not use is not there for them: the menu hides it, and the address leads nowhere. The server refuses on its own. */
export default function RequirePermission({
  permission,
  children,
}: Readonly<{ permission: string; children: ReactNode }>) {
  const { can } = usePermissions();
  return can(permission) ? children : <NotFound />;
}
