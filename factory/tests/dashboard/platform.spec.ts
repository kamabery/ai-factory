import { test, expect } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { cli, factoryEnv } from './playwright.config';

// Spec 24: the platform chip, end to end through the CLI, against the az fake playing the platform's Postgres server.
// The suite's own dashboard has deploys off, so this test serves a board of its own with HCP_ORG set.
const port = 4322;
const testHost = cli.replace(/Factory\.Cli([\\/]debug[\\/])factory\.dll$/, 'Factory.TestHost$1Factory.TestHost.dll');

test.describe.serial('the platform chip', () => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'factory-platform-'));
  const pg = path.join(dir, 'pg-state.txt');
  let server: ChildProcess;

  test.beforeAll(async () => {
    const env = path.join(dir, 'deploy.env');
    writeFileSync(env, [readFileSync(factoryEnv.FACTORY_ENV_FILE, 'utf8'), 'HCP_ORG=sim-org',
      'FACTORY_PLATFORM_SERVER_ID=/subscriptions/sim/resourceGroups/rg-factory-platform/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-factory-sim'].join('\n'));
    writeFileSync(pg, 'Stopped');
    server = spawn('dotnet', [cli, 'serve', '--port', String(port), '--state-dir', path.join(dir, 'state'), '--env-file', env], {
      env: { ...process.env, ...factoryEnv, FACTORY_HOME: path.join(dir, 'state'), FACTORY_ENV_FILE: env,
        FACTORY_AZ_CMD: JSON.stringify(['dotnet', testHost, 'tool', '--pg-state', pg, '--']) },
      stdio: 'ignore',
    });
    await expect.poll(async () => { try { return (await fetch(`http://localhost:${port}/`)).status; } catch { return 0; } },
      { timeout: 30_000 }).toBe(200);
  });

  test.afterAll(() => {
    server?.kill();
    rmSync(dir, { recursive: true, force: true });
  });

  test('says asleep, Start wakes it, Sleep puts it back to sleep', async ({ page }) => {
    await page.goto(`http://localhost:${port}/`);
    const chip = page.locator('#platform');
    await expect(chip).toHaveText(/platform: asleep/);

    await chip.locator('[data-platform-start]').click();
    await expect(chip).toHaveText(/platform: starting/);
    await expect.poll(() => readFileSync(pg, 'utf8')).toBe('Ready');
    await expect(chip).toHaveText(/platform: awake/, { timeout: 30_000 });

    await chip.locator('[data-platform-sleep]').click();
    await expect(chip).toHaveText(/platform: stopping/);
    await expect.poll(() => readFileSync(pg, 'utf8')).toBe('Stopped');
    await expect(chip).toHaveText(/platform: asleep/, { timeout: 30_000 });
  });
});
