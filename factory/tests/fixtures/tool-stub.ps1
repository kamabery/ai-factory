# Stands in for `gh` and `az` (FACTORY_GH_CMD / FACTORY_AZ_CMD). Appends one JSON line per call to
# $env:FACTORY_TOOL_LOG: { args, stdin, ghToken, ghTokenValue } - test tokens only. `az ... list` answers an empty JSON array, so the caller creates what it ensures.
param([Parameter(ValueFromRemainingArguments)] [string[]] $Rest)
$stdin = if ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else { '' }
# An `@<path>` argument is az's "read this value from a file": its contents are logged under `files`.
$files = @($Rest | Where-Object { $_ -like '@*' -and (Test-Path $_.Substring(1)) } | ForEach-Object { Get-Content $_.Substring(1) -Raw })
$line = [ordered]@{ args = @($Rest); stdin = $stdin.Trim(); files = $files; ghToken = [bool]$env:GH_TOKEN; ghTokenValue = "$env:GH_TOKEN" } | ConvertTo-Json -Compress
[IO.File]::AppendAllText($env:FACTORY_TOOL_LOG, $line + "`n")
# $env:FACTORY_TOOL_FAIL = '<regex>|<message>': a call whose args match the regex prints the message and exits 1.
if ($env:FACTORY_TOOL_FAIL) {
    $pattern, $message = $env:FACTORY_TOOL_FAIL -split '\|', 2
    if (($Rest -join ' ') -match $pattern) { $message; exit 1 }
}
if ($Rest -contains 'list') { '[]' } else { '{}' }
exit 0
