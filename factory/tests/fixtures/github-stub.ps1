<#
.SYNOPSIS
  A stand-in for the slice of the GitHub REST API that factory.ps1 uses, so targets can be tested
  without a token or a network.
.DESCRIPTION
  Repos are bare git repos at <GitBase>/<owner>/<name>.git, which is also what factory.ps1 clones
  (FACTORY_GIT_BASE). Pull requests live in <owner>/<name>.prs.json beside them. Every request is
  appended to <GitBase>/requests.jsonl as { method, path, auth, body }, so tests can assert on it.
  The token's user is -Login. Served:
    GET /user · GET /user/repos · GET /repos/o/n · GET /repos/o/n/branches/<b> · POST /user/repos · POST /orgs/o/repos
    GET|POST /repos/o/n/pulls · GET|PATCH /repos/o/n/pulls/N · PUT /repos/o/n/pulls/N/merge
    DELETE /repos/o/n/git/refs/heads/<b>
  A merge is a real `git merge --no-ff` pushed to main: 405 on a conflict, 409 when `sha` is not the
  head. GET / answers 200 (a liveness probe).
#>
param(
    [Parameter(Mandatory)] [int] $Port,
    [Parameter(Mandatory)] [string] $GitBase,
    [string] $Login = 'test'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $GitBase | Out-Null

function Invoke-StubGit([string] $dir) {
    $out = & git -C $dir -c user.name=github -c user.email=github@stub @args 2>&1
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out | Out-String).Trim() }
}

function Bare([string] $o, [string] $n) { Join-Path $GitBase "$o/$n.git" }
function PrFile([string] $o, [string] $n) { Join-Path $GitBase "$o/$n.prs.json" }
function Prs([string] $o, [string] $n) {
    $f = PrFile $o $n
    if (Test-Path $f) { @(Get-Content $f -Raw | ConvertFrom-Json -AsHashtable) } else { @() }
}
function Save-Prs([string] $o, [string] $n, $prs) { Set-Content (PrFile $o $n) (ConvertTo-Json -InputObject @($prs) -Depth 10) }
function Html([string] $o, [string] $n, [int] $num) { "https://github.test/$o/$n/pull/$num" }

function Send($ctx, [int] $status, $body = $null) {
    $res = $ctx.Response
    $res.StatusCode = $status
    if ($body -is [array] -or $null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $body -Depth 10 -Compress))
        $res.ContentType = 'application/json'
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
    }
    $res.Close()
}

function Read-Body($ctx) {
    $r = [IO.StreamReader]::new($ctx.Request.InputStream, [Text.Encoding]::UTF8)
    try { $r.ReadToEnd() } finally { $r.Dispose() }
}

function New-StubRepo([string] $o, [string] $n, $body) {
    $bare = Bare $o $n
    if (Test-Path $bare) { return @{ status = 422; body = @{ message = 'name already exists on this account' } } }
    New-Item -ItemType Directory -Force $bare | Out-Null
    Invoke-StubGit $bare init -q --bare -b main | Out-Null
    Set-Content (Join-Path $GitBase "$o/$n.repo.json") ($body | ConvertTo-Json -Compress)
    @{ status = 201; body = @{ full_name = "$o/$n"; private = [bool]$body.private; default_branch = 'main' } }
}

function Merge-StubPr([string] $o, [string] $n, [int] $num, $body) {
    $prs = Prs $o $n
    $pr = $prs | Where-Object { $_.number -eq $num } | Select-Object -First 1
    if (-not $pr -or $pr.state -ne 'open') { return @{ status = 404; body = @{ message = 'Not Found' } } }
    $bare = Bare $o $n
    $head = (Invoke-StubGit $bare rev-parse "refs/heads/$($pr.head)").Out
    if ($body.sha -and $body.sha -ne $head) { return @{ status = 409; body = @{ message = 'Head branch was modified. Review and try the merge again.' } } }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "gh-stub-$([guid]::NewGuid().ToString('N'))"
    try {
        Invoke-StubGit $GitBase clone -q $bare $tmp | Out-Null
        $r = Invoke-StubGit $tmp merge --no-ff "origin/$($pr.head)" -m ($body.commit_title ?? "Merge pull request #$num")
        if ($r.Code -ne 0) { return @{ status = 405; body = @{ message = 'Pull Request is not mergeable' } } }
        $r = Invoke-StubGit $tmp push -q origin HEAD:main
        if ($r.Code -ne 0) { return @{ status = 500; body = @{ message = $r.Out } } }
        $sha = (Invoke-StubGit $tmp rev-parse HEAD).Out
    } finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
    $pr.state = 'closed'; $pr.merged = $true; $pr.merge_commit_sha = $sha; $pr.head_sha = $head
    Save-Prs $o $n $prs
    @{ status = 200; body = @{ sha = $sha; merged = $true; message = 'Pull Request successfully merged' } }
}

