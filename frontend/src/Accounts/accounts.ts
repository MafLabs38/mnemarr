import { useQuery } from '@tanstack/react-query';

import { apiGet } from 'Helpers/Api/apiGet';

export interface AccountItem {
  id: string;
  name: string;
  kind: 'User' | 'Service';
  role: string;
  enabled: boolean;
  createdAt: string;
}

export interface RoleItem {
  name: string;
  description: string;
  permissions: string[];
  forUsers: boolean;
  forServices: boolean;
}

export interface ApiKeyItem {
  id: string;
  name: string;
  prefix: string;
  sources: string[] | null;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
}

/** A key that has just been created: the secret is in this answer and nowhere else, ever. */
export interface CreatedApiKey {
  key: ApiKeyItem;
  secret: string;
}

export interface AuditItem {
  id: number;
  at: string;
  actor: string;
  action: string;
  target: string;
  detail: string;
}

export const accountsKey = ['api', '/accounts'] as const;

export function useAccounts() {
  return useQuery({ queryKey: accountsKey, queryFn: ({ signal }) => apiGet<AccountItem[]>('/accounts', signal) });
}

export function useRoles() {
  return useQuery({
    queryKey: ['api', '/roles'],
    queryFn: ({ signal }) => apiGet<RoleItem[]>('/roles', signal),
    staleTime: Infinity,
  });
}
