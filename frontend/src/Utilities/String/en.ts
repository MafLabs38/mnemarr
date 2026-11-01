/**
 * The texts of the interface, in English (the source language). Every text goes through translate(): none is
 * written in a component. Keys are English PascalCase, as in the *arr apps.
 */
const en = {
  About: 'About',
  Agents: 'Agents',
  ApiReachable: 'API reachable',
  BackToOverview: 'Back to the overview',
  Loading: 'Loading…',
  MainMenu: 'Main menu',
  NothingToRememberYet: 'Nothing to remember yet.',
  Overview: 'Overview',
  OverviewIntro: 'Connect a source and a collector agent, and Mnemarr starts building its memory.',
  PageNotFound: 'Page not found',
  PageNotFoundHelp: 'There is nothing at this address.',
  Runtime: 'Runtime',
  ServerAnswered: 'The server answered {status}.',
  Settings: 'Settings',
  SkipToContent: 'Skip to content',
  Soon: 'Soon',
  Sources: 'Sources',
  Started: 'Started',
  Status: 'Status',
  SystemStatusError: 'Could not read the system status: {message}',
  System: 'System',
  Unreachable: 'Unreachable',
  Uptime: 'Uptime',
  Version: 'Version',
} as const;

export type TranslationKey = keyof typeof en;

export default en;
