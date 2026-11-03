import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';

import { stubStatusOk } from '__tests__/render';
import { createQueryClient, shouldRetry } from 'App/queryClient';
import { ApiError } from 'Helpers/Api/apiGet';
import { useApiQuery } from 'Helpers/Hooks/useApiQuery';

function Probe() {
  const { data } = useApiQuery<{ version: string }>('/api/v1/system/status');
  return <p>{data?.version}</p>;
}

describe('shouldRetry', () => {
  it('tries once more after a first failure', () => {
    expect(shouldRetry(0, new Error('network'))).toBe(true);
    expect(shouldRetry(0, new ApiError(503))).toBe(true);
  });

  it('gives up after the retry', () => {
    expect(shouldRetry(1, new Error('network'))).toBe(false);
  });

  it.each([401, 403, 404])('does not ask again when the server answered %i', (status) => {
    expect(shouldRetry(0, new ApiError(status))).toBe(false);
  });
});

describe('createQueryClient', () => {
  it('does not ask the server again when the window gets the focus back', async () => {
    const fetchMock = stubStatusOk();
    render(
      <QueryClientProvider client={createQueryClient()}>
        <Probe />
      </QueryClientProvider>,
    );
    await screen.findByText('1.2.3');

    globalThis.dispatchEvent(new Event('visibilitychange'));
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
