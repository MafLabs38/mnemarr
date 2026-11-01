import { QueryClient } from '@tanstack/react-query';

import { ApiError } from 'Helpers/Api/apiGet';

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
  return new QueryClient({ defaultOptions: { queries: { retry: shouldRetry, refetchOnWindowFocus: false } } });
}
