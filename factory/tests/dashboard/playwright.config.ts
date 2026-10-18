import { defineConfig } from '@playwright/test';
import path from 'node:path';
import os from 'node:os';

const repo = path.resolve(import.meta.dirname, '../../..');
export const simDir = path.join(repo, '.factory-runs-sim');
export const port = 4318;
// The Anthropic stub for idea sessions; factory/tests/fixtures/playwright.env points serve.ps1 at it.
export const stubPort = 4319;

// serve.ps1 runs against the simulator's state and sandbox repo. Claude is a no-op and the gate is
// green, so no tokens are spent and the real repo is never touched.
export const factoryEnv = {
  FACTORY_HOME: simDir,
  FACTORY_REPO: path.join(simDir, 'repo'),
  FACTORY_GATE_CMDS: '[{"name":"build","cmd":"exit 0"},{"name":"test","cmd":"exit 0"},{"name":"ui","cmd":"exit 0"}]',
  FACTORY_CLAUDE_CMD: 'exit 0',
};

export default defineConfig({
  testDir: '.',
  timeout: 120_000,
  workers: 1,
  reporter: 'list',
  use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure' },
  webServer: [
    {
      command: `pwsh -NoProfile -File "${path.join(repo, 'factory/tests/fixtures/anthropic-stub.ps1')}" -Port ${stubPort} -Dir "${os.tmpdir()}" -DelayMs 5`,
      url: `http://localhost:${stubPort}/`,
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: `pwsh -NoProfile -File "${path.join(repo, 'factory/scripts/serve.ps1')}" -Port ${port} -StateDir "${simDir}" -EnvFile "${path.join(repo, 'factory/tests/fixtures/playwright.env')}"`,
      url: `http://localhost:${port}/`,
      reuseExistingServer: false,
      timeout: 30_000,
      env: factoryEnv,
    },
  ],
});
