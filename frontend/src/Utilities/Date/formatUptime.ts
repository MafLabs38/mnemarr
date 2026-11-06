const MINUTE = 60;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** "42s", "5m 3s", "2h 10m", "3d 4h": the two largest units, enough to read at a glance. */
export function formatUptime(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  if (seconds >= DAY) {
    return `${Math.floor(seconds / DAY)}d ${Math.floor((seconds % DAY) / HOUR)}h`;
  }
  if (seconds >= HOUR) {
    return `${Math.floor(seconds / HOUR)}h ${Math.floor((seconds % HOUR) / MINUTE)}m`;
  }
  if (seconds >= MINUTE) {
    return `${Math.floor(seconds / MINUTE)}m ${seconds % MINUTE}s`;
  }
  return `${seconds}s`;
}
