import { formatDateTime } from 'Utilities/Date/formatDateTime';

describe('formatDateTime', () => {
  it('shows a moment in UTC to the minute, whatever the machine', () => {
    expect(formatDateTime('2026-11-03T09:05:59+00:00')).toBe('2026-11-03 09:05');
    expect(formatDateTime('2026-11-03T10:05:00+01:00')).toBe('2026-11-03 09:05');
  });

  it('shows nothing when there is no moment', () => {
    expect(formatDateTime(null)).toBe('');
    expect(formatDateTime(undefined)).toBe('');
    expect(formatDateTime('')).toBe('');
  });
});
