import { useApiQuery } from 'Helpers/Hooks/useApiQuery';

export interface SystemStatus {
  version: string;
  runtimeVersion: string;
  startedAt: string;
  uptimeSeconds: number;
}

export const STATUS_REFRESH_MS = 30_000;

export function useSystemStatus() {
  return useApiQuery<SystemStatus>('/system/status', { refetchInterval: STATUS_REFRESH_MS });
}
