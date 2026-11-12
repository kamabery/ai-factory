import { defineConfig } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const repo = path.resolve(import.meta.dirname, '../../..');
export const simDir = path.join(repo, '.factory-runs-sim');
export const port = 4318;
// The Anthropic stub for idea sessions, and the GitHub stub the targets live in.
export const stubPort = 4319;
export const githubPort = 4320;
const gitBase = path.join(simDir, 'github');

// The .env serve.ps1 and simulate.ps1 read: a fake GitHub token, and the GitHub stub. Written here because the
// GitHub stub's clone base is an absolute path.
const envFile = path.join(os.tmpdir(), 'factory-playwright.env');
writeFileSync(envFile, [
  'GITHUB_TOKEN=gh-playwright-fake', 'GITHUB_OWNER=sim', `GITHUB_API_URL=http://localhost:${githubPort}`,
  `FACTORY_GIT_BASE=file:///${gitBase.replace(/\\/g, '/')}`,
  // Previews: the HCP token and API (the GitHub stub serves both), but no HCP_ORG, so deploys stay
  // off for the dashboard's own moves; simulate.ps1 deploys J-008 with an env of its own.
  'HCP_TOKEN=sim-hcp', `HCP_API_URL=http://localhost:${githubPort}`,
  // Lessons propose their template fix as an idea session on this repo.
  'FACTORY_TEMPLATES_REPO=sim/sandbox',
].join('\n'));

// serve.ps1 runs against the simulator's state and its GitHub stub repos. Claude is a no-op and the
// gate is green, so no tokens are spent and neither GitHub nor this repo is touched.
export const factoryEnv = {
  FACTORY_HOME: simDir,
  FACTORY_WORK: path.join(simDir, 'work'),
  FACTORY_ENV_FILE: envFile,
  FACTORY_GATE_CMDS: '[{"name":"build","cmd":"exit 0"},{"name":"test","cmd":"exit 0"},{"name":"ui","cmd":"exit 0"}]',
  FACTORY_CLAUDE_CMD: 'exit 0',
  // Idea turns: claude-stub.ps1 stands in for claude and asks the Anthropic stub for each scripted reply (spec 10).
  FACTORY_IDEA_CLAUDE_CMD: path.join(repo, 'factory/tests/fixtures/claude-stub.ps1'),
  FACTORY_IDEA_STUB_URL: `http://localhost:${stubPort}`,
  FACTORY_IDEA_STUB_STATE: path.join(simDir, 'claude-stub'),
};

export default defineConfig({
  testDir: '.',
  timeout: 120_000,
  // A dashboard move runs factory.ps1 synchronously, which starts a pwsh: about 1 s here, but a
  // rework took 5.4 s on GitHub's windows-latest (PR #3's first CI run) and missed the 5 s default.
  expect: { timeout: 20_000 },
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
      command: `pwsh -NoProfile -File "${path.join(repo, 'factory/tests/fixtures/github-stub.ps1')}" -Port ${githubPort} -GitBase "${gitBase}" -Login sim`,
      url: `http://localhost:${githubPort}/`,
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: `pwsh -NoProfile -File "${path.join(repo, 'factory/scripts/serve.ps1')}" -Port ${port} -StateDir "${simDir}" -EnvFile "${envFile}"`,
      url: `http://localhost:${port}/`,
      reuseExistingServer: false,
      timeout: 30_000,
      env: factoryEnv,
    },
  ],
});
