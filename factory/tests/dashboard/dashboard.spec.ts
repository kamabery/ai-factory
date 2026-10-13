import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { simDir, factoryEnv } from './playwright.config';

const repo = path.resolve(import.meta.dirname, '../../..');

type Job = {
  id: string; stage: string; round: number; target?: string;
  merged?: { sha: string; repo: string; added: string[]; modified: string[] };
  history: { from: string; to: string; by: string }[];
};
const board = () => JSON.parse(readFileSync(path.join(simDir, 'board.json'), 'utf8'));
const job = (id: string): Job => board().jobs.find((j: Job) => j.id === id);

const column = (page: Page, name: string) => page.locator(`[data-column="${name}"]`);
const card = (page: Page, id: string) => page.locator(`[data-card="${id}"]`);
const modal = (page: Page) => page.locator('#modal');
const tab = (page: Page, name: string) => modal(page).locator(`[data-tab="${name}"]`);
const sandbox = path.join(simDir, 'repo');

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

  test('the modal shows the timeline, artifacts rendered as markdown, and the gate result', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-001').click();
    await expect(modal(page)).toBeVisible();
    await expect(modal(page).locator('[data-gate]')).toContainText('test=0');
    await expect(modal(page)).toContainText(sandbox);

    await tab(page, 'timeline').click();
    expect(await modal(page).locator('[data-timeline] li').count()).toBeGreaterThanOrEqual(10);

    await tab(page, 'artifacts').click();
    await modal(page).locator('[data-file="round-1/build.md"]').click();
    await expect(modal(page).locator('#viewer h1')).toContainText('Build: J-001');
  });

  test('Done shows the full paths the merge added', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-001').click();
    const merged = modal(page).locator('[data-merged]');
    await expect(merged).toContainText(job('J-001').merged!.sha.slice(0, 7));
    await expect(merged).toContainText(path.join(sandbox, 'J-001.txt'));
  });

  test('the Output tab streams the orchestrator and each agent, tagged', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-001').click();
    await tab(page, 'output').click();
    const out = modal(page).locator('[data-output]');
    await expect(out).toContainText('Starting J-001');                 // orchestrator text
    await expect(out).toContainText('▸ Agent');                        // orchestrator tool call
    await expect(out.locator('[data-agent="builder"]').first()).toBeVisible();
    await expect(out).toContainText('Writing the failing test first'); // builder transcript text
    await expect(out.locator('.thinking').first()).toBeVisible();      // thinking marker
    await expect(out).toContainText('$0.42');                          // result line
  });

  test('an idea card opens its spec-writer output', async ({ page }) => {
    await page.goto('/');
    await column(page, 'ideas').locator('[data-idea="I-003"]').click();
    await expect(modal(page)).toBeVisible();
    await expect(modal(page)).toContainText('Make it faster');
    await expect(modal(page).locator('[data-output]')).toContainText('too vague');
  });

  test('the pipeline strip has a node for every agent and the gate', async ({ page }) => {
    await page.goto('/');
    for (const node of ['spec-writer', 'builder', 'security-reviewer', 'code-reviewer', 'ux-reviewer', 'ui-reviewer', 'approver', 'gate']) {
      await expect(page.locator(`[data-node="${node}"]`)).toBeVisible();
    }
  });

  test('an idea needs a target; the hint tells a new repo from an existing one', async ({ page }) => {
    await page.goto('/');
    await page.locator('#idea-text').fill('Show a count of books in the header');
    const target = page.locator('#idea-target');
    await target.fill('');
    await expect(page.locator('#idea-submit')).toBeDisabled();
    await target.fill('relative\\path');
    await expect(page.locator('#target-hint')).toContainText(/absolute/i);
    await expect(page.locator('#idea-submit')).toBeDisabled();
    await target.fill(path.join(simDir, 'brand-new-app'));
    await expect(page.locator('#target-hint')).toContainText(/new repo/i);
    await target.fill(sandbox);
    await expect(page.locator('#target-hint')).toContainText(/existing repo/i);
    await page.locator('#idea-submit').click();
    await expect(column(page, 'ideas')).toContainText('Show a count of books in the header');
    const idea = board().ideas.find((i: { text: string }) => i.text === 'Show a count of books in the header');
    expect(idea.target).toBe(sandbox);
  });

  test('a new target shows scaffold options with defaults; an existing repo hides them', async ({ page }) => {
    await page.goto('/');
    const target = page.locator('#idea-target');
    await target.fill(sandbox);
    await expect(page.locator('#target-hint')).toContainText(/existing repo/i);
    await expect(page.locator('#scaffold')).toBeHidden();
    await target.fill(path.join(simDir, 'apps', 'my-shop'));
    await expect(page.locator('#target-hint')).toContainText(/new repo/i);
    await expect(page.locator('#scaffold')).toBeVisible();
    await expect(page.locator('#sc-name')).toHaveValue('myshop');
    await expect(page.locator('#sc-resource')).toHaveValue('Widget');
    await expect(page.locator('#sc-db')).toHaveValue('sqlite');
    await expect(page.locator('#sc-ui')).toHaveValue('react');
  });

  test('an idea for a new target waits, and its bootstrap job lands in Backlog', async ({ page }) => {
    await page.goto('/');
    await page.locator('#idea-text').fill('A shop with products');
    await page.locator('#idea-target').fill(path.join(simDir, 'apps', 'shop'));
    await expect(page.locator('#scaffold')).toBeVisible();
    await page.locator('#sc-resource').fill('Product');
    await page.locator('#idea-submit').click();
    await expect.poll(() => board().ideas.find((i: { text: string }) => i.text === 'A shop with products')?.status).toBe('waiting');
    const idea = board().ideas.find((i: { text: string }) => i.text === 'A shop with products');
    expect(idea.scaffold).toMatchObject({ name: 'shop', resource: 'Product', db: 'sqlite', ui: 'react', events: false });
    const boot = idea.jobs[0];
    expect(job(boot)).toMatchObject({ stage: 'backlog', kind: 'bootstrap' });
    await expect(column(page, 'backlog').locator(`[data-card="${boot}"] [data-kind="bootstrap"]`)).toBeVisible();
    await expect(column(page, 'ideas').locator(`[data-idea="${idea.id}"]`)).toContainText(`waiting for bootstrap ${boot}`);
  });

  test('merging a ready bootstrap job releases its idea to the spec-writer', async ({ page }) => {
    const idea = board().ideas.find((i: { text: string }) => i.text === 'A todo app, bootstrapped');
    const boot = idea.jobs[0];
    expect(idea.status).toBe('waiting');
    await page.goto('/');
    await card(page, boot).dragTo(column(page, 'done'));
    await expect(column(page, 'done').locator(`[data-card="${boot}"]`)).toBeVisible();
    await expect.poll(() => board().ideas.find((i: { id: string }) => i.id === idea.id).status).not.toBe('waiting');
  });

  test('a backlog card opens a large modal with Edit and a live Preview, and Save writes to disk', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-005').click();
    await expect(modal(page)).toBeVisible();
    expect((await modal(page).boundingBox())!.width).toBeGreaterThanOrEqual(1000);
    const editor = page.locator('#spec-editor');
    await expect(editor).toHaveValue(/# Spec: J-005/);
    const text = `# Spec: J-005 — Not-found page\n\n## Intent\nEdited by a human.\n\n## Target\n- Repo: ${sandbox}\n\n## Gate\n- build: \`exit 0\`\n\n## Acceptance criteria\n- [ ] AC1: shows a page\n`;
    await editor.fill(text);
    const preview = page.locator('#spec-preview');
    await expect(preview.locator('h2', { hasText: 'Acceptance criteria' })).toBeVisible();
    await expect(preview.locator('input[type="checkbox"]')).toHaveCount(1);
    await expect(preview).toContainText(`Repo: ${sandbox}`); // Windows backslashes survive markdown
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
