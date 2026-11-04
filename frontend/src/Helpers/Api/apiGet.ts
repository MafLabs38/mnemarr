import translate from 'Utilities/String/translate';

/**
 * The API answered, but not with a success. The status tells the caller what to do (a 401 means "sign in again"); `problem` is the
 * reason the server gave in its problem document, when it gave one.
 * The reason is shown as the server worded it, in English: the server does not know the language of the interface.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem?: string,
    /** The reasons, when the problem document lists them (a document that cannot be applied says every error). */
    readonly errors: ReadonlyArray<string> = [],
  ) {
    super(problem ?? translate('ServerAnswered', { status }));
    this.name = 'ApiError';
  }
}

const API_BASE = '/api/v1';

/** The address of a path of the API, for what the browser fetches by itself (an image): the base is added here, like everywhere. */
export function apiUrl(path: string): string {
  return `${API_BASE}${path}`;
}

/** The header the server requires on a write that does not carry an API key (docs/adr/0016): a browser cannot add it to a form. */
const REQUEST_HEADER = 'X-Mnemarr-Request';

async function failure(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { title?: unknown; errors?: unknown };
    const errors = Array.isArray(body.errors)
      ? body.errors.filter((error): error is string => typeof error === 'string')
      : [];
    return new ApiError(response.status, typeof body.title === 'string' ? body.title : undefined, errors);
  } catch {
    // Not a problem document (a proxy's page, an empty body): the status is all there is to say.
    return new ApiError(response.status);
  }
}

/**
 * The one place the API is addressed. A URL base (docs/SPEC.md, Q-21) and the header of a write are added here, and nowhere else;
 * a 401 is handled by the query client (App/queryClient.ts), which ends the session. A success whose body is not JSON (a proxy's page) is an error, not a value.
 */
export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, { signal });
  if (!response.ok) {
    throw await failure(response);
  }
  return (await response.json()) as T;
}

/** A write to the API (POST, PUT or DELETE), with a JSON body when there is one. A 204 answers with nothing. */
export async function apiSend<T = void>(method: 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { [REQUEST_HEADER]: '1' };
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  const response = await fetch(`${API_BASE}${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    throw await failure(response);
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

/** A read whose answer is text (a YAML document), not JSON. */
export async function apiGetText(path: string, signal?: AbortSignal): Promise<string> {
  const response = await fetch(`${API_BASE}${path}`, { signal });
  if (!response.ok) {
    throw await failure(response);
  }
  return response.text();
}

/** A write whose body is YAML text, answered in JSON. */
export async function apiSendYaml<T>(path: string, yaml: string): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    method: 'PUT',
    headers: { [REQUEST_HEADER]: '1', 'Content-Type': 'application/yaml' },
    body: yaml,
  });
  if (!response.ok) {
    throw await failure(response);
  }
  return (await response.json()) as T;
}
