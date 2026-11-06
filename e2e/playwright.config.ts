import { defineConfig, devices } from '@playwright/test';

// Runs against the real stack: `docker compose up --build` (demo model, sign-in on), then `npx playwright test`.
export default defineConfig({
  testDir: './tests',
  timeout: 90_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0, // the tests change state (bookings), so a blind retry would see its own earlier run
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env.BASE_URL ?? 'http://localhost:8080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
