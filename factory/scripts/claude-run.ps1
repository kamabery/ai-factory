<#
.SYNOPSIS
  Dot-sourced by runner.ps1 and spec-run.ps1: writes <stateDir>/headless-settings.json and runs one
  headless claude pass with the factory plugin, streaming every stdout line to a log as it arrives.
  See "Runner" in docs/FACTORY-CONTRACT.md. $env:FACTORY_CLAUDE_CMD replaces claude in tests.
#>

$FactoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$FactoryPs1 = Join-Path $PSScriptRoot 'factory.ps1'

# Claude Code permission paths: forward slashes; '//d/...' is absolute on Windows.
function ConvertTo-RulePath([string] $path) {
    $p = [IO.Path]::GetFullPath($path) -replace '\\', '/'
    if ($p -match '^([A-Za-z]):(.*)$') { $p = '/' + $Matches[1].ToLowerInvariant() + $Matches[2] }
    "/$p"
}

function Write-HeadlessSettings([string] $stateDir) {
    $factory = $FactoryPs1 -replace '\\', '/'
    $settings = [ordered]@{
        permissions = [ordered]@{
            allow = @(
                'Bash(dotnet build *)', 'Bash(dotnet test *)', 'Bash(UPDATE_CONTRACT=1 dotnet test *)', 'Bash(dotnet ef *)',
                'Bash(dotnet new *)', 'Bash(dotnet sln *)', 'Bash(dotnet add *)', 'Bash(dotnet restore *)',
                'Bash(npm ci)', 'Bash(npm install)', 'Bash(npm install *)', 'Bash(npm run *)', 'Bash(npx vite build)',
                'Bash(git status *)', 'Bash(git diff *)', 'Bash(git log *)', 'Bash(git add *)', 'Bash(git commit *)',
                'Bash(git branch --show-current)',
                "Bash(pwsh -NoProfile -File $factory *)", "PowerShell(pwsh -NoProfile -File $factory *)"
            )
            deny = @(
                'Edit(/factory/**)', 'Edit(/.claude/**)', 'Edit(/docs/FACTORY-CONTRACT.md)',
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot 'factory'))/**)",
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot '.claude'))/**)",
                "Edit($(ConvertTo-RulePath (Join-Path $FactoryRoot 'docs/FACTORY-CONTRACT.md')))",
                "Edit($(ConvertTo-RulePath $stateDir)/jobs/*/spec.md)"
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
    if ($env:FACTORY_CLAUDE_CMD) {
        & pwsh -NoProfile -Command $env:FACTORY_CLAUDE_CMD 2>&1 | & $sink
    } else {
        $dirs = @($stateDir) + $addDirs | ForEach-Object { '--add-dir'; $_ }
        & claude -p $prompt --plugin-dir (Join-Path $FactoryRoot 'factory/plugin') --settings $settings @dirs `
            --output-format stream-json --verbose --permission-mode acceptEdits 2>&1 | & $sink
    }
    @{ Code = $LASTEXITCODE; Result = $script:LastResult }
}
