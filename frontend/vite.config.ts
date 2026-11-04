/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';
import { defineConfig } from 'vite';

const src = fileURLToPath(new URL('./src', import.meta.url));

// Absolute imports from src: every top-level folder becomes an alias.
const aliases = [
  '__tests__',
  'Accounts',
  'App',
  'Components',
  'Helpers',
  'Login',
  'Notes',
  'Overview',
  'Session',
  'Sources',
  'Styles',
  'System',
  'Utilities',
].map((folder) => ({
  find: new RegExp(`^${folder}(/|$)`),
  replacement: `${src}/${folder}$1`,
}));

export default defineConfig({
  root: fileURLToPath(new URL('.', import.meta.url)),
  plugins: [react()],
  resolve: { alias: aliases },
  css: {
    modules: { localsConvention: 'camelCaseOnly' },
  },
  server: {
    port: 5173,
    // MNEMARR_API: where the backend runs (for example a dotnet watch on another port).
    proxy: {
      '/api': process.env.MNEMARR_API ?? 'http://localhost:8383',
      '/openapi': process.env.MNEMARR_API ?? 'http://localhost:8383',
    },
  },
  build: {
    outDir: fileURLToPath(new URL('../_output/UI', import.meta.url)),
    emptyOutDir: true,
    // Fonts stay files: Vite would otherwise inline the small ones as data: URIs, which the Content-Security-Policy refuses.
    assetsInlineLimit: (file) => (/\.woff2?$/.test(file) ? false : undefined),
  },
  test: {
    environment: 'jsdom',
    globals: true,
    // A test that stubs fetch cannot leak the stub into the next one.
    unstubGlobals: true,
    setupFiles: ['./src/setupTests.ts'],
    css: { modules: { classNameStrategy: 'non-scoped' } },
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov'],
      reportsDirectory: '../coverage/frontend',
      include: ['src/**/*.{ts,tsx}'],
      exclude: ['src/**/__tests__/**', 'src/index.tsx', 'src/setupTests.ts'],
      // The gate of docs/TESTING.md §4: a pull request that lets coverage fall under it fails.
      thresholds: { statements: 70, branches: 70, functions: 70, lines: 70 },
    },
  },
});
