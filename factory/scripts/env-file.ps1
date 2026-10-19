# Reads a .env file into a hashtable of NAME -> value (quotes stripped, comments skipped). Dot-sourced
# by github.ps1 (factory.ps1) and claude-run.ps1 (runner.ps1). Values go into local variables only;
# nothing here touches the process environment.
function Read-EnvFile([string] $path) {
    $vars = @{}
    if (Test-Path $path) {
        foreach ($line in Get-Content $path -Encoding utf8) {
            if ($line -match '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -and $line -notmatch '^\s*#') {
                $vars[$Matches[1]] = $Matches[2] -replace '^"(.*)"$', '$1' -replace "^'(.*)'$", '$1'
            }
        }
    }
    $vars
}
