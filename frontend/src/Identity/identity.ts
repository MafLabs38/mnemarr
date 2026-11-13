import { useMutation, useQueryClient } from '@tanstack/react-query';

import { apiSend, apiUpload } from 'Helpers/Api/apiGet';
import { useApiQuery } from 'Helpers/Hooks/useApiQuery';

/** The colors of the two texts of the badge: a hex color (`#rgb`, `#rrggbb`) or a theme token name (accent, ink, inkMuted…). */
export interface IdentityColors {
  name: string;
  version: string;
}

/** How this Mnemarr presents itself at the bottom of the menu (ADR 0044). */
export interface Identity {
  /** The name of this Mnemarr; "Mnemarr" when nothing else is set. */
  name: string;
  /** The custom logo, with a hash of the file in its address; null for Mnemarr's own. */
  logoUrl: string | null;
  /** The version of the running Mnemarr. */
  version: string;
  /** Why a logo that was given is not used. */
  logoProblem: string | null;
  colors: IdentityColors;
}

const path = '/identity';

/** The same for everyone who is signed in, and it changes only when an administrator changes it: it is asked once. */
export function useIdentity() {
  return useApiQuery<Identity>(path, { staleTime: Infinity });
}

function useIdentityMutation<V>(send: (value: V) => Promise<Identity>) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: send,
    gcTime: 0,
    onSuccess: (identity) => client.setQueryData(['api', path], identity),
  });
}

/** Only what is given is sent: a part that is missing stays as it is, an empty one goes back to the default. */
export function useSetIdentity() {
  return useIdentityMutation((change: { name?: string; colors?: Partial<IdentityColors> }) =>
    apiSend<Identity>('PUT', path, change),
  );
}

export function useUploadLogo() {
  return useIdentityMutation((file: File) => apiUpload<Identity>(`${path}/logo`, file));
}

export function useRemoveLogo() {
  return useIdentityMutation(() => apiSend<Identity>('DELETE', `${path}/logo`));
}
