import { identityColorToCss } from 'Identity/identityColor';

describe('identityColorToCss', () => {
  it.each([['#fff'], ['#3ee0c5'], ['#3EE0C5']])('gives a hex color as it is (%s)', (color) => {
    expect(identityColorToCss(color)).toBe(color);
  });

  it.each([['accent'], ['accentDeep'], ['secondary'], ['ink'], ['inkSoft'], ['inkMuted']])(
    'gives a theme token as its variable (%s)',
    (token) => {
      expect(identityColorToCss(token)).toBe(`var(--${token})`);
    },
  );

  it.each([[undefined], [''], ['red'], ['#ff'], ['red; background:url(x)'], ['var(--danger)'], ['paperWarm']])(
    'falls back to the accent for anything else, so that no CSS gets through (%s)',
    (value) => {
      expect(identityColorToCss(value)).toBe('var(--accent)');
    },
  );
});
