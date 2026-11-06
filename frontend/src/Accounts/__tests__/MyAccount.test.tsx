import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { readerSession, renderWithProviders, sentBody, stubApi } from '__tests__/render';
import MyAccount from 'Accounts/MyAccount';

async function change(current: string, next: string) {
  await userEvent.type(screen.getByLabelText('Current password'), current);
  await userEvent.type(screen.getByLabelText('New password'), next);
  await userEvent.click(screen.getByRole('button', { name: 'Save' }));
}

describe('MyAccount', () => {
  it('names the person and sends the current and the new password, then empties the form', async () => {
    const fetchMock = stubApi(() => ({ status: 204 }));
    renderWithProviders(<MyAccount />, '/account', readerSession);
    expect(screen.getByText('reader')).toBeInTheDocument();

    await change('the old password', 'a brand new password');

    expect(await screen.findByText('Your password was changed.')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/auth/password', expect.objectContaining({ method: 'PUT' }));
    expect(sentBody(fetchMock.mock.calls[0] as unknown[])).toEqual({
      currentPassword: 'the old password',
      newPassword: 'a brand new password',
    });
    await waitFor(() => expect(screen.getByLabelText('Current password')).toHaveValue(''));
  });

  it('shows why a password was refused and does not say it was changed', async () => {
    stubApi(() => ({ status: 400, body: { title: 'The current password is wrong.' } }));
    renderWithProviders(<MyAccount />);

    await change('wrong', 'a brand new password');

    expect(await screen.findByRole('alert')).toHaveTextContent('The current password is wrong.');
    expect(screen.queryByText('Your password was changed.')).not.toBeInTheDocument();
  });
});
