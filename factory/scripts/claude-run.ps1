<#
.SYNOPSIS
  Dot-sourced by runner.ps1: writes <stateDir>/headless-settings.json and runs one
  headless claude pass with the factory plugin, streaming every stdout line to a log as it arrives.
  See "Runner" in docs/FACTORY-CONTRACT.md. $env:FACTORY_CLAUDE_CMD replaces claude in tests.
#>

$FactoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$FactoryPs1 = Join-Path $PSScriptRoot 'factory.ps1'
# The template-projects workspace bootstrap jobs scaffold from: readable, never editable.
$TemplatesRoot = if ($env:FACTORY_TEMPLATES) { $env:FACTORY_TEMPLATES } else {
    [IO.Path]::GetFullPath((Join-Path (Split-Path $FactoryRoot -Parent) 'template-projects'))
}

# Claude Code permission paths: forward slashes; '//d/...' is absolute on Windows.
function ConvertTo-RulePath([string] $path) {
    $p = [IO.Path]::GetFullPath($path) -replace '\\', '/'
    if ($p -match '^([A-Za-z]):(.*)$') { $p = '/' + $Matches[1].ToLowerInvariant() + $Matches[2] }
    "/$p"
}

# Agents work in the job's throwaway clone, so the whole toolchain (scaffolding included) is allowed,
# plus read-only shell commands. Anything that would leave the branch (push, merge, switching to main)
# is denied, and so is reading .env: only factory.ps1 holds the GitHub token. Each rule is given for both the Bash and the PowerShell tool.
$AllowCommands = @(
    'dotnet *', 'npm *', 'npx *', 'node *', 'aspire *', 'aspire-addon *',
    # Offline Terraform for the devops agent (no backend, so no state and no credentials), and curl for
    # the approver's smoke test of the preview. Never apply, plan or destroy: factory.ps1 deploys.
    'terraform init -backend=false*', 'terraform validate*', 'terraform test*', 'terraform fmt*', 'curl *',
    'git status*', 'git diff*', 'git log*', 'git show*', 'git add *', 'git commit *', 'git branch*', 'git rev-parse*', 'git ls-files*',
    # Both only add a commit on the job branch; a rework may ask for a revert (J-022).
    'git revert *', 'git rm *',
    # The one merge allowed: the job's base into its branch. checkout points factory/base at it (J-022).
    'git merge --no-edit factory/base*', 'git merge --abort*',
    'ls*', 'cat *', 'head *', 'tail *', 'wc *', 'grep *', 'echo *', 'pwd', 'cd *', 'which *',
    'Get-ChildItem*', 'Get-Content *', 'Select-String *', 'Test-Path *', 'Resolve-Path *', 'Get-Location', 'Set-Location *', 'Write-Output *'
)
# The environment holds an openrouter job's key, so reading it is denied too.
$DenyCommands = @('git push*', 'git merge main*', 'git merge origin*', 'git rebase*', 'git reset --hard*', 'git checkout main*', 'git switch main*', 'git branch -D*', 'git worktree*',
    'printenv*', 'env*', 'Get-ChildItem env:*', 'Get-Item env:*', 'Get-Content env:*')

. (Join-Path $PSScriptRoot 'env-file.ps1')

# The child environment for a job's provider (see "Providers" in docs/FACTORY-CONTRACT.md): name ->
# value. For subscription, blanks an inherited key or token, which would beat the CLI's login;
# $null when openrouter has no key in .env.
$ModelVars = 'ANTHROPIC_DEFAULT_FABLE_MODEL', 'ANTHROPIC_DEFAULT_OPUS_MODEL', 'ANTHROPIC_DEFAULT_SONNET_MODEL', 'ANTHROPIC_DEFAULT_HAIKU_MODEL', 'CLAUDE_CODE_SUBAGENT_MODEL'
function Get-ProviderEnv([string] $provider) {
    if ($provider -ne 'openrouter') { return @{ ANTHROPIC_API_KEY = ''; ANTHROPIC_AUTH_TOKEN = '' } }
    $vars = Read-EnvFile $(if ($env:FACTORY_ENV_FILE) { $env:FACTORY_ENV_FILE } else { Join-Path $FactoryRoot '.env' })
    if (-not $vars['OPENROUTER_API_KEY']) { return $null }
    $e = [ordered]@{ ANTHROPIC_BASE_URL = 'https://openrouter.ai/api'; ANTHROPIC_AUTH_TOKEN = $vars['OPENROUTER_API_KEY']; ANTHROPIC_API_KEY = '' }
    foreach ($n in $ModelVars) { if ($vars[$n]) { $e[$n] = $vars[$n] } }
    $e
}

