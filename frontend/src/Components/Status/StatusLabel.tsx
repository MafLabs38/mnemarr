import type { LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';

import { icons } from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';

import styles from './StatusLabel.module.css';

/** Adding a kind is a compile error until it has its icon. */
const kindIcons: Record<Kind, LucideIcon> = {
  [kinds.SUCCESS]: icons.SUCCESS,
  [kinds.DANGER]: icons.DANGER,
};

interface StatusLabelProps {
  kind: Kind;
  children: ReactNode;
}

/** A status is never conveyed by color alone: an icon and the text come with it. */
export default function StatusLabel({ kind, children }: Readonly<StatusLabelProps>) {
  const Icon = kindIcons[kind];

  return (
    <span className={`${styles.label} ${styles[kind]}`}>
      <Icon aria-hidden="true" size={16} strokeWidth={2.5} />
      {children}
    </span>
  );
}
