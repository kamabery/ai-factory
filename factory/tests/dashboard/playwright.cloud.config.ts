import { defineConfig } from '@playwright/test';

// The release check for a deployed board (spec 22): cloud.spec.ts, read-only, against FACTORY_CLOUD_URL, signed in
// with the Entra session saved in FACTORY_CLOUD_AUTH. No simulator, no stubs, no writes, no tokens:
//   npx playwright open --save-storage=.cloud-auth.json https://<the dashboard URL>     # once: sign in, then close
//   $env:FACTORY_CLOUD_URL='https://<the dashboard URL>'; npx playwright test -c playwright.cloud.config.ts
const url = process.env.FACTORY_CLOUD_URL;
if (!url) throw new Error('set FACTORY_CLOUD_URL to the deployed dashboard URL');

export default defineConfig({
  testDir: '.',
  testMatch: 'cloud.spec.ts',
  timeout: 60_000,
  // A dashboard that scaled to zero takes a while to answer its first request.
  expect: { timeout: 30_000 },
  workers: 1,
  reporter: 'list',
  use: { baseURL: url, storageState: process.env.FACTORY_CLOUD_AUTH ?? '.cloud-auth.json', trace: 'retain-on-failure' },
});
