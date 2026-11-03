import { useMutation, useQueryClient } from '@tanstack/react-query';

import { apiSend } from 'Helpers/Api/apiGet';
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

export interface NewAccount {
  name: string;
  kind: AccountItem['kind'];
  role: string;
  /** A person has a password; a service has keys. */
  password?: string;
}

/** Writes go through here, next to the reads and the keys they refresh: a component keeps its form and nothing of the API. */
export function useCreateAccount() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (account: NewAccount) => apiSend('POST', '/accounts', account),
    onSuccess: () => client.invalidateQueries({ queryKey: accountsKey }),
  });
}

export function useUpdateAccount(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (change: { role: string; enabled: boolean }) => apiSend('PUT', `/accounts/${id}`, change),
    onSuccess: () => client.invalidateQueries({ queryKey: accountsKey }),
  });
}

export function useDeleteAccount(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiSend('DELETE', `/accounts/${id}`),
    onSuccess: () => client.invalidateQueries({ queryKey: accountsKey }),
  });
}

/** An administrator sets the password of a person. */
export function useSetPassword(id: string) {
  return useMutation({ mutationFn: (password: string) => apiSend('PUT', `/accounts/${id}/password`, { password }) });
}

/** A person changes their own password, with the current one. */
export function useChangeOwnPassword() {
  return useMutation({
    mutationFn: (passwords: { currentPassword: string; newPassword: string }) =>
      apiSend('PUT', '/auth/password', passwords),
  });
}

export function useCreateApiKey(accountId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (key: { name: string; expiresAt?: string }) =>
      apiSend<CreatedApiKey>('POST', `/accounts/${accountId}/keys`, key),
    onSuccess: () => client.invalidateQueries({ queryKey: keysQueryKey(accountId) }),
  });
}

export function useRevokeApiKey(accountId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (keyId: string) => apiSend('DELETE', `/accounts/${accountId}/keys/${keyId}`),
    onSuccess: () => client.invalidateQueries({ queryKey: keysQueryKey(accountId) }),
  });
}