# The factory's own plugin, and the template workspace as the aspire-starter plugin (boot-strap-app, enhance-app).
function Get-PluginDirs { @((Join-Path $FactoryRoot 'factory/plugin'), $TemplatesRoot) }

function Write-HeadlessSettings([string] $stateDir) {
    $factory = $FactoryPs1 -replace '\\', '/'
    $both = { param($cmds) foreach ($c in $cmds) { "Bash($c)"; "PowerShell($c)" } }
    $settings = [ordered]@{
        # A deploy can take an hour, and a shell call is cut off at 10 minutes by default (J-023).
        env = [ordered]@{ BASH_MAX_TIMEOUT_MS = '5400000' }
        permissions = [ordered]@{
            allow = @(& $both $AllowCommands) + @("Bash(pwsh -NoProfile -File $factory *)", "PowerShell(pwsh -NoProfile -File $factory *)")
            additionalDirectories = @(((Join-Path $FactoryRoot 'factory/templates') -replace '\\', '/'), ($TemplatesRoot -replace '\\', '/'))
            deny = @(& $both $DenyCommands) + @(
                'Edit(/factory/**)', 'Edit(/.claude/**)', 'Edit(/docs/FACTORY-CONTRACT.md)',
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot 'factory'))/**)",
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot '.claude'))/**)",
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot 'docs/FACTORY-CONTRACT.md')))",
                "Edit($(ConvertTo-RulePath $stateDir)/jobs/*/spec.md)",
                "Edit($(ConvertTo-RulePath $TemplatesRoot)/**)",
                "Read($(ConvertTo-RulePath (Join-Path $FactoryRoot '.env')))"
            )
        }
    }
    $path = Join-Path $stateDir 'headless-settings.json'
    Set-Content $path ($settings | ConvertTo-Json -Depth 5)
    $path
}

# Runs one pass in the current directory. Returns @{ Code; Result } where Result is the parsed
# final "type":"result" line (or the last JSON line carrying total_cost_usd), or $null.
# $childEnv (from Get-ProviderEnv) is set for claude only and restored afterwards.
function Invoke-ClaudeStream([string] $prompt, [string] $log, [string[]] $addDirs = @(), [Collections.IDictionary] $childEnv = @{}) {
    $stateDir = $env:FACTORY_HOME
    $settings = Write-HeadlessSettings $stateDir
    New-Item -ItemType Directory -Force (Split-Path $log -Parent) | Out-Null
    # One stream for the whole pass, shared for reading and writing, so a reader (the dashboard's
    # Output tab) can never make a write fail and take the job down with it.
    $out = [IO.StreamWriter]::new([IO.FileStream]::new($log, 'Create', 'Write', [IO.FileShare]'ReadWrite, Delete'), [Text.UTF8Encoding]::new($false))
    $out.AutoFlush = $true
    $script:LastResult = $null
    $sink = {
        process {
            $line = "$_"
            $out.Write($line + "`n")
            if ($line -match '"total_cost_usd"') { try { $script:LastResult = $line | ConvertFrom-Json } catch { } }
        }
    }
    # claude writes UTF-8; a runner started by serve.ps1 has a fresh console on the OEM codepage.
    $enc = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $saved = @{}
    foreach ($n in $childEnv.Keys) {
        $saved[$n] = [Environment]::GetEnvironmentVariable($n)
        [Environment]::SetEnvironmentVariable($n, $childEnv[$n])
    }
    try {
        if ($env:FACTORY_CLAUDE_CMD) {
            & pwsh -NoProfile -Command $env:FACTORY_CLAUDE_CMD 2>&1 | & $sink
        } else {
            $dirs = @($stateDir) + $addDirs | ForEach-Object { '--add-dir'; $_ }
            $plugins = Get-PluginDirs | ForEach-Object { '--plugin-dir'; $_ }
            & claude -p $prompt @plugins --settings $settings @dirs `
                --output-format stream-json --verbose --permission-mode acceptEdits 2>&1 | & $sink
        }
        $code = $LASTEXITCODE
    } finally {
        $out.Dispose()
        [Console]::OutputEncoding = $enc
        # PowerShell passes $null to SetEnvironmentVariable as '', which would leave the variable set.
        foreach ($n in $saved.Keys) {
            if ($null -eq $saved[$n]) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
        }
    }
    @{ Code = $code; Result = $script:LastResult }
}
