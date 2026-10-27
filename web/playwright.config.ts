import { defineConfig, devices } from '@playwright/test'

// Runs against a hub that is already up (npm run e2e starts one; see e2e/stack.mjs).
export default defineConfig({
  testDir: 'e2e',
  timeout: 300_000,
  expect: { timeout: 60_000 },
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:18092',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
