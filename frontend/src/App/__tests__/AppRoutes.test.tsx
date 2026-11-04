import { screen } from '@testing-library/react';

import { readerSession, renderWithProviders, stubApi } from '__tests__/render';
import AppRoutes from 'App/AppRoutes';

describe('AppRoutes', () => {
  it.each(['/accounts', '/accounts/s3/keys', '/system/status', '/sources'])(
    'leads nowhere for a person who may not use the page, and asks the API for nothing: %s',
    async (path) => {
      const fetchMock = stubApi(() => ({ status: 403 }));

      renderWithProviders(<AppRoutes />, path, readerSession);

      expect(await screen.findByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument();
      expect(fetchMock).not.toHaveBeenCalled();
    },
  );
});
