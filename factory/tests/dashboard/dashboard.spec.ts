import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { simDir, factoryEnv, stubPort } from './playwright.config';

const repo = path.resolve(import.meta.dirname, '../../..');

type Job = {
  id: string; stage: string; round: number; target?: string; approvedAt?: string;
  pr?: { number: number; url: string };
  merged?: { sha: string; repo: string; pr: string; added: string[]; modified: string[] };
  history: { from: string; to: string; by: string }[];
  deploy?: { status: string; urls: { frontend: string | null; api: string } | null };
};
const board = () => JSON.parse(readFileSync(path.join(simDir, 'board.json'), 'utf8'));
const job = (id: string): Job => board().jobs.find((j: Job) => j.id === id);

const column = (page: Page, name: string) => page.locator(`[data-column="${name}"]`);
const card = (page: Page, id: string) => page.locator(`[data-card="${id}"]`);
const modal = (page: Page) => page.locator('#modal');
const tab = (page: Page, name: string) => modal(page).locator(`[data-tab="${name}"]`);
const sandbox = 'sim/sandbox';   // the simulator's GitHub target, served by the GitHub stub

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
    // A published job awaiting the human shows its PR on the card; following it does not open the
    // modal, which offers the PR beside Merge.
    await expect(j3.locator('a[data-pr]')).toHaveAttribute('href', job('J-003').pr!.url);
    await expect(j3.locator('a[data-pr]')).toHaveText(`PR #${job('J-003').pr!.number}`);
    await j3.locator('a[data-pr]').evaluate((a) => a.addEventListener('click', (e) => e.preventDefault()));
    await j3.locator('a[data-pr]').click();
    await expect(modal(page)).toBeHidden();
    await expect(j2.locator('a[data-pr]')).toHaveCount(0);   // never published
    await j3.click();
    await expect(modal(page).locator('#pr-btn')).toHaveAttribute('href', job('J-003').pr!.url);
    await expect(modal(page).locator('#pr-btn')).toHaveText(`Open PR #${job('J-003').pr!.number}`);

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
    await expect(modal(page).locator('[data-pr]')).toHaveAttribute('href', job('J-001').pr!.url);

    await tab(page, 'timeline').click();
    expect(await modal(page).locator('[data-timeline] li').count()).toBeGreaterThanOrEqual(10);

    await tab(page, 'artifacts').click();
    await modal(page).locator('[data-file="round-1/build.md"]').click();
    await expect(modal(page).locator('#viewer h1')).toContainText('Build: J-001');
  });

  test('Done shows the PR and the repo paths the merge added, and the work dir is gone', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-001').click();
    const merged = modal(page).locator('[data-merged]');
    await expect(merged).toContainText(job('J-001').merged!.sha.slice(0, 7));
    await expect(merged).toContainText('J-001.txt');
    await expect(merged.locator('a')).toHaveAttribute('href', job('J-001').merged!.pr);
    expect(existsSync(path.join(simDir, 'work/jobs/J-001'))).toBe(false);
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

  test('an idea card opens its brief', async ({ page }) => {
    await page.goto('/');
    await column(page, 'ideas').locator('[data-idea="I-003"]').click();
    await expect(modal(page)).toBeVisible();
    await expect(modal(page)).toContainText('Make it faster');
    await expect(modal(page).locator('[data-brief]')).toContainText('which list, and what counts as fast');
  });

  test('New idea is offered when the .env has a key, and the CLI hint stays', async ({ page }) => {
    await page.goto('/');
    await expect(column(page, 'ideas').locator('#new-idea-btn')).toBeVisible();
    await expect(page.locator('#idea-hint')).toContainText('idea.ps1');
  });

  test('the pipeline strip has a node for every agent and the gate', async ({ page }) => {
    await page.goto('/');
    for (const node of ['spec-writer', 'builder', 'security-reviewer', 'code-reviewer', 'ux-reviewer', 'ui-reviewer', 'devops', 'approver', 'gate']) {
      await expect(page.locator(`[data-node="${node}"]`)).toBeVisible();
    }
  });

  test('an escalated card says why it was escalated, and its modal shows the approver\'s reason', async ({ page }) => {
    const id = board().jobs.find((j: { title: string }) => j.title === 'Keycloak sign-in').id;
    await page.goto('/');
    const c = column(page, 'ready').locator(`[data-card="${id}"]`);
    await expect(c.locator('[data-flag]')).toHaveText('escalated');
    await expect(c.locator('[data-flag]')).toHaveAttribute('title', /AC8 \(manual\) has no evidence/);
    await c.click();
    const box = modal(page).locator('[data-flagbox]');
    await expect(box).toContainText('needs your decision');            // what the reason code means
    await expect(box).toContainText('is that acceptable before merge?');
    await expect(box.locator('ol li')).toHaveCount(2);                   // the options, as markdown
    await expect(box.locator('strong')).toHaveText('Verify first');
  });

  test('an agent left running by an escalated job is not shown as active', async ({ page }) => {
    // The runner kills claude on escalation, so the builder's agent-stop is never logged.
    execFileSync('pwsh', ['-NoProfile', '-File', path.join(repo, 'factory/scripts/factory.ps1'), 'event', '-Job', 'J-002', '-Kind', 'agent-start', '-Agent', 'builder'], {
      env: { ...process.env, ...factoryEnv },
    });
    await page.goto('/');
    await expect(page.locator('[data-node="builder"]')).not.toHaveClass(/active/);
    await expect(page.locator('[data-node="builder"]')).not.toContainText('J-002');
  });

  test('an agent left running by a job that was purged from the board is not shown as active', async ({ page }) => {
    // J-913 never existed on this board: like a discarded job after Purge, only its events remain.
    execFileSync('pwsh', ['-NoProfile', '-File', path.join(repo, 'factory/scripts/factory.ps1'), 'event', '-Job', 'J-913', '-Kind', 'agent-start', '-Agent', 'builder'], {
      env: { ...process.env, ...factoryEnv },
    });
    await page.goto('/');
    await expect(page.locator('[data-node="builder"]')).not.toContainText('J-913');
  });

  test('merging a ready bootstrap job releases its idea for a /factory:idea session', async ({ page }) => {
    const idea = board().ideas.find((i: { text: string }) => i.text === 'A todo app, bootstrapped');
    const boot = idea.jobs[0];
    expect(idea.status).toBe('waiting');
    await page.goto('/');
    await card(page, boot).dragTo(column(page, 'done'));
    await expect(column(page, 'done').locator(`[data-card="${boot}"]`)).toBeVisible();
    await expect.poll(() => board().ideas.find((i: { id: string }) => i.id === idea.id).status).toBe('drafting');
    await expect(column(page, 'ideas').locator(`[data-idea="${idea.id}"]`)).toContainText(`/factory:idea ${idea.id}`);
    // Nothing headless picks it up: it stays drafting until a human opens the session.
    await page.waitForTimeout(3000);
    expect(board().ideas.find((i: { id: string }) => i.id === idea.id).status).toBe('drafting');
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
    await expect(preview).toContainText(`Repo: ${sandbox}`);
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

  test('a backlog card\'s provider chip switches it between subscription and openrouter without opening the modal', async ({ page }) => {
    await page.goto('/');
    const chip = card(page, 'J-005').locator('[data-provider]');
    await expect(chip).toHaveText('subscription');
    await chip.click();
    await expect(chip).toHaveText('openrouter');
    await expect(modal(page)).toBeHidden();
    expect(board().jobs.find((j: { id: string }) => j.id === 'J-005').provider).toBe('openrouter');
    await chip.click();
    await expect(chip).toHaveText('subscription');
    expect(board().jobs.find((j: { id: string }) => j.id === 'J-005').provider).toBe('subscription');
    await expect(card(page, 'J-001').locator('[data-provider]')).toHaveCount(0);   // done, on the default
  });

  test('dragging a backlog card to Approved approves it', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-005').dragTo(column(page, 'approved'));
    await expect(column(page, 'backlog').locator('[data-card="J-005"]')).toHaveCount(0);
    expect(job('J-005').history.some((h) => h.from === 'backlog' && h.to === 'approved' && h.by === 'human')).toBe(true);
  });

  // Dragging to Done is covered by the bootstrap test above.
  test('Merge (Done) in the modal merges, closes the modal, and the card lands in Done', async ({ page }) => {
    await page.goto('/');
    await card(page, 'J-003').click();
    await page.locator('#merge-btn').click();
    await expect(modal(page)).toBeHidden();
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

  test('a dashboard idea session grills, confirms the brief, and puts the specs on the board as decided', async ({ page }) => {
    const dir = mkdtempSync(path.join(os.tmpdir(), 'ideate-'));
    const specMd = (t: string) => `# Spec: ${t}\n\n## Target\n- Repo: ${sandbox}\n\n## Gate\n- build: \`exit 0\`\n\n## Acceptance criteria\n- [ ] AC1: ${t}`;
    const replies = [
      { content: [{ type: 'text', text: 'Hello! First, who is this for?' }, { type: 'tool_use', id: 'q1', name: 'ask_questions', input: { questions: [
        { question: 'Who uses it?', header: 'Users', multiSelect: false, options: [{ label: 'Just me', description: 'one person' }, { label: 'A team', description: 'several' }] }] } }], stop_reason: 'tool_use' },
      { content: [{ type: 'tool_use', id: 'b1', name: 'submit_brief', input: { markdown: `# Brief: Tag books\n\n## Target\n- Repo: ${sandbox}\n\n## Done means\n- a tag shows on a book — PRINTS: the tag test` } }], stop_reason: 'tool_use' },
      { content: [{ type: 'tool_use', id: 'r1', name: 'read_file', input: { path: 'README.md' } }], stop_reason: 'tool_use' },
      { content: [{ type: 'tool_use', id: 's1', name: 'submit_specs', input: { specs: [{ k: 1, markdown: specMd('Tag a book') }, { k: 2, markdown: specMd('Filter by tag') }] } }], stop_reason: 'tool_use' },
      { content: [{ type: 'text', text: 'Two specs are ready for you.' }], stop_reason: 'end_turn' },
    ];
    replies.forEach((r, i) => writeFileSync(path.join(dir, `${String(i + 1).padStart(3, '0')}.json`), JSON.stringify(r)));
    await fetch(`http://localhost:${stubPort}/__reset`, { method: 'POST', body: JSON.stringify({ dir }) });

    await page.goto('/');
    await column(page, 'ideas').locator('#new-idea-btn').click();
    // The picker lists the GitHub repos (list-repos), after Add new.
    await expect(modal(page).locator(`#s-target option[value="${sandbox}"]`)).toHaveCount(1);
    await expect(modal(page).locator('#s-target option').first()).toHaveAttribute('value', '+new');
    await modal(page).locator('#s-target').selectOption(sandbox);
    await modal(page).locator('#s-text').fill('Let me tag books');
    await modal(page).locator('#s-start').click();

    // Creating the session checks and clones the GitHub target before the first turn starts.
    await expect(modal(page).locator('[data-log]')).toContainText('who is this for?', { timeout: 20_000 });
    const q = modal(page).locator('[data-question="0"]');
    await expect(q).toContainText('Who uses it?');
    await expect(column(page, 'ideas').locator('[data-session]')).toHaveCount(1);
    await q.locator('[data-option="Just me"]').click();
    await modal(page).locator('#s-send-answers').click();

    await expect(modal(page).locator('[data-brief]')).toContainText('Tag books');
    const sent = JSON.parse(readFileSync(path.join(dir, 'requests/002.json'), 'utf8'));
    expect(sent.messages.at(-1).content.at(-1)).toMatchObject({ type: 'tool_result', tool_use_id: 'q1' });
    expect(sent.messages.at(-1).content.at(-1).content).toContain('Just me');
    await modal(page).locator('#s-confirm-brief').click();

    await expect(modal(page).locator('[data-spec="1"]')).toContainText('Tag a book');
    await expect(modal(page).locator('[data-spec="2"]')).toContainText('Filter by tag');
    await expect(modal(page).locator('[data-log]')).toContainText('read_file');
    await expect(modal(page).locator('[data-cost]')).toContainText('$');
    await modal(page).locator('[data-spec="1"] [data-decide="approved"]').click();
    await expect(modal(page).locator('[data-spec="1"]')).toContainText('approved');
    await modal(page).locator('[data-spec="2"] [data-decide="backlog"]').click();

    await expect(modal(page)).toBeHidden();
    const idea = board().ideas.find((i: { text: string }) => i.text === 'Tag books');
    expect(idea.status).toBe('done');
    expect(idea.jobs).toHaveLength(2);
    expect(job(idea.jobs[0]).approvedAt).toBeTruthy();
    expect(job(idea.jobs[1]).stage).toBe('backlog');
    await expect(column(page, 'ideas').locator('[data-session]')).toHaveCount(0);
  });

  test('Add new starts a session with no repo; the scaffold form names it and sets the target', async ({ page }) => {
    const dir = mkdtempSync(path.join(os.tmpdir(), 'ideate-'));
    const replies = [
      { content: [{ type: 'tool_use', id: 'q1', name: 'ask_questions', input: { questions: [
        { question: 'Who uses it?', header: 'Users', multiSelect: false, options: [{ label: 'Just me', description: 'one person' }, { label: 'A team', description: 'several' }] }] } }], stop_reason: 'tool_use' },
      { content: [{ type: 'tool_use', id: 'p1', name: 'propose_scaffold', input: { repo: 'recipes', name: 'recipes', resource: 'Recipe', db: 'sqlite', ui: 'react', events: false, auth: true } }], stop_reason: 'tool_use' },
      { content: [{ type: 'tool_use', id: 'b1', name: 'submit_brief', input: { markdown: '# Brief: Recipes\n\n## Target\n- Repo: sim/recipes-box' } }], stop_reason: 'tool_use' },
    ];
    replies.forEach((r, i) => writeFileSync(path.join(dir, `${String(i + 1).padStart(3, '0')}.json`), JSON.stringify(r)));
    await fetch(`http://localhost:${stubPort}/__reset`, { method: 'POST', body: JSON.stringify({ dir }) });

    await page.goto('/');
    await column(page, 'ideas').locator('#new-idea-btn').click();
    await expect(modal(page).locator(`#s-target option[value="${sandbox}"]`)).toHaveCount(1);
    await modal(page).locator('#s-target').selectOption('+new');
    await modal(page).locator('#s-text').fill('A recipe box');
    await modal(page).locator('#s-start').click();

    await expect(modal(page).locator('.sub-meta')).toContainText('target pending', { timeout: 20_000 });
    await modal(page).locator('[data-question="0"] [data-option="Just me"]').click();
    await modal(page).locator('#s-send-answers').click();

    await expect(modal(page).locator('#sc-repo')).toHaveValue('recipes');
    await expect(modal(page).locator('#sc-auth')).toBeChecked();
    await expect(modal(page).locator('[data-scaffold]')).toContainText('sim/');
    await modal(page).locator('#sc-repo').fill('recipes-box');
    await modal(page).locator('#s-confirm-scaffold').click();

    await expect(modal(page).locator('[data-brief]')).toContainText('Recipes');
    await expect(modal(page).locator('.sub-meta')).toContainText('sim/recipes-box');
    const sent = JSON.parse(readFileSync(path.join(dir, 'requests/003.json'), 'utf8'));
    expect(sent.messages.at(-1).content.at(-1).content).toContain('sim/recipes-box');

    await modal(page).locator('#s-close').click();
    await expect(modal(page)).toBeHidden();
  });

  test('Clear in Done purges evaluated finished cards, their folders and finished ideas, and keeps unevaluated ones', async ({ page }) => {
    await page.goto('/');
    await expect(column(page, 'done').locator('[data-card="J-001"]')).toBeVisible();
    await column(page, 'done').locator('#purge-btn').click();
    await expect(page.locator('#toast')).toContainText('kept until evaluated');
    await expect(column(page, 'done').locator('[data-card="J-001"]')).toHaveCount(0);
    await expect(column(page, 'ideas').locator('[data-idea="I-003"]')).toHaveCount(0);
    expect(existsSync(path.join(simDir, 'jobs/J-001'))).toBe(false);
    // J-003 (merged) and J-004 (discarded) have no evaluation, so they and their folders stay.
    await expect(column(page, 'done').locator('[data-card="J-003"]')).toBeVisible();
    expect(job('J-004').stage).toBe('discarded');
    expect(existsSync(path.join(simDir, 'jobs/J-003'))).toBe(true);
    expect(job('J-005')).toBeTruthy();
  });

  test('the Evaluations tab lists each evaluation, newest first, and opens its markdown; purged jobs keep theirs', async ({ page }) => {
    await page.goto('/');
    await page.locator('[data-view="evals"]').click();
    await expect(page.locator('#board')).toBeHidden();
    const rated = page.locator('[data-eval]:not([data-pending])');
    await expect(rated).toHaveCount(2);
    await expect(rated.nth(0)).toHaveAttribute('data-eval', 'J-002');
    const j1 = page.locator('[data-eval="J-001"]');
    await expect(j1.locator('[data-rating]')).toHaveText('good');
    await expect(j1).toContainText('Book search filter');
    await expect(j1).toContainText('$0.42');
    await expect(page.locator('[data-eval="J-002"] [data-rating]')).toHaveText('mixed');
    await expect(page.locator('[data-eval="J-002"]')).toContainText('max-rounds');

    await j1.click();
    await expect(page.locator('[data-eval-body] h1')).toContainText('Eval: J-001');

    // Gate, cost and time come first, as a grid, above the markdown.
    const facts = page.locator('[data-eval-facts]');
    await expect(page.locator('[data-eval-body] > :first-child')).toHaveAttribute('data-eval-facts', '');
    await expect(facts.locator('h2')).toHaveText('Gate, cost and time');
    await expect(facts.locator('[data-fact="gate"]')).toContainText('green');
    await expect(facts.locator('[data-fact="cost"]')).toContainText('$0.42');
    await expect(facts.locator('[data-fact="time"]')).toContainText('1m 35s');
    await expect(facts.locator('[data-fact="rounds"]')).toContainText('1');
    await expect(facts.locator('[data-model="claude-opus-5-5"]')).toContainText('$0.38');
    await expect(facts.locator('[data-agent="builder"]')).toBeVisible();
    await expect(facts.locator('[data-agent="security-reviewer"]')).toBeVisible();

    await page.locator('[data-view="board"]').click();
    await expect(page.locator('#board')).toBeVisible();
    await expect(page.locator('#evals')).toBeHidden();
  });

  test('the Evaluations tab also lists every finished job not yet evaluated, and how to evaluate it', async ({ page }) => {
    await page.goto('/');
    await page.locator('[data-view="evals"]').click();
    const ids = await page.locator('[data-eval]').evaluateAll((els) => els.map((e) => (e as HTMLElement).dataset.eval));
    expect(ids).toEqual([...ids].sort((a, b) => Number(b!.slice(2)) - Number(a!.slice(2))));
    for (const id of ['J-003', 'J-004']) {
      await expect(page.locator(`[data-eval="${id}"][data-pending] [data-rating]`)).toHaveText('not evaluated');
    }
    await expect(page.locator('[data-eval="J-005"]')).toHaveCount(0);   // still in the backlog
    await page.locator('[data-eval="J-004"]').click();
    await expect(page.locator('[data-eval-body]')).toContainText('factory.ps1 eval J-004 -File');
    await expect(page.locator('[data-feedback]')).toHaveCount(0);
  });

  test('the human leaves feedback on an evaluation; it survives the poll and a reload', async ({ page }) => {
    await page.goto('/');
    await page.locator('[data-view="evals"]').click();
    const j2 = page.locator('[data-eval="J-002"]');
    await expect(j2.locator('[data-human-rating]')).toHaveCount(0);
    await j2.click();

    const form = page.locator('[data-feedback]');
    await expect(form.locator('h2')).toHaveText('Human feedback');
    await form.locator('[data-human="poor"]').click();
    await form.locator('textarea').fill('The ui never settled; I would have split this spec.');
    await page.waitForTimeout(3500); // a poll must not wipe the draft
    await expect(form.locator('textarea')).toHaveValue(/split this spec/);
    await form.locator('[data-save-feedback]').click();
    await expect(page.locator('#toast')).toContainText('feedback saved');
    await expect(j2.locator('[data-human-rating]')).toHaveText('you: poor');
    expect(readFileSync(path.join(simDir, 'evals/J-002.feedback.md'), 'utf8')).toMatch(/^Rating: poor\r?\n\r?\nThe ui never settled/);

    await page.reload();
    await page.locator('[data-view="evals"]').click();
    await page.locator('[data-eval="J-002"]').click();
    await expect(page.locator('[data-feedback] [data-human="poor"]')).toHaveClass(/on/);
    await expect(page.locator('[data-feedback] textarea')).toHaveValue('The ui never settled; I would have split this spec.');
  });

  test('a job with a live preview offers Launch and QA login on its card, without opening the modal', async ({ page }) => {
    await page.goto('/');
    const c = column(page, 'ready').locator('[data-card="J-008"]');
    await expect(c).toBeVisible();
    await expect(c.locator('[data-preview]')).toHaveText(/up/);
    const urls = board().jobs.find((j: Job) => j.id === 'J-008').deploy.urls;
    await expect(c.locator('a[data-launch]')).toHaveAttribute('href', urls.frontend);
    await expect(c.locator('a[data-launch]')).toHaveAttribute('target', '_blank');

    await c.locator('[data-qa]').click();
    await expect(page.locator('#toast')).toContainText('qa-j008@sandbox.test');
    await expect(page.locator('#toast')).toContainText('pw-j008');
    await expect(modal(page)).toBeHidden();

    // The ticket links the preview too, first in its actions: before Open PR when there is one.
    await c.click();
    const open = modal(page).locator('.actions').first();
    await expect(open.locator('#preview-btn')).toHaveAttribute('href', urls.frontend);
    await expect(open.locator('#preview-btn')).toHaveAttribute('target', '_blank');
    await expect(open.locator('#preview-btn')).toHaveText('Open preview');
    await expect(open.locator(':scope > :first-child')).toHaveId('preview-btn');
  });

  test('Tear down destroys the preview: the card shows destroying, then down, and Launch goes away', async ({ page }) => {
    await page.goto('/');
    const c = column(page, 'ready').locator('[data-card="J-008"]');
    await c.locator('[data-teardown]').click();
    await expect(modal(page)).toBeHidden();
    await expect(c.locator('[data-preview]')).toHaveText(/down/, { timeout: 30_000 });
    await expect(c.locator('a[data-launch]')).toHaveCount(0);
    await expect(c.locator('[data-teardown]')).toHaveCount(0);
    expect(board().jobs.find((j: Job) => j.id === 'J-008').deploy.status).toBe('down');
  });

  test('the Lessons tab lists what jobs that went bad taught, newest first, and opens the markdown', async ({ page }) => {
    await page.goto('/');
    await page.locator('#views [data-view="lessons"]').click();
    const rows = page.locator('#lessons [data-lesson]');
    // The simulator's J-002, J-007 and J-008, plus whatever the runner compiled for jobs the earlier
    // tests sent through it (its no-op claude leaves them escalated).
    await expect(page.locator('#lessons [data-lesson="J-008"]')).toBeVisible();
    const ids = await rows.evaluateAll((els) => els.map((e) => Number((e as HTMLElement).dataset.lesson!.replace(/\D/g, ''))));
    expect(ids).toEqual([...ids].sort((a, b) => b - a));
    expect(ids).toEqual(expect.arrayContaining([8, 7, 2]));
    await expect(page.locator('#lessons [data-lesson="J-008"] [data-gaps]')).toHaveText(/1 template gap/);
    await expect(page.locator('#lessons [data-lesson="J-007"] .flag')).toHaveText(/escalated/);

    await page.locator('#lessons [data-lesson="J-008"] .title').click();
    await expect(page.locator('[data-lesson-body]')).toContainText('empty-state');
  });

  test('Propose template fix starts an idea session on the templates repo with the selected lessons, and marks them proposed', async ({ page }) => {
    await page.goto('/');
    await page.locator('#views [data-view="lessons"]').click();
    await page.locator('#lessons [data-lesson="J-008"] [data-select]').check();
    await page.locator('#lessons [data-lesson="J-002"] [data-select]').check();
    await page.locator('[data-propose]').click();

    await expect(page.locator('#lessons [data-lesson="J-008"] [data-status]')).toHaveText(/proposed/, { timeout: 20_000 });
    await expect(page.locator('#lessons [data-lesson="J-002"] [data-status]')).toHaveText(/proposed/);
    const lesson = JSON.parse(readFileSync(path.join(simDir, 'lessons/J-008.json'), 'utf8'));
    expect(lesson.status).toBe('proposed');
    expect(lesson.session).toMatch(/^S-\d+$/);
    expect(JSON.parse(readFileSync(path.join(simDir, 'lessons/J-002.json'), 'utf8')).session).toBe(lesson.session);
    const session = JSON.parse(readFileSync(path.join(simDir, `sessions/${lesson.session}/session.json`), 'utf8'));
    expect(session.target).toBe('sim/sandbox');
    expect(session.text).toContain('empty-state');
    expect(session.text).toContain('J-002');

    await page.locator('#lessons [data-lesson="J-008"] [data-applied]').click();
    await expect(page.locator('#lessons [data-lesson="J-008"] [data-status]')).toHaveText(/applied/);
  });

  test('a failed canary shows a banner with its error and log, and Run canary starts one', async ({ page }) => {
    const file = path.join(simDir, 'canary.json');
    writeFileSync(path.join(simDir, 'canary-4.log'), '## HCP\nError: exec: "az": executable file not found in $PATH');
    writeFileSync(file, JSON.stringify({
      fingerprint: 'sha256:00', status: 'failed', error: 'Error: exec: "az": executable file not found in $PATH',
      repo: 'sim/factory-canary', sha: null, urls: null, log: 'canary-4.log',
      startedAt: '2026-10-24T17:00:00Z', finishedAt: '2026-10-24T17:05:00Z', n: 4,
    }));
    await page.goto('/');
    const banner = page.locator('#canary');
    await expect(banner).toBeVisible();
    await expect(banner).toContainText('Deployable jobs are waiting');
    await expect(banner).toContainText('executable file not found');
    await expect(banner.locator('a[data-canary-log]')).toHaveAttribute('href', '/state/canary-4.log');

    const answer = page.waitForResponse((r) => r.url().endsWith('/api/canary') && r.request().method() === 'POST');
    await banner.locator('[data-run-canary]').click();
    expect((await (await answer).json()).ok).toBe(true);

    // A pass clears the banner.
    writeFileSync(file, JSON.stringify({ fingerprint: 'sha256:00', status: 'pass', error: null, log: 'canary-5.log', n: 5 }));
    await expect(banner).toBeHidden();
  });
});
