import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query';

import { ApiError } from 'Helpers/Api/apiGet';
import { sessionKey } from 'Session/session';

/** Answers that will not change by asking again. */
const FINAL_STATUSES = new Set([401, 403, 404]);

/** One retry, and none when the server has said no for good. */
export function shouldRetry(failureCount: number, error: Error): boolean {
  if (error instanceof ApiError && FINAL_STATUSES.has(error.status)) {
    return false;
  }
  return failureCount < 1;
}

export function createQueryClient() {
  // Any call that is answered 401 (an expired session, a disabled account) means nobody is signed in any more: the gate shows the sign-in page.
  const signOutOnUnauthorized = (error: Error) => {
    if (error instanceof ApiError && error.status === 401) {
      client.setQueryData(sessionKey, null);
    }
  };
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({ onError: signOutOnUnauthorized }),
    mutationCache: new MutationCache({ onError: signOutOnUnauthorized }),
    defaultOptions: { queries: { retry: shouldRetry, refetchOnWindowFocus: false } },
  });
  return client;
}
