import { formatUptime } from 'Utilities/Date/formatUptime';

describe('formatUptime', () => {
  it.each([
    [0, '0s'],
    [59, '59s'],
    [60, '1m 0s'],
    [3599, '59m 59s'],
    [3600, '1h 0m'],
    [86399, '23h 59m'],
    [86400, '1d 0h'],
    [273600, '3d 4h'],
  ])('formats %i seconds as %s', (seconds, expected) => {
    expect(formatUptime(seconds)).toBe(expected);
  });

  it('never goes below zero', () => {
    expect(formatUptime(-5)).toBe('0s');
  });

  it('drops the fraction of a second', () => {
    expect(formatUptime(61.9)).toBe('1m 1s');
  });
});
