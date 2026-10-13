import { defineConfig } from '@playwright/test';
import path from 'node:path';

const repo = path.resolve(import.meta.dirname, '../../..');
export const simDir = path.join(repo, '.factory-runs-sim');
export const port = 4318;

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
  webServer: {
    command: `pwsh -NoProfile -File "${path.join(repo, 'factory/scripts/serve.ps1')}" -Port ${port} -StateDir "${simDir}"`,
    url: `http://localhost:${port}/`,
    reuseExistingServer: false,
    timeout: 30_000,
    env: factoryEnv,
  },
});
