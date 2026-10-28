import { defineConfig } from '@playwright/test'

/**
 * Drives the app that `aspire run` (or `dotnet run --project src/Warranty.AppHost`) is already serving
 * in replay mode: SPA on http://localhost:5173, claimant channels on aurora.localhost / borealis.localhost,
 * Keycloak on http://localhost:8080. Nothing is started here.
 *
 * Headed by default because it is a live demo: the browser opens full screen and the page takes the
 * screen's real size, so captions are never cut off. DEMO_HEADLESS=1 for a dry run (fixed 1440×900).
 * Every run is also recorded to test-results/ as a fallback video.
 */
const headless = process.env.DEMO_HEADLESS === '1'

export default defineConfig({
  testDir: '.',
  testMatch: '*.demo.spec.ts',
  // The narrated walkthrough is about 5½ minutes at DEMO_PACE=0.9; leave room for slow AI processing.
  timeout: 15 * 60_000,
  expect: { timeout: 30_000 },
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    channel: process.env.DEMO_BROWSER_CHANNEL ?? 'msedge',
    headless,
    // null: the page follows the (full-screen) window instead of a fixed size.
    viewport: headless ? { width: 1440, height: 900 } : null,
    ignoreHTTPSErrors: true,
    actionTimeout: 30_000,
    navigationTimeout: 60_000,
    video: { mode: 'on', size: { width: 1440, height: 900 } },
    // The window itself is switched to full screen at the start of the test.
    launchOptions: { slowMo: Number(process.env.DEMO_SLOWMO ?? (headless ? 0 : 120)) },
  },
})
