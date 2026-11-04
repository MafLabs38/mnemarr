import { type QueryClient, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { ApiError, apiGet, apiSend } from 'Helpers/Api/apiGet';

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
  auditRead: 'audit.read',
  sourcesManage: 'sources.manage',
  search: 'search',
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
  // The mutations remember what they were given and what they returned: a password, the secret of a key. None of it outlives the session.
  client.getMutationCache().clear();
}

/** Nobody is signed in any more. Nobody first, so that the gate leaves the pages; then what they had read goes. */
export function endSession(client: QueryClient) {
  client.setQueryData(sessionKey, null);
  forgetSessionData(client);
}

export function useSignIn() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (credentials: { name: string; password: string }) =>
      apiSend<Session>('POST', '/auth/login', credentials),
    onSuccess: (session) => {
      // Whatever an earlier session left in this tab is not for the person who signs in now.
      forgetSessionData(client);
      client.setQueryData(sessionKey, session);
    },
  });
}

/** Signs out. The interface leaves the session only once the server has ended it: a failed call would leave it valid, and a reload would sign the person back in. */
export function useSignOut() {
  const client = useQueryClient();
  // A 401 says the session was over already: the query client ends it, like for any call.
  return useMutation({ mutationFn: () => apiSend('POST', '/auth/logout'), onSuccess: () => endSession(client) });
}
