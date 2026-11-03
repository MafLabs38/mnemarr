import { shouldRetry } from 'App/queryClient';
import { ApiError } from 'Helpers/Api/apiGet';

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
