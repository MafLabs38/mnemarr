import { defineConfig, devices } from '@playwright/test';

// MNEMARR_URL: a running instance (the e2e stack by default). No browser is started by the config.
const baseURL = process.env.MNEMARR_URL ?? 'http://localhost:8383';

export default defineConfig({
  testDir: '.',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: { baseURL, trace: 'on-first-retry' },
  projects: [
    // Each browser has a fake microphone (a tone) that it gives without asking: the chat's dictation records it.
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] },
      },
    },
    // Requests that need no browser give the same answer in each: they run once, in the first project.
    {
      name: 'firefox',
      use: {
        ...devices['Desktop Firefox'],
        launchOptions: {
          firefoxUserPrefs: { 'media.navigator.streams.fake': true, 'media.navigator.permission.disabled': true },
        },
      },
      grepInvert: /@browserless/,
    },
  ],
});
