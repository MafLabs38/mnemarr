/** The `kind` vocabulary of the *arr apps. Only the kinds in use are listed; the others join when a component needs them. */
export const kinds = {
  DANGER: 'danger',
  INFO: 'info',
  SUCCESS: 'success',
  WARNING: 'warning',
} as const;

export type Kind = (typeof kinds)[keyof typeof kinds];
