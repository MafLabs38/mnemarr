import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { stubStatusOk } from '__tests__/render';
import App from 'App/App';

function openApp(path: string) {
  window.history.pushState({}, '', path);
  return render(<App />);
}

beforeEach(() => {
  stubStatusOk();
});

describe('App routing', () => {
  it('shows the overview at the root', () => {
    openApp('/');

    expect(screen.getByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument();
  });

  it('opens the system status from its address', async () => {
    openApp('/system/status');

    expect(screen.getByRole('heading', { level: 1, name: 'Status' })).toBeInTheDocument();
    expect(await screen.findByText('1.2.3')).toBeInTheDocument();
  });

  it('sends /system to its status page, and changes the address', () => {
    openApp('/system');

    expect(screen.getByRole('heading', { level: 1, name: 'Status' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/system/status');
  });

  it('shows a page not found for an unknown address, with a way back and a title', () => {
    openApp('/nowhere');

    expect(screen.getByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to the overview' })).toHaveAttribute('href', '/');
    expect(document.title).toBe('Page not found - Mnemarr');
  });

  it('navigates from the menu', async () => {
    openApp('/');

    await userEvent.click(screen.getByRole('link', { name: 'System' }));

    expect(screen.getByRole('heading', { level: 1, name: 'Status' })).toBeInTheDocument();
  });
});
