import { QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { BrowserRouter } from 'react-router-dom';

import Page from 'Components/Page/Page';

import AppRoutes from './AppRoutes';
import { createQueryClient } from './queryClient';

export default function App() {
  const [queryClient] = useState(createQueryClient);

  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Page>
          <AppRoutes />
        </Page>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
