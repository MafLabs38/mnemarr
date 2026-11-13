/** The theme tokens a color of the badge may name: the same list as the server's (ADR 0044). */
export const COLOR_TOKENS = ['accent', 'accentDeep', 'secondary', 'ink', 'inkSoft', 'inkMuted'] as const;

const TOKENS = new Set<string>(COLOR_TOKENS);

const HEX = /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i;

const DEFAULT_COLOR = 'accent';

/**
 * The CSS color for a color of the badge: a hex color as it is, a theme token as its variable. Anything else (the server never sends it) falls back to the accent,
 * so that no arbitrary CSS can reach a style.
 */
export function identityColorToCss(value: string | undefined): string {
  if (value && HEX.test(value)) {
    return value;
  }
  return `var(--${value && TOKENS.has(value) ? value : DEFAULT_COLOR})`;
}
