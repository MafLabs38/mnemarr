import js from '@eslint/js';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import reactHooks from 'eslint-plugin-react-hooks';
import simpleImportSort from 'eslint-plugin-simple-import-sort';
import sonarjs from 'eslint-plugin-sonarjs';
import unicorn from 'eslint-plugin-unicorn';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['_output/**', 'coverage/**', 'playwright-report/**', 'test-results/**', 'collector/**', 'reports/**'] },
  js.configs.recommended,
  ...tseslint.configs.strict,
  // The rules of the SonarQube server, in the editor and in `yarn lint`: SonarJS is its engine (S3776, S5852, S5332, S1313...).
  sonarjs.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      globals: { ...globals.browser, ...globals.node },
    },
    plugins: {
      'jsx-a11y': jsxA11y,
      'react-hooks': reactHooks,
      'simple-import-sort': simpleImportSort,
      unicorn,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // Sonar's S77xx rules come from this plugin: each line is the rule of the profile it stands for.
      'unicorn/no-negated-condition': 'error', // S7735
      'unicorn/no-useless-spread': 'error', // S7747
      'unicorn/prefer-at': 'error', // S7755
      'unicorn/prefer-code-point': 'error', // S7758
      'unicorn/prefer-global-this': 'error', // S7764
      'unicorn/prefer-string-raw': 'error', // S7780
      'unicorn/prefer-string-replace-all': 'error', // S7781
      'unicorn/prefer-top-level-await': 'error', // S7785
      // The accessibility rules of the profile (S6807-S6853) are the recommended set of jsx-a11y.
      ...jsxA11y.flatConfigs.recommended.rules,
      'simple-import-sort/imports': [
        'error',
        // Side effects, packages, absolute imports from src, then relative imports.
        {
          groups: [
            [String.raw`^\u0000`],
            [String.raw`^@?\w`],
            ['^(__tests__|App|Components|Helpers|Overview|Styles|System|Utilities)(/|$)'],
            [String.raw`^\.`],
          ],
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
  {
    // Test inputs use example addresses and fakes keep mutable statics, as the repository's rules ask.
    files: ['**/__tests__/**', '**/*.test.{ts,tsx}', 'tests/**'],
    rules: {
      'sonarjs/no-hardcoded-ip': 'off',
      'sonarjs/no-clear-text-protocols': 'off',
      'sonarjs/public-static-readonly': 'off',
    },
  },
);
