import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { simDir, factoryEnv } from './playwright.config';

const repo = path.resolve(import.meta.dirname, '../../..');

type Job = { id: string; stage: string; round: number; history: { from: string; to: string; by: string }[] };
const board = () => JSON.parse(readFileSync(path.join(simDir, 'board.json'), 'utf8'));
const job = (id: string): Job => board().jobs.find((j: Job) => j.id === id);

const column = (page: Page, name: string) => page.locator(`[data-column="${name}"]`);
const card = (page: Page, id: string) => page.locator(`[data-card="${id}"]`);

test.describe.serial('factory dashboard', () => {
  test.beforeAll(() => {
    execFileSync('pwsh', ['-NoProfile', '-File', path.join(repo, 'factory/scripts/simulate.ps1'), '-Reset', '-Speed', '200'], {
      env: { ...process.env, ...factoryEnv },
      stdio: 'inherit',
    });
  });

  test('the simulated run lands every card where the contract says', async ({ page }) => {
    await page.goto('/');
    await expect(column(page, 'done').locator('[data-card="J-001"]')).toBeVisible();

    const j2 = column(page, 'ready').locator('[data-card="J-002"]');
    await expect(j2).toBeVisible();
    await expect(j2.locator('[data-flag]')).toHaveText(/max-rounds/);
    await expect(j2.locator('[data-review="ui"]')).toHaveText(/CHANGES/);

    const j3 = column(page, 'ready').locator('[data-card="J-003"]');
    await expect(j3.locator('[data-flag]')).toHaveText(/merge-blocked/);

    await expect(column(page, 'backlog').locator('[data-card="J-005"]')).toBeVisible();
    await expect(card(page, 'J-004')).toHaveCount(0); // discarded
    await expect(column(page, 'ideas').locator('[data-idea="I-003"]')).toContainText(/failed/i);

    await expect(card(page, 'J-001').locator('[data-review="security"]')).toHaveText(/PASS/);
    await expect(card(page, 'J-001').locator('[data-cost]')).toBeVisible();
  });

  test('the drawer shows the timeline, artifacts and gate result', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-001').click();
    const drawer = page.locator('#drawer');
    await expect(drawer).toBeVisible();
    expect(await drawer.locator('[data-timeline] li').count()).toBeGreaterThanOrEqual(10);
    await expect(drawer.locator('[data-gate]')).toContainText('test=0');

    await drawer.locator('a[data-file="spec.md"]').click();
    await expect(page.locator('#viewer')).toContainText('# Spec: J-001');
  });

  test('the pipeline strip has a node for every agent and the gate', async ({ page }) => {
    await page.goto('/');
    for (const node of ['spec-writer', 'builder', 'security-reviewer', 'code-reviewer', 'ux-reviewer', 'ui-reviewer', 'approver', 'gate']) {
      await expect(page.locator(`[data-node="${node}"]`)).toBeVisible();
    }
  });

  test('submitting an idea adds a card to Ideas', async ({ page }) => {
    await page.goto('/');
    await page.locator('#idea-text').fill('Show a count of books in the header');
    await page.locator('#idea-submit').click();
    await expect(column(page, 'ideas')).toContainText('Show a count of books in the header');
    expect(board().ideas.some((i: { text: string }) => i.text === 'Show a count of books in the header')).toBe(true);
  });

  test('editing a backlog spec in the drawer saves it to disk', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-005').click();
    const editor = page.locator('#spec-editor');
    await expect(editor).toHaveValue(/# Spec: J-005/);
    await editor.fill('# Spec: J-005 — Not-found page\n\n## Intent\nEdited by a human.\n');
    await page.locator('#spec-save').click();
    await expect(page.locator('#toast')).toContainText(/saved/i);
    expect(readFileSync(path.join(simDir, 'jobs/J-005/spec.md'), 'utf8')).toContain('Edited by a human.');
  });

  test('an illegal drop snaps back and shows the refusal', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-005').dragTo(column(page, 'done'));
    await expect(page.locator('#toast')).toContainText(/refused/i);
    await expect(column(page, 'backlog').locator('[data-card="J-005"]')).toBeVisible();
    expect(job('J-005').stage).toBe('backlog');
  });

  test('dragging a backlog card to Approved approves it', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-005').dragTo(column(page, 'approved'));
    await expect(column(page, 'backlog').locator('[data-card="J-005"]')).toHaveCount(0);
    expect(job('J-005').history.some((h) => h.from === 'backlog' && h.to === 'approved' && h.by === 'human')).toBe(true);
  });

  test('dragging a ready card to Done merges it', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-003').dragTo(column(page, 'done'));
    await expect(column(page, 'done').locator('[data-card="J-003"]')).toBeVisible();
    expect(job('J-003').stage).toBe('done');
  });

  test('rework from a ready card sends it back to Approved with the note', async ({ page }) => {
    const before = job('J-002').round;
    await page.goto('/');
    await card(page, 'J-002').click();
    await page.locator('#rework-note').fill('use the Input component from the design system');
    await page.locator('#rework-btn').click();
    await expect.poll(() => job('J-002').round).toBe(before + 1);
    const last = job('J-002').history.filter((h) => h.from === 'ready').at(-1);
    expect(last).toMatchObject({ to: 'approved', by: 'human' });
    expect(readFileSync(path.join(simDir, `jobs/J-002/rework-${before + 1}.md`), 'utf8')).toContain('Input component');
  });
});
