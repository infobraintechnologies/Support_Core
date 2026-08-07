import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './',
  testMatch: '*.spec.mjs',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: process.env.CI ? [['line'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env.CBS_SUPPORT_BROWSER_BASE_URL || 'http://127.0.0.1:5000',
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome']
  },
  outputDir: 'test-results',
  projects: [
    {
      name: 'support',
      testMatch: 'admin-support-regressions.spec.mjs',
      use: {
        ...devices['Desktop Chrome'],
        storageState: process.env.CBS_SUPPORT_BROWSER_CLIENT_STORAGE_STATE || undefined
      }
    },
    {
      name: 'admin',
      testMatch: 'admin-regressions.spec.mjs',
      use: {
        ...devices['Desktop Chrome'],
        storageState: process.env.CBS_SUPPORT_BROWSER_ADMIN_STORAGE_STATE || undefined
      }
    }
  ]
});
