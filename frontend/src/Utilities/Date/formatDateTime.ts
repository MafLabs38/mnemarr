/** A moment as the interface shows it: "2026-11-03 09:00", in UTC, whatever the machine's locale. Nothing when there is none. */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) {
    return '';
  }
  return new Date(iso).toISOString().slice(0, 16).replace('T', ' ');
}
