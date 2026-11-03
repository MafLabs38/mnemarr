import { CircleCheck, CircleX, GitBranch, LayoutDashboard, Radio, Server, Settings, Users } from 'lucide-react';

/** The only place icons are chosen: components import names from here, never from the icon library. */
export const icons = {
  ACCOUNTS: Users,
  AGENTS: Radio,
  OVERVIEW: LayoutDashboard,
  SETTINGS: Settings,
  SOURCES: GitBranch,
  SYSTEM: Server,
  SUCCESS: CircleCheck,
  DANGER: CircleX,
} as const;
