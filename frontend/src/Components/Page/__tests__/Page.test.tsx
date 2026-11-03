import { screen } from '@testing-library/react';

import { renderWithProviders } from '__tests__/render';
import Page from 'Components/Page/Page';
import PageContent from 'Components/Page/PageContent';

describe('Page', () => {
  const renderPage = (route = '/') =>
    renderWithProviders(
      <Page>
        <PageContent title="Test">content</PageContent>
      </Page>,
      route,
    );

  it('lists every entry of the menu in order, as links when the page exists and as disabled entries when it does not', () => {
    renderPage();

    const menu = screen.getByRole('navigation', { name: 'Main menu' });
    expect(menu).toHaveTextContent(/Overview.*Sources.*Agents.*Settings.*System.*Accounts/s);
    expect(screen.getByRole('link', { name: 'Overview' })).toHaveAttribute('href', '/');
    expect(screen.getByRole('link', { name: 'System' })).toHaveAttribute('href', '/system/status');
    for (const name of ['Sources', 'Agents', 'Settings']) {
      expect(screen.queryByRole('link', { name: new RegExp(name) })).not.toBeInTheDocument();
      const entry = screen.getByText(name).closest('[aria-disabled="true"]');
      expect(entry).toHaveTextContent('Soon');
    }
  });

  it('marks the page being shown as the current one', () => {
    renderPage('/system/status');

    expect(screen.getByRole('link', { name: 'System' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current');
  });

  it('brings back to the overview from the product name', () => {
    renderPage('/system/status');

    expect(screen.getByRole('link', { name: 'Mnemarr' })).toHaveAttribute('href', '/');
  });

  it('starts with a link that skips to the content, which can take the focus', () => {
    renderPage();

    const skip = screen.getByRole('link', { name: 'Skip to content' });
    expect(skip).toHaveAttribute('href', '#main');
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main');
    expect(screen.getByRole('main')).toHaveAttribute('tabindex', '-1');
  });

  it('puts the title of the page in the browser tab', () => {
    renderPage();

    expect(document.title).toBe('Test - Mnemarr');
  });
});
