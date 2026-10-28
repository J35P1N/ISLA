import { defineConfig } from '@playwright/test';

const kind = process.env.ISLA_TEST_IMAGE ? `${process.env.ISLA_TEST_COMPOSE === '1' ? 'compose' : 'container'}-${process.env.ISLA_TEST_PLATFORM?.replaceAll('/', '-') ?? 'amd64'}` : 'local';

const arm64 = process.env.ISLA_TEST_PLATFORM === 'linux/arm64';

export default defineConfig({
  testDir: '.',
  testMatch: '**/*.spec.mjs',
  workers: 1,
  timeout: arm64 ? 240_000 : 120_000,
  expect: { timeout: arm64 ? 30_000 : 10_000 },
  outputDir: `../../TestResults/browser/${kind}`,
  reporter: [['list'], ['junit', { outputFile: `../../TestResults/browser-${kind}.xml` }]],
  use: { headless: true, actionTimeout: arm64 ? 30_000 : 15_000, navigationTimeout: arm64 ? 30_000 : 15_000, screenshot: 'only-on-failure' },
});
