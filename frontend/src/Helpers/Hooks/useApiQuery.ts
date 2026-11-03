import { useQuery } from '@tanstack/react-query';

import { apiGet } from 'Helpers/Api/apiGet';

interface ApiQueryOptions {
  /** Milliseconds between two automatic refreshes. */
  refetchInterval?: number;
  /** False: nothing is asked (the person may not read it). */
  enabled?: boolean;
  /** Milliseconds during which the answer is not asked again; `Infinity` for what never changes while the page is open. */
  staleTime?: number;
}

/** Server data goes through here, never through a direct `fetch` in a component. */
export function useApiQuery<T>(path: string, options: ApiQueryOptions = {}) {
  return useQuery({ queryKey: ['api', path], queryFn: ({ signal }) => apiGet<T>(path, signal), ...options });
}

/** Whether a cached query is a read of the API at one of these paths (or below): the key is built here, so what reads it lives here too. */
export function isApiPath(query: { queryKey: readonly unknown[] }, ...prefixes: string[]): boolean {
  const path = query.queryKey[1];
  return typeof path === 'string' && prefixes.some((prefix) => path.startsWith(prefix));
}
