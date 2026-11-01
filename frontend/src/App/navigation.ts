import type { LucideIcon } from 'lucide-react';

import { icons } from 'Helpers/Props/icons';
import type { TranslationKey } from 'Utilities/String/en';

export interface MenuEntry {
  to: string;
  label: TranslationKey;
  icon: LucideIcon;
  /** False while the page is not built: the entry is shown, marked "Soon", and is not a link. */
  available: boolean;
}

/** The menu of the application. Mnemarr's interface only configures and watches the brain (docs/SPEC.md, D-18). */
export const menuEntries: ReadonlyArray<MenuEntry> = [
  { to: '/', label: 'Overview', icon: icons.OVERVIEW, available: true },
  { to: '/sources', label: 'Sources', icon: icons.SOURCES, available: false },
  { to: '/agents', label: 'Agents', icon: icons.AGENTS, available: false },
  { to: '/settings', label: 'Settings', icon: icons.SETTINGS, available: false },
  { to: '/system/status', label: 'System', icon: icons.SYSTEM, available: true },
];
