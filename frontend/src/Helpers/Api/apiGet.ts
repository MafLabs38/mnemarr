import translate from 'Utilities/String/translate';

/** The API answered, but not with a success. The status tells the caller what to do (a 401 will mean "sign in again"). */
export class ApiError extends Error {
  constructor(readonly status: number) {
    super(translate('ServerAnswered', { status }));
    this.name = 'ApiError';
  }
}

const API_BASE = '/api/v1';

/**
 * The one place the API is addressed. A URL base (docs/SPEC.md, Q-21), an authentication header and the handling of a
 * 401 are added here, and nowhere else. A success whose body is not JSON (a proxy's page) is an error, not a value.
 */
export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, { signal });
  if (!response.ok) {
    throw new ApiError(response.status);
  }
  return (await response.json()) as T;
}
