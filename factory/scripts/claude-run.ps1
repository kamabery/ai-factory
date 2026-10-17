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

# Agents work in a throwaway worktree, so the whole toolchain (scaffolding included) is allowed, plus
# read-only shell commands. Anything that would leave the branch (push, merge, switching to main) is
# denied. Each rule is given for both the Bash and the PowerShell tool.
$AllowCommands = @(
    'dotnet *', 'npm *', 'npx *', 'node *', 'aspire *',
    'git status*', 'git diff*', 'git log*', 'git show*', 'git add *', 'git commit *', 'git branch*', 'git rev-parse*', 'git ls-files*',
    'ls*', 'cat *', 'head *', 'tail *', 'wc *', 'grep *', 'echo *', 'pwd', 'cd *', 'which *', 'printenv*',
    'Get-ChildItem*', 'Get-Content *', 'Select-String *', 'Test-Path *', 'Resolve-Path *', 'Get-Location', 'Set-Location *', 'Write-Output *'
)
$DenyCommands = @('git push*', 'git merge*', 'git rebase*', 'git reset --hard*', 'git checkout main*', 'git switch main*', 'git branch -D*', 'git worktree*')

function Write-HeadlessSettings([string] $stateDir) {
    $factory = $FactoryPs1 -replace '\\', '/'
    $both = { param($cmds) foreach ($c in $cmds) { "Bash($c)"; "PowerShell($c)" } }
    $settings = [ordered]@{
        permissions = [ordered]@{
            allow = @(& $both $AllowCommands) + @("Bash(pwsh -NoProfile -File $factory *)", "PowerShell(pwsh -NoProfile -File $factory *)")
            additionalDirectories = @(((Join-Path $FactoryRoot 'factory/templates') -replace '\\', '/'), ($TemplatesRoot -replace '\\', '/'))
            deny = @(& $both $DenyCommands) + @(
                'Edit(/factory/**)', 'Edit(/.claude/**)', 'Edit(/docs/FACTORY-CONTRACT.md)',
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot 'factory'))/**)",
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot '.claude'))/**)",
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot 'docs/FACTORY-CONTRACT.md')))",
                "Edit($(ConvertTo-RulePath $stateDir)/jobs/*/spec.md)",
                "Edit($(ConvertTo-RulePath $TemplatesRoot)/**)"
            )
        }
    }
    $path = Join-Path $stateDir 'headless-settings.json'
    Set-Content $path ($settings | ConvertTo-Json -Depth 5)
    $path
}

# Runs one pass in the current directory. Returns @{ Code; Result } where Result is the parsed
# final "type":"result" line (or the last JSON line carrying total_cost_usd), or $null.
function Invoke-ClaudeStream([string] $prompt, [string] $log, [string[]] $addDirs = @()) {
    $stateDir = $env:FACTORY_HOME
    $settings = Write-HeadlessSettings $stateDir
    New-Item -ItemType Directory -Force (Split-Path $log -Parent) | Out-Null
    [IO.File]::WriteAllText($log, '')
    $script:LastResult = $null
    $sink = {
        process {
            $line = "$_"
            [IO.File]::AppendAllText($log, $line + "`n")
            if ($line -match '"total_cost_usd"') { try { $script:LastResult = $line | ConvertFrom-Json } catch { } }
        }
    }
    # claude writes UTF-8; a runner started by serve.ps1 has a fresh console on the OEM codepage.
    $enc = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        if ($env:FACTORY_CLAUDE_CMD) {
            & pwsh -NoProfile -Command $env:FACTORY_CLAUDE_CMD 2>&1 | & $sink
        } else {
            $dirs = @($stateDir) + $addDirs | ForEach-Object { '--add-dir'; $_ }
            & claude -p $prompt --plugin-dir (Join-Path $FactoryRoot 'factory/plugin') --settings $settings @dirs `
                --output-format stream-json --verbose --permission-mode acceptEdits 2>&1 | & $sink
        }
        $code = $LASTEXITCODE
    } finally { [Console]::OutputEncoding = $enc }
    @{ Code = $code; Result = $script:LastResult }
}
