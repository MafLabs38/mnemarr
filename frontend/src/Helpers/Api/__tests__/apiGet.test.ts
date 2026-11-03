import { ApiError, apiGet, apiSend } from 'Helpers/Api/apiGet';

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

describe('apiGet failures', () => {
  it('gives the reason of the server when it sent a problem document', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: false,
        status: 400,
        json: () => Promise.resolve({ title: 'A password is at least 12 characters.' }),
      }),
    );

    await expect(apiGet('/x')).rejects.toMatchObject({
      status: 400,
      problem: 'A password is at least 12 characters.',
      message: 'A password is at least 12 characters.',
    });
  });

  it('says only the status when the body is not a problem document', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: false, status: 502, json: () => Promise.reject(new Error('html')) }),
    );

    await expect(apiGet('/x')).rejects.toMatchObject({
      status: 502,
      problem: undefined,
      message: 'The server answered 502.',
    });
  });

  it('ignores a title that is not text', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: false, status: 500, json: () => Promise.resolve({ title: 42 }) }),
    );

    await expect(apiGet('/x')).rejects.toMatchObject({ problem: undefined });
  });
});

describe('apiSend', () => {
  const answer = (status: number, body?: unknown) => ({ ok: status < 400, status, json: () => Promise.resolve(body) });

  it('sends a JSON body with the header that a write needs, and returns the answer', async () => {
    const fetchMock = vi.fn().mockResolvedValue(answer(201, { id: 'a1' }));
    vi.stubGlobal('fetch', fetchMock);

    const created = await apiSend<{ id: string }>('POST', '/accounts', { name: 'alice' });

    expect(created).toEqual({ id: 'a1' });
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/accounts', {
      method: 'POST',
      headers: { 'X-Mnemarr-Request': '1', 'Content-Type': 'application/json' },
      body: '{"name":"alice"}',
    });
  });

  it('sends no body and no content type when there is nothing to say', async () => {
    const fetchMock = vi.fn().mockResolvedValue(answer(204));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiSend('DELETE', '/accounts/a1')).resolves.toBeUndefined();

    expect(fetchMock).toHaveBeenCalledWith('/api/v1/accounts/a1', {
      method: 'DELETE',
      headers: { 'X-Mnemarr-Request': '1' },
      body: undefined,
    });
  });

  it('fails with the reason of the server', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: false,
        status: 409,
        json: () => Promise.resolve({ title: 'An account named x already exists.' }),
      }),
    );

    const failure = await apiSend('POST', '/accounts', {}).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 409, message: 'An account named x already exists.' });
  });
});
