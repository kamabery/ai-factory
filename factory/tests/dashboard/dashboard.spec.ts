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

  test('the header names the run mode and its provider, and no card carries a provider chip', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('#mode')).toHaveText('local · subscription');
    await expect(card(page, 'J-005')).toBeVisible();
    await expect(page.locator('[data-provider]')).toHaveCount(0);
    await expect(page.locator('#mode')).toHaveAttribute('data-mode', 'local');
  });

  test('cloud mode stands out in the header, because it spends money', async ({ page }) => {
    await page.goto('/');
    const quiet = await page.locator('#mode').evaluate((e) => getComputedStyle(e).color);
    const real = await (await page.request.get('/api/ideate/config')).json();
    await page.route('**/api/ideate/config', (r) => r.fulfill({ body: JSON.stringify({ ...real, mode: 'cloud', provider: 'foundry' }) }));
    await page.reload();
    const mode = page.locator('#mode');
    await expect(mode).toHaveText('cloud · foundry');
    await expect(mode).toHaveAttribute('data-mode', 'cloud');
    await expect(mode).toHaveAttribute('title', /bill.*Microsoft Foundry/);
    const run = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--run').trim());
    const loud = await mode.evaluate((e) => ({ color: getComputedStyle(e).color, bg: getComputedStyle(e).backgroundColor }));
    expect(loud.color).not.toBe(quiet);
    expect(loud.bg).not.toBe('rgba(0, 0, 0, 0)');
    const probe = await page.evaluate((c) => { const d = document.createElement('div'); d.style.color = c; document.body.append(d); const v = getComputedStyle(d).color; d.remove(); return v; }, run);
    expect(loud.color).toBe(probe);

    await page.unroute('**/api/ideate/config');
    await page.route('**/api/ideate/config', (r) => r.fulfill({ body: JSON.stringify({ ...real, mode: null, provider: null, enabled: false }) }));
    await page.reload();
    await expect(mode).toHaveText('FACTORY_MODE is invalid');
    await expect(mode).toHaveAttribute('data-mode', 'invalid');
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
    await expect(page.locator('#backdrop')).not.toHaveClass(/open/);
    await expect(column(page, 'approved').locator('[data-card="J-002"]')).toBeVisible();
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

  test('a job in deploy counts its attempts out of five, and the new blocked flags say what they mean', async ({ page }) => {
    await page.route('**/state/board.json', async (r) => {
      const b = board();
      const set = (id: string, patch: object) => Object.assign(b.jobs.find((j: Job) => j.id === id), patch);
      set('J-008', { stage: 'deploy', flag: null });
      b.jobs.find((j: Job) => j.id === 'J-008').deploy.attempts = 2;
      await r.fulfill({ body: JSON.stringify(b) });
    });
    await page.goto('/');
    await expect(page.locator('[data-card="J-008"]')).toContainText('2/5');
    await page.unroute('**/state/board.json');

    for (const [reason, says] of [['environment', 'cannot be fixed by an agent'], ['deploy-fix-rounds', 'two deploy-fix rounds']]) {
      await page.route('**/state/board.json', async (r) => {
        const b = board();
        Object.assign(b.jobs.find((j: Job) => j.id === 'J-008'), {
          stage: 'blocked', flag: { reason, detail: 'detail line' },
          history: [{ ts: '2026-11-06T17:00:00Z', from: 'deploy', to: 'blocked', by: 'orchestrator' }],
        });
        await r.fulfill({ body: JSON.stringify(b) });
      });
      await page.goto('/');
      await page.locator('[data-card="J-008"]').click();
      await expect(modal(page).locator('[data-flagbox]')).toContainText(says);
      await page.unroute('**/state/board.json');
    }
  });

  test('a canary that passed but could not be torn down says so, and does not say jobs are waiting', async ({ page }) => {
    await page.route('**/state/canary.json', (r) => r.fulfill({ body: JSON.stringify({
      fingerprint: 'sha256:00', status: 'pass', error: null, log: 'canary-6.log', n: 6,
      teardown: { status: 'failed', error: 'destroy run ended errored: 404', startedAt: '2026-11-06T17:00:00Z', failures: 2 },
    }) }));
    await page.goto('/');
    const banner = page.locator('#canary');
    await expect(banner).toBeVisible();
    await expect(banner).toContainText('could not be torn down');
    await expect(banner).toContainText('destroy run ended errored: 404');
    await expect(banner).not.toContainText('Deployable jobs are waiting');
    await expect(banner.locator('a[data-canary-log]')).toHaveAttribute('href', '/state/canary-6.log');
  });

  test('a preview whose destroy failed says so on its card, and offers Tear down even past its timer', async ({ page }) => {
    await page.route('**/state/board.json', async (r) => {
      const b = board();
      Object.assign(b.jobs.find((j: Job) => j.id === 'J-008').deploy, {
        status: 'up', destroyAt: '2020-01-01T00:00:00Z', destroyFailures: 2,
        destroyError: 'destroy run ended errored: Error: deleting Application Redirect URIs',
      });
      await r.fulfill({ body: JSON.stringify(b) });
    });
    await page.goto('/');
    const c = column(page, 'ready').locator('[data-card="J-008"]');
    const chip = c.locator('[data-preview]');
    await expect(chip).toHaveText('preview: destroy failed');
    await expect(chip).toHaveClass(/failed/);
    await expect(chip).toHaveAttribute('title', /deleting Application Redirect URIs/);
    await expect(c.locator('[data-teardown]')).toBeVisible();
    await expect(c.locator('a[data-launch]')).toHaveCount(0);
  });

  test('an Approved card says what it is waiting on', async ({ page, request }) => {
    // The simulator runs with deploys off, and serve.ps1 says so.
    expect((await (await request.get('/api/ideate/config')).json()).deploys).toBe(false);

    // The rest is rendering: a board, a canary and deploys on, served by the test.
    const at = (m: number) => new Date(Date.UTC(2026, 9, 26, 12, m)).toISOString();
    const j = (id: string, stage: string, m: number, extra = {}) => ({
      id, stage, round: 1, title: `job ${id}`, target: 'sim/sandbox', createdAt: at(m), approvedAt: at(m), updatedAt: at(m), history: [], ...extra,
    });
    let jobs = [j('J-201', 'approved', 1), j('J-202', 'approved', 2), j('J-203', 'approved', 3, { kind: 'bootstrap' })];
    let canary: object = { fingerprint: 'sha256:01', status: 'running', error: null, log: 'canary-6.log', startedAt: at(0), n: 6 };
    await page.route('**/api/ideate/config', (r) => r.fulfill({ json: { enabled: false, deploys: true } }));
    await page.route('**/state/board.json', (r) => r.fulfill({ body: JSON.stringify({ jobs, ideas: [] }) }));
    await page.route('**/state/events.jsonl', (r) => r.fulfill({ body: '' }));
    await page.route('**/state/canary.json', (r) => r.fulfill({ body: JSON.stringify(canary) }));
    const wait = (id: string) => card(page, id).locator('[data-wait]');

    await page.goto('/');
    for (const id of ['J-201', 'J-202', 'J-203']) await expect(wait(id)).toHaveText('waiting: deploy canary');

    // Only canary.json changes: the cards follow it. A bootstrap never deploys, so it is not held.
    canary = { ...canary, status: 'failed', error: 'boom', finishedAt: at(5) };
    await expect(wait('J-201')).toHaveText('held: deploy canary failed');
    await expect(wait('J-202')).toHaveText('held: deploy canary failed');
    await expect(wait('J-203')).toHaveText('next');

    canary = { ...canary, status: 'pass', error: null };
    await expect(wait('J-201')).toHaveText('next');
    await expect(wait('J-202')).toHaveText('after J-201');
    await expect(wait('J-203')).toHaveText('after J-202');

    jobs = [j('J-200', 'build', 0), ...jobs];
    for (const id of ['J-201', 'J-202', 'J-203']) await expect(wait(id)).toHaveText('waiting: J-200 is building');
    await expect(card(page, 'J-200').locator('[data-wait]')).toHaveCount(0);
  });

  test('a blocked card waits in Building with its flag and Retry deploy, Rework and Discard, and does not hold the queue', async ({ page }) => {
    // Rendering and wiring only: a board served by the test, and the write calls captured.
    const at = (m: number) => new Date(Date.UTC(2026, 10, 1, 12, m)).toISOString();
    const j = (id: string, stage: string, m: number, extra = {}) => ({
      id, stage, round: 2, title: `job ${id}`, target: 'sim/sandbox', createdAt: at(m), approvedAt: at(m), updatedAt: at(m), history: [], ...extra,
    });
    const jobs = [
      j('J-300', 'blocked', 1, { flag: { reason: 'deploy-failed', detail: 'Error: boom' }, history: [{ ts: at(2), from: 'deploy', to: 'blocked', by: 'orchestrator' }],
        pr: { number: 7, url: 'https://github.com/sim/sandbox/pull/7' } }),
      j('J-301', 'blocked', 2, { flag: { reason: 'max-rounds', detail: 'CHANGES from ui at round 2' }, history: [{ ts: at(3), from: 'review', to: 'blocked', by: 'orchestrator' }] }),
      j('J-302', 'approved', 3),
    ];
    const canary = { fingerprint: 'sha256:01', status: 'pass', error: null, log: 'canary-1.log', startedAt: at(0), finishedAt: at(1), n: 1 };
    await page.route('**/api/ideate/config', (r) => r.fulfill({ json: { enabled: false, deploys: true } }));
    await page.route('**/state/board.json', (r) => r.fulfill({ body: JSON.stringify({ jobs, ideas: [] }) }));
    await page.route('**/state/events.jsonl', (r) => r.fulfill({ body: '' }));
    await page.route('**/state/canary.json', (r) => r.fulfill({ body: JSON.stringify(canary) }));
    const calls: { url: string; body: unknown }[] = [];
    await page.route(/\/api\/jobs\/J-30\d\/(redeploy|move)$/, (r) => {
      calls.push({ url: new URL(r.request().url()).pathname, body: r.request().postDataJSON() });
      return r.fulfill({ json: { ok: true, message: 'ok' } });
    });

    await page.goto('/');
    const b0 = column(page, 'building').locator('[data-card="J-300"]');
    const b1 = column(page, 'building').locator('[data-card="J-301"]');
    await expect(b0).toBeVisible();
    await expect(b0.locator('.sub')).toHaveText(/blocked r2/);
    await expect(b0.locator('[data-flag]')).toHaveText('deploy-failed');
    await expect(b0.locator('a[data-pr]')).toHaveText('PR #7');
    await expect(b1.locator('[data-flag]')).toHaveText('max-rounds');
    // Retry deploy only for a job blocked from deploy: one blocked from review never had a passing round.
    await expect(b0.locator('[data-redeploy]')).toHaveText('Retry deploy');
    await expect(b1.locator('[data-redeploy]')).toHaveCount(0);
    await expect(b1.locator('[data-rework]')).toHaveText('Rework');
    await expect(b1.locator('[data-discard]')).toHaveText('Discard');
    await expect(card(page, 'J-302').locator('[data-wait]')).toHaveText('next');

    await b0.locator('[data-redeploy]').click();
    await expect(modal(page)).toBeHidden();
    await expect.poll(() => calls.map((c) => c.url)).toEqual(['/api/jobs/J-300/redeploy']);

    await b1.locator('[data-discard]').click();
    await expect.poll(() => calls.at(-1)).toEqual({ url: '/api/jobs/J-301/move', body: { to: 'discarded' } });

    // Rework opens the job with its note box; the note goes to rework as from Ready for review.
    await b1.locator('[data-rework]').click();
    await expect(modal(page).locator('[data-flagbox]')).toContainText('max-rounds');
    await modal(page).locator('#rework-note').fill('drop the raw colour');
    await modal(page).locator('#rework-btn').click();
    await expect.poll(() => calls.at(-1)).toEqual({ url: '/api/jobs/J-301/move', body: { to: 'approved', note: 'drop the raw colour' } });
  });

  test('Approve closes the modal at once, spins on the card until it lands, and cannot be sent twice', async ({ page }) => {
    const at = new Date(Date.UTC(2026, 10, 1, 12, 0)).toISOString();
    const j = (id: string) => ({ id, stage: 'backlog', round: 0, title: `job ${id}`, target: 'sim/sandbox', createdAt: at, updatedAt: at, history: [] });
    const jobs = [j('J-330'), j('J-331')];
    await page.route('**/api/ideate/config', (r) => r.fulfill({ json: { enabled: false, deploys: true } }));
    await page.route('**/state/board.json', (r) => r.fulfill({ body: JSON.stringify({ jobs, ideas: [] }) }));
    await page.route('**/state/events.jsonl', (r) => r.fulfill({ body: '' }));
    // factory.ps1 approve is slow: each call waits here until the test answers it.
    const calls: string[] = [];
    const answers: ((ok: boolean) => void)[] = [];
    await page.route(/\/api\/jobs\/J-33\d\/move$/, async (r) => {
      const id = new URL(r.request().url()).pathname.split('/')[3];
      calls.push(id);
      const ok = await new Promise<boolean>((resolve) => answers.push(resolve));
      if (ok) jobs.find((x) => x.id === id)!.stage = 'approved';
      await r.fulfill({ json: ok ? { ok: true, message: 'ok' } : { ok: false, message: 'approve refused: no spec' } });
    });

    await page.goto('/');
    const c = card(page, 'J-330');
    await c.click();
    await page.locator('#approve-btn').dblclick();

    // The modal is gone while the request is still in flight, and the card says it is moving.
    await expect(page.locator('#backdrop')).not.toHaveClass(/open/);
    await expect(c.locator('[data-moving] .spinner')).toBeVisible();
    await expect(c).toHaveAttribute('draggable', 'false');
    await expect(column(page, 'backlog').locator('[data-card="J-330"]')).toBeVisible();

    // Reopened meanwhile, the modal offers no second approve.
    await c.click();
    await expect(page.locator('#approve-btn')).toBeDisabled();
    await expect(page.locator('#approve-btn .spinner')).toBeVisible();
    await expect(page.locator('#discard-btn')).toBeDisabled();
    await modal(page).locator('button.close').click();
    expect(calls).toEqual(['J-330']);

    answers.shift()!(true);
    await expect(column(page, 'approved').locator('[data-card="J-330"]')).toBeVisible();
    await expect(c.locator('[data-moving]')).toHaveCount(0);

    // A refusal stops the spinner, leaves the card in Backlog draggable, and says why.
    const r = card(page, 'J-331');
    await r.click();
    await page.locator('#approve-btn').click();
    await expect(r.locator('[data-moving] .spinner')).toBeVisible();
    await expect.poll(() => answers.length).toBe(1);
    answers.shift()!(false);
    await expect(r.locator('[data-moving]')).toHaveCount(0);
    await expect(column(page, 'backlog').locator('[data-card="J-331"]')).toHaveAttribute('draggable', 'true');
    await expect(page.locator('#toast')).toContainText('approve refused: no spec');
  });

  test('a blocked out-of-scope card says what that means and lists the paths', async ({ page }) => {
    const at = new Date(Date.UTC(2026, 10, 1, 12, 0)).toISOString();
    const jobs = [{
      id: 'J-310', stage: 'blocked', round: 3, title: 'job J-310', target: 'sim/sandbox', createdAt: at, approvedAt: at, updatedAt: at,
      history: [{ ts: at, from: 'build', to: 'blocked', by: 'orchestrator' }],
      flag: { reason: 'out-of-scope', detail: '2 paths outside Scope: In: IaC/adopt.tf, docs/x.md',
        why: "Paths outside the spec's Scope: In:\n- `IaC/adopt.tf`\n- `docs/x.md`" },
    }];
    await page.route('**/api/ideate/config', (r) => r.fulfill({ json: { enabled: false, deploys: true } }));
    await page.route('**/state/board.json', (r) => r.fulfill({ body: JSON.stringify({ jobs, ideas: [] }) }));
    await page.route('**/state/events.jsonl', (r) => r.fulfill({ body: '' }));

    await page.goto('/');
    const c = column(page, 'building').locator('[data-card="J-310"]');
    await expect(c.locator('[data-flag]')).toHaveText('out-of-scope');
    await expect(c.locator('[data-redeploy]')).toHaveCount(0);   // blocked from build: nothing to redeploy
    await c.click();
    const box = modal(page).locator('[data-flagbox]');
    await expect(box).toContainText('Scope +:');
    await expect(box.locator('li')).toHaveText(['IaC/adopt.tf', 'docs/x.md']);
  });

  test('a sibling merged: cards say base moved, and a spec-stale card says what to do', async ({ page }) => {
    const at = new Date(Date.UTC(2026, 10, 2, 12, 0)).toISOString();
    const moved = { by: 'J-320', sha: 'abc1234', at };
    const j = (id: string, stage: string, extra = {}) => ({ id, stage, round: 1, title: `job ${id}`, target: 'sim/sandbox',
      createdAt: at, approvedAt: at, updatedAt: at, history: [], ...extra });
    const jobs = [
      j('J-320', 'done'),
      j('J-321', 'backlog', { baseMoved: moved }),
      j('J-322', 'approved', { baseMoved: moved }),
      j('J-323', 'ready', { baseMoved: moved, history: [{ ts: at, from: 'build', to: 'ready', by: 'orchestrator' }],
        flag: { reason: 'spec-stale', detail: 'the spec does not hold since J-320 merged',
          why: '## Spec against base\nSpec holds: no\nJ-320 renamed the page.\nScope +: src/pages/renamed/**' } }),
      j('J-324', 'backlog'),
    ];
    await page.route('**/api/ideate/config', (r) => r.fulfill({ json: { enabled: false, deploys: false } }));
    await page.route('**/state/board.json', (r) => r.fulfill({ body: JSON.stringify({ jobs, ideas: [] }) }));
    await page.route('**/state/events.jsonl', (r) => r.fulfill({ body: '' }));

    await page.goto('/');
    for (const id of ['J-321', 'J-322', 'J-323']) {
      await expect(page.locator(`[data-card="${id}"] [data-base-moved]`)).toHaveText('base moved: J-320 merged');
    }
    await expect(page.locator('[data-card="J-324"] [data-base-moved]')).toHaveCount(0);
    const c = page.locator('[data-card="J-323"]');
    await expect(c.locator('[data-flag]')).toHaveText('spec-stale');
    await c.click();
    const box = modal(page).locator('[data-flagbox]');
    await expect(box).toContainText('merged after this spec was written');
    await expect(box).toContainText('Scope +: src/pages/renamed/**');
  });

  test('New idea can start from an unmerged job, Abandon asks once more, and stacked cards name their base', async ({ page }) => {
    // Rendering and requests only: serve.ps1's side is in serve.Tests.ps1.
    const j = (id: string, stage: string, extra = {}) => ({ id, stage, round: 1, title: `job ${id}`, target: sandbox, history: [], ...extra });
    const jobs = [
      j('J-301', 'ready', { pr: { number: 9, url: 'https://github.example/pull/9' } }),
      j('J-302', 'backlog'),
      j('J-303', 'done', { pr: { number: 8, url: 'https://github.example/pull/8' } }),
      j('J-304', 'backlog', { base: 'J-301' }),
    ];
    const ideas = [
      { id: 'I-301', text: 'an idea nobody wants', target: sandbox, status: 'drafting', jobs: [] },
      { id: 'I-302', text: 'already abandoned', target: sandbox, status: 'abandoned', jobs: [] },
    ];
    const posts: { url: string; body: Record<string, unknown> }[] = [];
    await page.route('**/api/ideate/config', (r) => r.fulfill({ json: { enabled: true, owner: 'sim', deploys: false } }));
    await page.route('**/state/board.json', (r) => r.fulfill({ body: JSON.stringify({ jobs, ideas }) }));
    await page.route('**/state/events.jsonl', (r) => r.fulfill({ body: '' }));
    await page.route('**/api/sessions', (r) => {
      if (r.request().method() === 'GET') return r.fulfill({ json: [{ id: 'S-301', target: sandbox, text: 'move tag list to the main menu', status: 'idle', idea: null, costUsd: 0.1 }] });
      posts.push({ url: r.request().url(), body: r.request().postDataJSON() });
      return r.fulfill({ status: 409, json: { ok: false, message: 'stopped by the test' } });
    });
    for (const p of ['**/api/sessions/S-301/close', '**/api/ideas/I-301/abandon'])
      await page.route(p, (r) => { posts.push({ url: r.request().url(), body: r.request().postDataJSON() }); return r.fulfill({ json: { ok: true, message: 'ok' } }); });

    await page.goto('/');
    await expect(card(page, 'J-304').locator('[data-base]')).toHaveText('on J-301');
    await expect(card(page, 'J-301').locator('[data-base]')).toHaveCount(0);
    await expect(column(page, 'ideas').locator('[data-idea="I-302"]')).toHaveCount(0);

    // Base: main, plus each job on the target that is unmerged and has a PR.
    await column(page, 'ideas').locator('#new-idea-btn').click();
    await expect(modal(page).locator('#s-base')).toBeHidden();
    await modal(page).locator('#s-target').selectOption(sandbox);
    const base = modal(page).locator('#s-base');
    await expect(base).toBeVisible();
    await expect(base.locator('option')).toHaveCount(2);
    await expect(base.locator('option').nth(0)).toHaveText('main');
    await base.selectOption('J-301');
    await modal(page).locator('#s-text').fill('move tag list to the main menu');
    await modal(page).locator('#s-start').click();
    await expect(page.locator('#toast')).toContainText('stopped by the test');
    expect(posts.at(-1)!.body).toMatchObject({ target: sandbox, base: 'J-301' });
    await page.keyboard.press('Escape');
    await expect(modal(page)).toBeHidden();

    // Abandon: the first click asks, the second abandons; neither opens the card.
    for (const [sel, url, body] of [['[data-session="S-301"]', '/api/sessions/S-301/close', { abandon: true }], ['[data-idea="I-301"]', '/api/ideas/I-301/abandon', {}]] as const) {
      const n = posts.length;
      const btn = column(page, 'ideas').locator(`${sel} [data-abandon]`);
      await btn.click();
      await expect(btn).toHaveText('Abandon?');
      expect(posts.length).toBe(n);
      await btn.click();
      await expect.poll(() => posts.length).toBe(n + 1);
      expect(posts.at(-1)!.url).toContain(url);
      expect(posts.at(-1)!.body).toMatchObject(body);
      await expect(modal(page)).toBeHidden();
    }
  });

  test('an opened idea has its own Abandon, which abandons at once and closes the card', async ({ page }) => {
    const ideas = [
      { id: 'I-401', text: 'a released idea', target: sandbox, status: 'drafting', jobs: ['J-401'] },
      { id: 'I-402', text: 'an idea that is done', target: sandbox, status: 'done', jobs: [] },
    ];
    const posts: string[] = [];
    await page.route('**/state/board.json', (r) => r.fulfill({ body: JSON.stringify({ jobs: [], ideas }) }));
    await page.route('**/api/ideas/I-401/abandon', (r) => { posts.push(r.request().url()); return r.fulfill({ json: { ok: true, message: 'ok' } }); });

    await page.goto('/');
    await column(page, 'ideas').locator('[data-idea="I-401"] .title').click();
    const btn = modal(page).locator('#idea-abandon-btn');
    await expect(btn).toHaveText('Abandon');
    await btn.click();
    await expect.poll(() => posts.length).toBe(1);
    await expect(modal(page)).toBeHidden();
  });

  test('an opened idea session has its own Abandon too, which abandons at once and closes it', async ({ page }) => {
    const session = { id: 'S-401', target: sandbox, text: 'radar items', status: 'idle', idea: null, costUsd: 0.2 };
    const posts: { url: string; body: unknown }[] = [];
    await page.route('**/state/board.json', (r) => r.fulfill({ body: JSON.stringify({ jobs: [], ideas: [] }) }));
    await page.route('**/api/sessions', (r) => r.fulfill({ json: [session] }));
    await page.route('**/state/sessions/S-401/session.json', (r) => r.fulfill({ json: session }));
    await page.route('**/state/sessions/S-401/chat.json', (r) => r.fulfill({ json: { messages: [] } }));
    await page.route('**/state/sessions/S-401/stream.jsonl', (r) => r.fulfill({ body: '' }));
    await page.route('**/api/sessions/S-401/close', (r) => { posts.push({ url: r.request().url(), body: r.request().postDataJSON() }); return r.fulfill({ json: { ok: true, message: 'ok' } }); });

    await page.goto('/');
    await column(page, 'ideas').locator('[data-session="S-401"] .title').click();
    const btn = modal(page).locator('#session-abandon-btn');
    await expect(btn).toHaveText('Abandon');
    await btn.click();
    await expect.poll(() => posts.length).toBe(1);
    expect(posts[0].body).toMatchObject({ abandon: true });
    await expect(modal(page)).toBeHidden();
  });
});
