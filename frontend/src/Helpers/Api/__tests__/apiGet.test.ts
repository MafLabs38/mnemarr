import { ApiError, apiGet } from 'Helpers/Api/apiGet';

describe('apiGet', () => {
  it('reads JSON from the API under /api/v1 and passes the signal on', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({ hello: 'world' }) });
    vi.stubGlobal('fetch', fetchMock);
    const signal = new AbortController().signal;

    const body = await apiGet<{ hello: string }>('/things', signal);

    expect(body).toEqual({ hello: 'world' });
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/things', { signal });
  });

  it('throws an ApiError carrying the status when the server says no', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 404 }));

    const failure = await apiGet('/things').catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 404, name: 'ApiError', message: 'The server answered 404.' });
  });
});
