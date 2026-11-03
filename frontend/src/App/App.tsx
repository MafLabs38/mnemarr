import { QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { BrowserRouter } from 'react-router-dom';

import Page from 'Components/Page/Page';
import SessionGate from 'Session/SessionGate';

import AppRoutes from './AppRoutes';
import { createQueryClient } from './queryClient';

export default function App() {
  const [queryClient] = useState(createQueryClient);

  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <SessionGate>
          <Page>
            <AppRoutes />
          </Page>
        </SessionGate>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
