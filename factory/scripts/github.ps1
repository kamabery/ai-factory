# GitHub access for factory.ps1, which dot-sources this file: the REST API and authenticated git.
# See "Targets" in docs/FACTORY-CONTRACT.md. The token is read from .env into $script:Gh and goes
# out only as a request header; it is never put in the environment, a remote URL or .git/config.
# Helpers return $null or an error string (as Ensure-Repo did), so callers choose Fail or a flag.

$script:Gh = $null
$env:GIT_TERMINAL_PROMPT = '0'   # a refused token must fail, not wait on a prompt nobody sees

function GitHubConfig {
    if ($script:Gh) { return $script:Gh }
    $path = if ($env:FACTORY_ENV_FILE) { $env:FACTORY_ENV_FILE } else { Join-Path (MainRoot) '.env' }
    $vars = @{}
    if (Test-Path $path) {
        foreach ($line in Get-Content $path -Encoding utf8) {
            if ($line -match '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -and $line -notmatch '^\s*#') {
                $vars[$Matches[1]] = $Matches[2] -replace '^"(.*)"$', '$1' -replace "^'(.*)'$", '$1'
            }
        }
    }
    $script:Gh = @{
        token = $vars['GITHUB_TOKEN']
        api = $(if ($vars['GITHUB_API_URL']) { $vars['GITHUB_API_URL'] } else { 'https://api.github.com' }).TrimEnd('/')
        gitBase = $(if ($vars['FACTORY_GIT_BASE']) { $vars['FACTORY_GIT_BASE'] } else { 'https://github.com' }).TrimEnd('/', '\')
    }
    $script:Gh
}

# Returns { Status, Body }; Status 0 means no answer (no token, or the request itself failed).
function Invoke-GitHub([string] $Method, [string] $Path, $Body = $null) {
    $c = GitHubConfig
    if (-not $c.token) { return [pscustomobject]@{ Status = 0; Body = @{ message = 'GITHUB_TOKEN is not set in .env' } } }
    $p = @{
        Method = $Method; Uri = "$($c.api)$Path"; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'
        Headers = @{ Authorization = "Bearer $($c.token)"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    }
    if ($null -ne $Body) { $p.Body = $Body | ConvertTo-Json -Depth 10 -Compress; $p.ContentType = 'application/json' }
    try { $res = Invoke-RestMethod @p } catch { return [pscustomobject]@{ Status = 0; Body = @{ message = "$($_.Exception.Message)" } } }
    [pscustomobject]@{ Status = [int]$status; Body = $res }
}

function GhError($r) {
    $msg = if ($r.Body -is [string]) { $r.Body }
           elseif ($r.Body -is [Collections.IDictionary]) { $r.Body['message'] }
           elseif ($r.Body -and $r.Body.PSObject.Properties['message']) { $r.Body.message } else { '' }
    "GitHub $($r.Status): $msg".Trim()
}

# 'existing' (has main), 'missing' or 'empty' (no main), or an error string prefixed 'error: '.
function Get-TargetState([string] $t) {
    $r = Invoke-GitHub GET "/repos/$t"
    if ($r.Status -eq 404) { return 'missing' }
    if ($r.Status -ne 200) { return "error: $(GhError $r)" }
    $b = Invoke-GitHub GET "/repos/$t/branches/main"
    if ($b.Status -eq 404) { return 'empty' }
    if ($b.Status -ne 200) { return "error: $(GhError $b)" }
    'existing'
}

# Creates <owner>/<name>, private and empty: under the token's user if it is the owner, else in that org.
function New-GitHubRepo([string] $t) {
    $o, $n = $t -split '/', 2
    $me = Invoke-GitHub GET '/user'
    if ($me.Status -ne 200) { return "cannot create $($t): $(GhError $me)" }
    $path = if ($me.Body.login -eq $o) { '/user/repos' } else { "/orgs/$o/repos" }
    $r = Invoke-GitHub POST $path @{ name = $n; private = $true; auto_init = $false }
    if ($r.Status -ne 201) { return "cannot create $($t): $(GhError $r)" }
    $null
}

# The token user's own repos as owner/name, newest push first (one page of 100), or an error string.
function Get-OwnedRepos {
    $r = Invoke-GitHub GET '/user/repos?affiliation=owner&sort=pushed&direction=desc&per_page=100'
    if ($r.Status -ne 200) { return "error: $(GhError $r)" }
    , @(@($r.Body) | ForEach-Object { $_.full_name })
}

function CloneUrl([string] $t) { "$((GitHubConfig).gitBase)/$t.git" }

# git with the token as a one-command header; for clone, fetch and push only.
function Invoke-GitAuth([string] $Where) {
    $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$((GitHubConfig).token)"))
    Invoke-Git $Where -c credential.helper= -c "http.extraHeader=Authorization: Basic $basic" @args
}

# PR <n> as GitHub returns it ({ state, merged, merge_commit_sha, head.sha, ... }), or $null.
function Get-Pr([string] $t, [int] $n) {
    $r = Invoke-GitHub GET "/repos/$t/pulls/$n"
    if ($r.Status -eq 200) { $r.Body }
}

# The job's open PR as { number, url }, or $null.
function Get-OpenPr([string] $t, [string] $branch) {
    $o = ($t -split '/', 2)[0]
    $r = Invoke-GitHub GET "/repos/$t/pulls?state=open&head=$($o):$branch"
    if ($r.Status -ne 200) { return $null }
    $pr = @($r.Body) | Select-Object -First 1
    if ($pr) { [ordered]@{ number = [int]$pr.number; url = $pr.html_url } }
}
