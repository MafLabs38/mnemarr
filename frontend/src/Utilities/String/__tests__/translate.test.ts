import translate from 'Utilities/String/translate';

describe('translate', () => {
  it('fills the tokens of a text, numbers included', () => {
    expect(translate('SystemStatusError', { message: 'boom' })).toBe('Could not read the system status: boom');
    expect(translate('ServerAnswered', { status: 503 })).toBe('The server answered 503.');
  });

  it('keeps a token it was not given, so a missing value is visible', () => {
    expect(translate('SystemStatusError')).toBe('Could not read the system status: {message}');
  });
});
