import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { type ApiAnswer, defaultIdentity, renderWithProviders, stubApi } from '__tests__/render';
import type { Identity } from 'Identity/identity';
import Settings from 'Settings/Settings';

const lab: Identity = {
  name: 'Example Lab',
  logoUrl: '/api/v1/identity/logo?v=abc',
  version: '0.6.0',
  logoProblem: null,
  colors: { name: 'accent', version: '#3ee0c5' },
};

interface Call {
  method: string;
  path: string;
  body?: unknown;
}

function serve(identity: Identity, answer: (call: Call) => ApiAnswer | undefined = () => undefined) {
  const calls: Call[] = [];
  const fetchMock = stubApi((method, path, init) => {
    const call = { method, path, body: init?.body };
    if (method !== 'GET') {
      calls.push(call);
    }
    const special = answer(call);
    if (special) {
      return special;
    }
    if (path === '/identity') {
      return { body: method === 'GET' ? identity : { ...identity, name: 'Changed' } };
    }
    if (path === '/identity/logo') {
      return { body: { ...identity, logoUrl: method === 'PUT' ? '/api/v1/identity/logo?v=new' : null } };
    }
    return undefined;
  });
  return { calls, fetchMock };
}

/** The card, once the identity has been read (until then it says "Loading…"). */
async function card() {
  await screen.findByLabelText('Name');
  return screen.getByRole('region', { name: 'Identity' });
}

describe('Settings', () => {
  it('says what the identity is for and shows what is set', async () => {
    serve(lab);
    renderWithProviders(<Settings />, '/settings');

    const identity = await card();

    expect(screen.getByRole('heading', { level: 1, name: 'General' })).toBeInTheDocument();
    expect(within(identity).getByText(/what tells this Mnemarr from another/)).toBeInTheDocument();
    expect(within(identity).getByLabelText('Name')).toHaveValue('Example Lab');
    expect(within(identity).getByLabelText('Color of the name')).toHaveValue('accent');
    expect(within(identity).getByLabelText('Color of the version')).toHaveValue('#3ee0c5');
  });

  it('keeps the button off until something changes, then sends the name and the colors', async () => {
    const { calls } = serve(lab);
    renderWithProviders(<Settings />, '/settings');
    const identity = await card();
    const save = within(identity).getByRole('button', { name: 'Save the identity' });
    expect(save).toBeDisabled();

    await userEvent.clear(within(identity).getByLabelText('Name'));
    await userEvent.type(within(identity).getByLabelText('Name'), ' Lab 2 ');
    await userEvent.clear(within(identity).getByLabelText('Color of the name'));
    await userEvent.type(within(identity).getByLabelText('Color of the name'), '#fff');
    await userEvent.click(save);

    expect(await within(identity).findByText('Saved.')).toBeInTheDocument();
    expect(calls).toEqual([
      {
        method: 'PUT',
        path: '/identity',
        body: JSON.stringify({ name: 'Lab 2', colors: { name: '#fff', version: '#3ee0c5' } }),
      },
    ]);
  });

  it('sends the image as a form with the field file, and nothing else when the texts did not change', async () => {
    const { calls } = serve(lab);
    renderWithProviders(<Settings />, '/settings');
    const identity = await card();
    const image = new File(['png'], 'lab.png', { type: 'image/png' });

    await userEvent.upload(identity.querySelector<HTMLInputElement>('input[type=file]') as HTMLInputElement, image);
    await userEvent.click(within(identity).getByRole('button', { name: 'Save the identity' }));

    expect(await within(identity).findByText('Saved.')).toBeInTheDocument();
    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({ method: 'PUT', path: '/identity/logo' });
    expect((calls[0]?.body as FormData).get('file')).toBe(image);
    expect(within(identity).getByRole('button', { name: 'Save the identity' })).toBeDisabled();
  });

  it('only accepts the kinds of image that the server accepts', async () => {
    serve(lab);
    renderWithProviders(<Settings />, '/settings');
    const identity = await card();

    expect(identity.querySelector('input[type=file]')).toHaveAttribute('accept', expect.stringContaining('.svg'));
    expect(within(identity).getByText(/png, webp or svg of at most 1 MiB/)).toBeInTheDocument();
  });

  it('says why the server refused, in its own words, and does not say saved', async () => {
    serve(lab, ({ method, path }) =>
      method === 'PUT' && path === '/identity/logo'
        ? { status: 400, body: { title: 'The file is not a valid png image.' } }
        : undefined,
    );
    renderWithProviders(<Settings />, '/settings');
    const identity = await card();

    await userEvent.upload(
      identity.querySelector<HTMLInputElement>('input[type=file]') as HTMLInputElement,
      new File(['x'], 'lab.png', { type: 'image/png' }),
    );
    await userEvent.click(within(identity).getByRole('button', { name: 'Save the identity' }));

    expect(await within(identity).findByRole('alert')).toHaveTextContent('The file is not a valid png image.');
    expect(within(identity).queryByText('Saved.')).not.toBeInTheDocument();
  });

  it('does not say saved when the name was saved and the image was refused, and keeps what was typed', async () => {
    const { calls } = serve(lab, ({ method, path }) =>
      method === 'PUT' && path === '/identity/logo'
        ? { status: 400, body: { title: 'The file is not a valid png image.' } }
        : undefined,
    );
    renderWithProviders(<Settings />, '/settings');
    const identity = await card();

    await userEvent.clear(within(identity).getByLabelText('Name'));
    await userEvent.type(within(identity).getByLabelText('Name'), 'Lab 3');
    await userEvent.upload(
      identity.querySelector<HTMLInputElement>('input[type=file]') as HTMLInputElement,
      new File(['x'], 'lab.png', { type: 'image/png' }),
    );
    await userEvent.click(within(identity).getByRole('button', { name: 'Save the identity' }));

    expect(await within(identity).findByRole('alert')).toHaveTextContent('not a valid png');
    expect(within(identity).queryByText('Saved.')).not.toBeInTheDocument();
    expect(calls.map((call) => call.path)).toEqual(['/identity', '/identity/logo']);
  });

  it('stops saying saved as soon as something is edited again', async () => {
    serve(lab);
    renderWithProviders(<Settings />, '/settings');
    const identity = await card();
    await userEvent.type(within(identity).getByLabelText('Name'), '!');
    await userEvent.click(within(identity).getByRole('button', { name: 'Save the identity' }));
    expect(await within(identity).findByText('Saved.')).toBeInTheDocument();

    await userEvent.type(within(identity).getByLabelText('Name'), '?');

    expect(within(identity).queryByText('Saved.')).not.toBeInTheDocument();
  });

  it('goes back to the default logo, and offers that only when a logo is set', async () => {
    const { calls } = serve(lab);
    const first = renderWithProviders(<Settings />, '/settings');
    const identity = await card();

    await userEvent.click(within(identity).getByRole('button', { name: 'Use the default logo' }));

    await waitFor(() => expect(calls).toEqual([expect.objectContaining({ method: 'DELETE', path: '/identity/logo' })]));
    first.unmount();

    serve(defaultIdentity);
    renderWithProviders(<Settings />, '/settings');
    await card();

    expect(screen.queryByRole('button', { name: 'Use the default logo' })).not.toBeInTheDocument();
  });

  it('warns that a logo that was given is not used, and why', async () => {
    serve({ ...lab, logoUrl: null, logoProblem: 'The logo file of the settings is not there.' });
    renderWithProviders(<Settings />, '/settings');
    await card();

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'The logo is not used: The logo file of the settings is not there.',
    );
  });
});
