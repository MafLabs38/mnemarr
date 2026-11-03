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
