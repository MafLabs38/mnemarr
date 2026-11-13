import { fireEvent, screen } from '@testing-library/react';

import { adminSession, defaultIdentity, readerSession, renderWithProviders, stubApi } from '__tests__/render';
import IdentityBadge from 'Components/Page/IdentityBadge';
import type { Identity } from 'Identity/identity';

const lab: Identity = {
  name: 'MafLabs',
  logoUrl: '/api/v1/identity/logo?v=abc',
  version: '0.6.0',
  logoProblem: null,
  colors: { name: '#3ee0c5', version: 'inkMuted' },
};

function serve(identity: Identity, systemVersion = '0.6.1') {
  return stubApi((_method, path) => {
    if (path === '/identity') {
      return { body: identity };
    }
    if (path === '/system/status') {
      return {
        body: { version: systemVersion, runtimeVersion: '10', startedAt: '2026-11-13T09:00:00Z', uptimeSeconds: 5 },
      };
    }
    return undefined;
  });
}

describe('IdentityBadge', () => {
  it("shows the product's own name, drawing and version when nothing else is set", async () => {
    serve(defaultIdentity, '1.2.3');
    const { container } = renderWithProviders(<IdentityBadge />);

    expect(await screen.findByText('Mnemarr')).toHaveStyle({ color: 'var(--accent)' });
    expect(await screen.findByText('v1.2.3')).toHaveStyle({ color: 'var(--accent)' });
    expect(container.querySelector('img')).toHaveAttribute('src', '/logo.png');
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('shows the name, the logo and the version of the lab, in the colors that are set', async () => {
    serve(lab);
    renderWithProviders(<IdentityBadge />);

    const name = await screen.findByText('MafLabs');
    expect(name).toHaveStyle({ color: '#3ee0c5' });
    expect(screen.getByRole('img', { name: 'MafLabs' })).toHaveAttribute('src', '/api/v1/identity/logo?v=abc');
    expect(await screen.findByText('v0.6.1')).toHaveStyle({ color: 'var(--inkMuted)' });
    expect(name.closest('div')).toHaveAttribute('title', 'MafLabs');
  });

  it('follows the version of the system status where the role may read it, and keeps the one of the identity where it may not', async () => {
    serve(lab, '0.6.1');
    const admin = renderWithProviders(<IdentityBadge />, '/', adminSession);
    expect(await screen.findByText('v0.6.1')).toBeInTheDocument();
    admin.unmount();

    const fetchMock = serve(lab, '0.6.1');
    renderWithProviders(<IdentityBadge />, '/', readerSession);

    expect(await screen.findByText('v0.6.0')).toBeInTheDocument();
    expect(fetchMock.mock.calls.map(([url]) => String(url))).not.toContain('/api/v1/system/status');
  });

  it('shows no version when the server has none to say', async () => {
    serve({ ...lab, version: '' }, '');
    renderWithProviders(<IdentityBadge />);

    await screen.findByText('MafLabs');

    expect(screen.queryByText(/^v/)).not.toBeInTheDocument();
  });

  it('falls back to the drawing of the product when the logo cannot be loaded, and says nothing about it', async () => {
    serve(lab);
    const { container } = renderWithProviders(<IdentityBadge />);

    fireEvent.error(await screen.findByRole('img', { name: 'MafLabs' }));

    expect(screen.queryByRole('img', { name: 'MafLabs' })).not.toBeInTheDocument();
    expect(container.querySelector('img')).toHaveAttribute('src', '/logo.png');
    expect(screen.getByText('MafLabs')).toBeInTheDocument();
  });

  it('draws nothing while the identity is being read', () => {
    stubApi(() => new Promise<never>(() => undefined) as never);
    const { container } = renderWithProviders(<IdentityBadge />);

    expect(container).toBeEmptyDOMElement();
  });
});
