import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { stubStatusOk } from '__tests__/render';
import App from 'App/App';

function openApp(path: string) {
  globalThis.history.pushState({}, '', path);
  return render(<App />);
}

beforeEach(() => {
  stubStatusOk();
});

describe('App routing', () => {
  it('shows the overview at the root', async () => {
    openApp('/');

    expect(await screen.findByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument();
  });

  it('opens the system status from its address', async () => {
    openApp('/system/status');

    expect(await screen.findByRole('heading', { level: 1, name: 'Status' })).toBeInTheDocument();
    expect(await screen.findByText('1.2.3')).toBeInTheDocument();
  });

  it('sends /system to its status page, and changes the address', async () => {
    openApp('/system');

    expect(await screen.findByRole('heading', { level: 1, name: 'Status' })).toBeInTheDocument();
    expect(globalThis.location.pathname).toBe('/system/status');
  });

  it('shows a page not found for an unknown address, with a way back and a title', async () => {
    openApp('/nowhere');

    expect(await screen.findByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to the overview' })).toHaveAttribute('href', '/');
    expect(document.title).toBe('Page not found - Mnemarr');
  });

  it('navigates from the menu', async () => {
    openApp('/');

    await userEvent.click(await screen.findByRole('link', { name: 'System' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Status' })).toBeInTheDocument();
  });

  it('goes back to the sign-in page when the person signs out, and shows nothing of what they had read', async () => {
    openApp('/system/status');
    expect(await screen.findByText('1.2.3')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { name: 'Sign in to Mnemarr' })).toBeInTheDocument();
    expect(screen.queryByText('1.2.3')).not.toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Main menu' })).not.toBeInTheDocument();
  });
});
