import { useApiQuery } from 'Helpers/Hooks/useApiQuery';

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
  return useApiQuery<AccountItem[]>('/accounts');
}

export function useAccount(id: string) {
  return useApiQuery<AccountItem>(`/accounts/${id}`);
}

/** The key of the keys of one account, for what has to refresh them. */
export const keysQueryKey = (accountId: string) => ['api', `/accounts/${accountId}/keys`] as const;

export function useApiKeys(accountId: string) {
  return useApiQuery<ApiKeyItem[]>(`/accounts/${accountId}/keys`);
}

export function useAudit(limit: number) {
  return useApiQuery<AuditItem[]>(`/audit?limit=${limit}`);
}

export function useRoles() {
  return useApiQuery<RoleItem[]>('/roles', { staleTime: Infinity });
}

/**
 * The role a new account starts with: the one that can do the least, the first of the list on a tie. The server owns the list of roles;
 * a default that could not be read from it would have to be kept in step with it.
 */
export function leastPrivilegedRole(roles: ReadonlyArray<RoleItem>): string | undefined {
  return roles.reduce<RoleItem | undefined>(
    (least, role) => (!least || role.permissions.length < least.permissions.length ? role : least),
    undefined,
  )?.name;
}
