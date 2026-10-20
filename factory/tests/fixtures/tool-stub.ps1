# Stands in for `gh` and `az` (FACTORY_GH_CMD / FACTORY_AZ_CMD). Appends one JSON line per call to
# $env:FACTORY_TOOL_LOG: { args, stdin, ghToken, ghTokenValue } - test tokens only. `az ... list` answers an empty JSON array, so the caller creates what it ensures.
param([Parameter(ValueFromRemainingArguments)] [string[]] $Rest)
$stdin = if ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else { '' }
$line = [ordered]@{ args = @($Rest); stdin = $stdin.Trim(); ghToken = [bool]$env:GH_TOKEN; ghTokenValue = "$env:GH_TOKEN" } | ConvertTo-Json -Compress
[IO.File]::AppendAllText($env:FACTORY_TOOL_LOG, $line + "`n")
if ($Rest -contains 'list') { '[]' } else { '{}' }
exit 0
