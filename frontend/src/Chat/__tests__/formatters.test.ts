import formatAge from '../formatAge';
import formatArguments from '../formatArguments';

describe('formatAge', () => {
  const now = new Date('2026-11-08T12:00:00Z');
  const ago = (seconds: number) => new Date(now.getTime() - seconds * 1000).toISOString();

  it.each([
    [0, 'now'],
    [59, 'now'],
    [60, '1 min'],
    [45 * 60, '45 min'],
    [3 * 3600, '3 h'],
    [47 * 3600, '47 h'],
    [48 * 3600, '2 d'],
    [5 * 86_400, '5 d'],
  ])('says %i seconds as "%s"', (seconds, expected) => {
    expect(formatAge(ago(seconds), now)).toBe(expected);
  });

  it('says "now" for a date in the future (clocks differ)', () => {
    expect(formatAge(ago(-300), now)).toBe('now');
  });
});

describe('formatArguments', () => {
  it('shows nothing as an empty object and a value as indented JSON', () => {
    expect(formatArguments(undefined)).toBe('{}');
    expect(formatArguments(null)).toBe('{}');
    expect(formatArguments({ source: 'notes' })).toBe('{\n  "source": "notes"\n}');
  });

  it('does not fail on what JSON cannot hold', () => {
    const cycle: Record<string, unknown> = {};
    cycle.self = cycle;

    expect(formatArguments(cycle)).toBe('{…}');
    expect(formatArguments(10n)).toBe('10');
  });
});
