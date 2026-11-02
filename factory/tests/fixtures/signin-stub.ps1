# Stands in for signin-smoke.mjs (FACTORY_SIGNIN_CMD). Appends one JSON line per call to
# $env:FACTORY_SIGNIN_LOG, when set: { args, stdin } - test passwords only. Prints the steps a real
# smoke prints, and exits $env:FACTORY_SIGNIN_EXIT (default 0); a failure prints an Entra error first.
param([Parameter(ValueFromRemainingArguments)] [string[]] $Rest)
$stdin = if ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else { '' }
if ($env:FACTORY_SIGNIN_LOG) {
    $line = [ordered]@{ args = @($Rest); stdin = $stdin.Trim() } | ConvertTo-Json -Compress
    [IO.File]::AppendAllText($env:FACTORY_SIGNIN_LOG, $line + "`n")
}
$code = if ($env:FACTORY_SIGNIN_EXIT) { [int]$env:FACTORY_SIGNIN_EXIT } else { 0 }
'step: opened the front end'
'step: clicked Sign in'
if ($code -ne 0) {
    'step: identity provider answered with an error'
    'url: https://example.ciamlogin.com/oauth2/v2.0/authorize'
    'error: AADSTS500011: The resource principal named api://todoapp was not found in the tenant.'
    exit $code
}
'step: signed in: Sign out is showing'
'step: /api/me answered 200'
exit 0
