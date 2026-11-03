import { type QueryClient, useQuery } from '@tanstack/react-query';

import { ApiError, apiGet } from 'Helpers/Api/apiGet';

/** Who is signed in and what they may do. The interface shows or hides by `permissions`; the server decides on its own. */
export interface Session {
  accountId: string;
  name: string;
  kind: 'User' | 'Service';
  role: string;
  permissions: string[];
}

export const sessionKey = ['api', '/auth/session'] as const;

/** The permissions of the specification (docs/adr/0016), as the interface asks about them. */
export const permissions = {
  systemRead: 'system.read',
  accountsManage: 'accounts.manage',
} as const;

/** The session, or null when nobody is signed in (the API answers 401). Any other failure is an error. */
export async function fetchSession(signal?: AbortSignal): Promise<Session | null> {
  try {
    return await apiGet<Session>('/auth/session', signal);
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      return null;
    }
    throw error;
  }
}

/** Asks who is signed in once; the answer changes only by signing in or out, or when a call is answered 401. */
export function useSessionQuery() {
  return useQuery({ queryKey: sessionKey, queryFn: ({ signal }) => fetchSession(signal), staleTime: Infinity });
}

/** Forgets everything that was read for the previous session: whoever signs in next, on this tab, must not see it. */
export function forgetSessionData(client: QueryClient) {
  client.removeQueries({ predicate: (query) => query.queryKey[1] !== sessionKey[1] });
}

/** Nobody is signed in any more. Nobody first, so that the gate leaves the pages; then what they had read goes. */
export function endSession(client: QueryClient) {
  client.setQueryData(sessionKey, null);
  forgetSessionData(client);
}
