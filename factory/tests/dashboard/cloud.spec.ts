import { test, expect } from '@playwright/test';

// The deployed board's release check (spec 22, "Proofs"): what a release can break, read without changing anything.
// FACTORY_CLOUD_HEADER is the header's run-mode text to expect (default the Azure board's).
const header = process.env.FACTORY_CLOUD_HEADER ?? 'cloud · foundry';

test.describe('a deployed board', { tag: '@cloud' }, () => {
  test('loads signed in, names its run mode, and has the six columns, Canary among them', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('#mode')).toHaveText(header);
    await expect(page.locator('[data-column]')).toHaveCount(6);
    expect(await page.locator('[data-column]').evaluateAll((cs) => cs.map((c) => (c as HTMLElement).dataset.column)))
      .toEqual(['ideas', 'approved', 'canary', 'building', 'ready', 'done']);
  });

  test('its API answers: the board, the idea config, and why a canary is due', async ({ page }) => {
    await page.goto('/');
    const board = await page.request.get('/state/board.json');
    expect(board.ok()).toBeTruthy();
    expect(Array.isArray((await board.json()).jobs)).toBeTruthy();
    const config = await (await page.request.get('/api/ideate/config')).json();
    expect(`${config.mode} · ${config.provider}`).toBe(header);
    const canary = await (await page.request.get('/api/canary')).json();
    expect(typeof canary.due).toBe('boolean');
    expect(Array.isArray(canary.reasons)).toBeTruthy();
  });

  test('the platform chip says whether the preview platform is awake, through the identity (spec 24)', async ({ page }) => {
    await page.goto('/');
    const status = await (await page.request.get('/api/platform')).json();
    expect(status.ok, status.message).toBeTruthy();
    await expect(page.locator('#platform')).toHaveText(/^platform: (awake|asleep|starting|stopping)/);
  });

  test('New idea opens with + New repo, and closes without starting anything', async ({ page }) => {
    await page.goto('/');
    await page.locator('#new-idea-btn').click();
    await expect(page.locator('#s-target option[value="+new"]')).toHaveText(/New repo/);
    await page.locator('#modal button[aria-label="Close"]').first().click();
    await expect(page.locator('#modal')).toBeHidden();
  });
});