function Handle($ctx, [string] $method, [string] $path, $body) {
    if ($method -eq 'GET' -and $path -eq '/') { return @{ status = 200 } }
    if ($method -eq 'GET' -and $path -eq '/user') { return @{ status = 200; body = @{ login = $Login } } }
    if ($method -eq 'GET' -and $path -eq '/user/repos') {
        $dirs = @(Get-ChildItem (Join-Path $GitBase $Login) -Directory -Filter '*.git' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        return @{ status = 200; body = @($dirs | ForEach-Object { @{ full_name = "$Login/$($_.Name -replace '\.git$', '')" } }) }
    }
    if ($method -eq 'POST' -and $path -eq '/user/repos') { return New-StubRepo $Login $body.name $body }
    if ($method -eq 'POST' -and $path -match '^/orgs/([^/]+)/repos$') { return New-StubRepo $Matches[1] $body.name $body }
    if ($path -notmatch '^/repos/([^/]+)/([^/]+)(/.*)?$') { return @{ status = 404; body = @{ message = 'Not Found' } } }
    $o = $Matches[1]; $n = $Matches[2]; $rest = $Matches[3] ?? ''
    $bare = Bare $o $n
    if (-not (Test-Path $bare)) { return @{ status = 404; body = @{ message = 'Not Found' } } }
    if ($method -eq 'GET' -and $rest -eq '') {
        $meta = Get-Content (Join-Path $GitBase "$o/$n.repo.json") -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json -AsHashtable
        return @{ status = 200; body = @{ full_name = "$o/$n"; private = [bool]($meta -and $meta.private); default_branch = 'main' } }
    }
    if ($method -eq 'GET' -and $rest -match '^/branches/(.+)$') {
        $sha = (Invoke-StubGit $bare rev-parse --verify --quiet "refs/heads/$($Matches[1])").Out
        if (-not $sha) { return @{ status = 404; body = @{ message = 'Branch not found' } } }
        return @{ status = 200; body = @{ name = $Matches[1]; commit = @{ sha = $sha } } }
    }
    if ($method -eq 'GET' -and $rest -eq '/pulls') {
        $q = [Web.HttpUtility]::ParseQueryString($ctx.Request.Url.Query)
        $headBranch = if ($q['head']) { ($q['head'] -split ':', 2)[-1] }
        $list = @(Prs $o $n | Where-Object { (-not $q['state'] -or $q['state'] -eq 'all' -or $_.state -eq $q['state']) -and (-not $headBranch -or $_.head -eq $headBranch) })
        return @{ status = 200; body = @($list | ForEach-Object { @{ number = $_.number; html_url = $_.html_url; state = $_.state; head = @{ ref = $_.head } } }) }
    }
    if ($method -eq 'POST' -and $rest -eq '/pulls') {
        if (-not (Invoke-StubGit $bare rev-parse --verify --quiet "refs/heads/$($body.head)").Out) { return @{ status = 422; body = @{ message = 'Validation Failed: head' } } }
        $prs = Prs $o $n
        if ($prs | Where-Object { $_.head -eq $body.head -and $_.state -eq 'open' }) { return @{ status = 422; body = @{ message = "A pull request already exists for $($o):$($body.head)." } } }
        $num = @($prs).Count + 1
        $pr = [ordered]@{ number = $num; head = $body.head; base = $body.base; title = $body.title; state = 'open'; merged = $false; html_url = (Html $o $n $num) }
        Save-Prs $o $n (@($prs) + $pr)
        return @{ status = 201; body = @{ number = $num; html_url = $pr.html_url; state = 'open' } }
    }
    if ($method -eq 'GET' -and $rest -match '^/pulls/(\d+)$') {
        $pr = Prs $o $n | Where-Object { $_.number -eq [int]$Matches[1] } | Select-Object -First 1
        if (-not $pr) { return @{ status = 404; body = @{ message = 'Not Found' } } }
        $head = if ($pr.merged) { $pr.head_sha } else { (Invoke-StubGit $bare rev-parse --verify --quiet "refs/heads/$($pr.head)").Out }
        return @{ status = 200; body = @{ number = $pr.number; html_url = $pr.html_url; state = $pr.state; merged = [bool]$pr.merged
                                          merge_commit_sha = $pr.merge_commit_sha; head = @{ ref = $pr.head; sha = $head } } }
    }
    if ($method -eq 'PATCH' -and $rest -match '^/pulls/(\d+)$') {
        $prs = Prs $o $n
        $pr = $prs | Where-Object { $_.number -eq [int]$Matches[1] } | Select-Object -First 1
        if (-not $pr) { return @{ status = 404; body = @{ message = 'Not Found' } } }
        if ($body.state) { $pr.state = $body.state }
        Save-Prs $o $n $prs
        return @{ status = 200; body = @{ number = $pr.number; state = $pr.state } }
    }
    if ($method -eq 'PUT' -and $rest -match '^/pulls/(\d+)/merge$') { return Merge-StubPr $o $n ([int]$Matches[1]) $body }
    if ($method -eq 'DELETE' -and $rest -match '^/git/refs/heads/(.+)$') {
        $r = Invoke-StubGit $bare branch --delete --force $Matches[1]
        if ($r.Code -ne 0) { return @{ status = 422; body = @{ message = 'Reference does not exist' } } }
        return @{ status = 204 }
    }
    @{ status = 404; body = @{ message = 'Not Found' } }
}

$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "github stub on http://localhost:$Port/ (repos: $GitBase)"
while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    try {
        New-Item -ItemType Directory -Force $GitBase | Out-Null   # simulate.ps1 -Reset may have deleted it
        $method = $ctx.Request.HttpMethod
        $path = $ctx.Request.Url.AbsolutePath
        $raw = Read-Body $ctx
        $body = if ($raw) { $raw | ConvertFrom-Json -AsHashtable } else { @{} }
        $log = [ordered]@{ method = $method; path = $path; auth = "$($ctx.Request.Headers['Authorization'])"; body = $body }
        [IO.File]::AppendAllText((Join-Path $GitBase 'requests.jsonl'), ($log | ConvertTo-Json -Compress -Depth 10) + "`n")
        $r = Handle $ctx $method $path $body
        Send $ctx $r.status $r.body
    } catch {
        Write-Warning "github stub: $_"
        try { Send $ctx 500 @{ message = "$_" } } catch { }
    }
}
