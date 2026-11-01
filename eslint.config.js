import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import simpleImportSort from 'eslint-plugin-simple-import-sort';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['_output/**', 'coverage/**', 'playwright-report/**', 'test-results/**', 'collector/**', 'reports/**'] },
  js.configs.recommended,
  ...tseslint.configs.strict,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      globals: { ...globals.browser, ...globals.node },
    },
    plugins: {
      'react-hooks': reactHooks,
      'simple-import-sort': simpleImportSort,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'simple-import-sort/imports': [
        'error',
        // Side effects, packages, absolute imports from src, then relative imports.
        {
          groups: [['^\\u0000'], ['^@?\\w'], ['^(__tests__|App|Components|Helpers|Overview|Styles|System|Utilities)(/|$)'], ['^\\.']],
        },
      ],
      'simple-import-sort/exports': 'error',
      'no-restricted-syntax': [
        'error',
        {
          selector: 'ExportDefaultDeclaration > ArrowFunctionExpression',
          message: 'Use a named default export: export default function Name().',
        },
      ],
    },
  },
);
