import en, { type TranslationKey } from './en';

/**
 * Looks a text up by key and fills its `{name}` tokens. The dictionary is bundled for now; it is the seam where
 * localization files served by the backend (and French) plug in, like the *arr apps.
 */
export default function translate(key: TranslationKey, tokens: Readonly<Record<string, string | number>> = {}): string {
  return en[key].replace(/\{(\w+)\}/g, (match, name: string) => (name in tokens ? String(tokens[name]) : match));
}
