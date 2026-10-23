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

  Deploys (see "Deploy" in docs/FACTORY-CONTRACT.md):
    POST /repos/o/n/actions/workflows/deploy.yml/dispatches · GET /repos/o/n/actions/runs[/<id>[/jobs]]
    GET /repos/o/n/actions/jobs/<id>/logs · PUT /repos/o/n/environments/<e>
    POST|PATCH /repos/o/n/environments/<e>/variables[/<name>] · POST|PATCH /repos/o/n/actions/variables[/<name>]
  A write under /actions or /environments answers 403 with X-Accepted-GitHub-Permissions when
  <owner>/<name>.deny lists the permission it needs (a PAT missing it).
  A dispatched run completes at once: `failure` when the branch head has deploy-fail.txt (its text is
  the error), else `success`, which writes the workspace's outputs. HCP Terraform's API is served
  under /api/v2 from the same process, so the two agree: projects, varset attachment, workspaces,
  current-state-version-outputs, state-version-outputs, runs (a destroy run applies at once and
  clears the outputs), plans and applies with a log-read-url under /hcp-log/, and safe-delete.
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

# A fine-grained PAT that lacks a permission: <owner>/<name>.deny lists permissions (one per line, e.g.
# environments=write) the token does not have. Returns the one this write needs when it is denied.
function Denied-Permission([string] $o, [string] $n, [string] $method, [string] $rest) {
    $f = Join-Path $GitBase "$o/$n.deny"
    if ($method -eq 'GET' -or -not (Test-Path $f)) { return $null }
    $need = if ($rest -match '^/environments/') { 'environments=write' }
            elseif ($rest -match '^/actions/variables') { 'variables=write' }
            elseif ($rest -match '^/actions/workflows/.+/dispatches$') { 'actions=write' }
    if ($need -and $need -in @(Get-Content $f | ForEach-Object Trim)) { $need }
}
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

function Send-Text($ctx, [int] $status, [string] $text) {
    $res = $ctx.Response
    $res.StatusCode = $status
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $res.ContentType = 'text/plain'
    $res.OutputStream.Write($bytes, 0, $bytes.Length)
    $res.Close()
}

# --- deploy state: one JSON file for Actions and HCP together -----------------------------------
function DeployFile { Join-Path $GitBase 'deploy-state.json' }
function Deploy-State {
    $f = DeployFile
    $s = if (Test-Path $f) { Get-Content $f -Raw | ConvertFrom-Json -AsHashtable } else { $null }
    if (-not $s) { $s = @{} }
    foreach ($k in 'runs', 'vars', 'projects', 'varsets', 'workspaces', 'hcpRuns', 'outputs', 'logs') { if (-not $s.Contains($k)) { $s[$k] = @{} } }
    if (-not $s.Contains('seq')) { $s.seq = 0 }
    $s
}
function Save-DeployState($s) { Set-Content (DeployFile) ($s | ConvertTo-Json -Depth 20) }
function Next-Id($s) { $s.seq = [int]$s.seq + 1; [int]$s.seq }
function Ws-ByName($s, [string] $name) { $s.workspaces.Values | Where-Object { $_.name -eq $name -and -not $_.deleted } | Select-Object -First 1 }
function Ws-ById($s, [string] $id) { $s.workspaces[$id] }

function Dispatch-Run([string] $o, [string] $n, $body) {
    $bare = Bare $o $n
    $sha = (Invoke-StubGit $bare rev-parse --verify --quiet "refs/heads/$($body.ref)").Out
    if (-not $sha) { return @{ status = 422; body = @{ message = "No ref found for: $($body.ref)" } } }
    $fail = (Invoke-StubGit $bare show "$($sha):deploy-fail.txt")
    $s = Deploy-State
    $id = Next-Id $s
    $envName = $body.inputs.environment
    $conclusion = if ($fail.Code -eq 0) { 'failure' } else { 'success' }
    $s.runs["$id"] = [ordered]@{ id = $id; repo = "$o/$n"; head_branch = $body.ref; head_sha = $sha; event = 'workflow_dispatch'
        status = 'completed'; conclusion = $conclusion; environment = $envName; html_url = "https://github.test/$o/$n/actions/runs/$id" }
    $ws = Ws-ByName $s "$n-$envName"
    if ($conclusion -eq 'failure') {
        $s.logs["job-$id"] = "deploy`nRun terraform apply`nError: $($fail.Out)`n##[error]Process completed with exit code 1."
        if ($ws) {
            $rid = "run-$(Next-Id $s)"
            $s.hcpRuns[$rid] = [ordered]@{ id = $rid; ws = $ws.id; status = 'errored'; isDestroy = $false }
            $s.logs["plan-$rid"] = "Terraform v1.13.4`nError: $($fail.Out)"
        }
    } elseif ($ws) {
        $rid = "run-$(Next-Id $s)"
        $s.hcpRuns[$rid] = [ordered]@{ id = $rid; ws = $ws.id; status = 'applied'; isDestroy = $false }
        $s.logs["plan-$rid"] = 'Plan: 21 to add, 0 to change, 0 to destroy.'
        $s.outputs[$ws.id] = [ordered]@{
            api_url = @{ value = "https://ca-$n-api.$envName.test"; sensitive = $false }
            react_url = @{ value = "https://stapp-$n-react-$envName.test"; sensitive = $false }
            qa_user = @{ value = "qa-$envName@$n.test"; sensitive = $false }
            qa_password = @{ value = "pw-$envName"; sensitive = $true }
        }
    }
    Save-DeployState $s
    @{ status = 204 }
}

function Handle-Actions([string] $method, [string] $o, [string] $n, [string] $rest, $body, $ctx) {
    if ($method -eq 'POST' -and $rest -eq '/actions/workflows/deploy.yml/dispatches') { return Dispatch-Run $o $n $body }
    $s = Deploy-State
    if ($method -eq 'GET' -and $rest -eq '/actions/runs') {
        $q = [Web.HttpUtility]::ParseQueryString($ctx.Request.Url.Query)
        $runs = @($s.runs.Values | Where-Object { $_.repo -eq "$o/$n" -and (-not $q['branch'] -or $_.head_branch -eq $q['branch']) } | Sort-Object { [int]$_.id } -Descending)
        return @{ status = 200; body = @{ total_count = $runs.Count; workflow_runs = $runs } }
    }
    if ($method -eq 'GET' -and $rest -match '^/actions/runs/(\d+)$') {
        $r = $s.runs[$Matches[1]]
        if (-not $r) { return @{ status = 404; body = @{ message = 'Not Found' } } }
        return @{ status = 200; body = $r }
    }
    if ($method -eq 'GET' -and $rest -match '^/actions/runs/(\d+)/jobs$') {
        $r = $s.runs[$Matches[1]]
        if (-not $r) { return @{ status = 404; body = @{ message = 'Not Found' } } }
        return @{ status = 200; body = @{ jobs = @(@{ id = [int]$r.id; name = 'deploy'; conclusion = $r.conclusion }) } }
    }
    if ($method -eq 'GET' -and $rest -match '^/actions/jobs/(\d+)/logs$') {
        return @{ status = 200; text = ($s.logs["job-$($Matches[1])"] ?? 'deploy succeeded') }
    }
    if ($method -eq 'PUT' -and $rest -match '^/environments/([^/]+)$') {
        $s.vars["$o/$n/env/$($Matches[1])"] = $s.vars["$o/$n/env/$($Matches[1])"] ?? @{}
        Save-DeployState $s
        return @{ status = 200; body = @{ name = $Matches[1] } }
    }
    if ($rest -match '^/(environments/([^/]+)|actions)/variables(/([^/]+))?$') {
        $scope = if ($Matches[2]) { "$o/$n/env/$($Matches[2])" } else { "$o/$n/repo" }
        $name = $Matches[4]
        $set = $s.vars[$scope] ?? @{}
        if ($method -eq 'POST' -and -not $name) {
            if ($set.Contains($body.name)) { return @{ status = 409; body = @{ message = 'Already exists' } } }
            $set[$body.name] = $body.value; $s.vars[$scope] = $set; Save-DeployState $s
            return @{ status = 201; body = @{} }
        }
        if ($method -eq 'PATCH' -and $name) {
            $set[$name] = $body.value; $s.vars[$scope] = $set; Save-DeployState $s
            return @{ status = 204 }
        }
    }
    @{ status = 404; body = @{ message = 'Not Found' } }
}

function Hcp-Run($r) {
    @{ id = $r.id; type = 'runs'; attributes = @{ status = $r.status; 'is-destroy' = [bool]$r.isDestroy }
       relationships = @{ plan = @{ data = @{ id = "plan-$($r.id)" } }; apply = @{ data = @{ id = "apply-$($r.id)" } } } }
}

function Handle-Hcp([string] $method, [string] $path, $body, $ctx) {
    $s = Deploy-State
    if ($method -eq 'GET' -and $path -match '^/api/v2/organizations/([^/]+)/projects$') {
        $q = [Web.HttpUtility]::ParseQueryString($ctx.Request.Url.Query)
        $want = $q['filter[names]']
        $list = @($s.projects.Values | Where-Object { -not $want -or $_.name -eq $want })
        return @{ status = 200; body = @{ data = @($list | ForEach-Object { @{ id = $_.id; type = 'projects'; attributes = @{ name = $_.name } } }) } }
    }
    if ($method -eq 'POST' -and $path -match '^/api/v2/organizations/([^/]+)/projects$') {
        $id = "prj-$(Next-Id $s)"
        $s.projects[$id] = @{ id = $id; name = $body.data.attributes.name }
        Save-DeployState $s
        return @{ status = 201; body = @{ data = @{ id = $id; type = 'projects'; attributes = @{ name = $body.data.attributes.name } } } }
    }
    if ($method -eq 'POST' -and $path -match '^/api/v2/varsets/([^/]+)/relationships/projects$') {
        $vs = $Matches[1]
        $list = @($s.varsets[$vs] ?? @())
        foreach ($d in @($body.data)) { if ($d.id -notin $list) { $list += $d.id } }
        $s.varsets[$vs] = $list
        Save-DeployState $s
        return @{ status = 204 }
    }
    if ($method -eq 'GET' -and $path -match '^/api/v2/organizations/([^/]+)/workspaces/([^/]+)$') {
        $ws = Ws-ByName $s $Matches[2]
        if (-not $ws) { return @{ status = 404; body = @{ errors = @(@{ status = '404'; title = 'not found' }) } } }
        return @{ status = 200; body = @{ data = @{ id = $ws.id; type = 'workspaces'; attributes = @{ name = $ws.name; 'auto-destroy-at' = $ws.autoDestroyAt } } } }
    }
    if ($method -eq 'POST' -and $path -match '^/api/v2/organizations/([^/]+)/workspaces$') {
        $a = $body.data.attributes
        if (Ws-ByName $s $a.name) { return @{ status = 422; body = @{ errors = @(@{ detail = 'Name has already been taken' }) } } }
        $id = "ws-$(Next-Id $s)"
        $s.workspaces[$id] = [ordered]@{ id = $id; name = $a.name; project = $body.data.relationships.project.data.id; autoDestroyAt = $a.'auto-destroy-at'; deleted = $false }
        Save-DeployState $s
        return @{ status = 201; body = @{ data = @{ id = $id; type = 'workspaces'; attributes = @{ name = $a.name } } } }
    }
    if ($method -eq 'PATCH' -and $path -match '^/api/v2/workspaces/([^/]+)$') {
        $ws = Ws-ById $s $Matches[1]
        if (-not $ws) { return @{ status = 404; body = @{} } }
        if ($body.data.attributes.Contains('auto-destroy-at')) { $ws.autoDestroyAt = $body.data.attributes.'auto-destroy-at' }
        Save-DeployState $s
        return @{ status = 200; body = @{ data = @{ id = $ws.id } } }
    }
    if ($method -eq 'GET' -and $path -match '^/api/v2/workspaces/([^/]+)/current-state-version-outputs$') {
        $wsId = $Matches[1]
        $outs = $s.outputs[$wsId] ?? @{}
        return @{ status = 200; body = @{ data = @($outs.Keys | ForEach-Object {
            @{ id = "wsout-$wsId-$_"; type = 'state-version-outputs'; attributes = @{ name = $_; sensitive = [bool]$outs[$_].sensitive; value = $(if ($outs[$_].sensitive) { $null } else { $outs[$_].value }) } } }) } }
    }
    if ($method -eq 'GET' -and $path -match '^/api/v2/state-version-outputs/wsout-(ws-\d+)-(.+)$') {
        $o = ($s.outputs[$Matches[1]] ?? @{})[$Matches[2]]
        if (-not $o) { return @{ status = 404; body = @{} } }
        return @{ status = 200; body = @{ data = @{ attributes = @{ name = $Matches[2]; sensitive = [bool]$o.sensitive; value = $o.value } } } }
    }
    if ($method -eq 'GET' -and $path -match '^/api/v2/workspaces/([^/]+)/runs$') {
        $wsId = $Matches[1]
        $runs = @($s.hcpRuns.Values | Where-Object { $_.ws -eq $wsId } | Sort-Object { [int]($_.id -replace '\D', '') } -Descending)
        return @{ status = 200; body = @{ data = @($runs | ForEach-Object { Hcp-Run $_ }) } }
    }
    if ($method -eq 'POST' -and $path -eq '/api/v2/runs') {
        $wsId = $body.data.relationships.workspace.data.id
        if (-not (Ws-ById $s $wsId)) { return @{ status = 404; body = @{} } }
        $rid = "run-$(Next-Id $s)"
        $destroy = [bool]$body.data.attributes.'is-destroy'
        $s.hcpRuns[$rid] = [ordered]@{ id = $rid; ws = $wsId; status = 'applied'; isDestroy = $destroy }
        if ($destroy) { $s.outputs.Remove($wsId) }
        Save-DeployState $s
        return @{ status = 201; body = @{ data = (Hcp-Run $s.hcpRuns[$rid]) } }
    }
    if ($method -eq 'GET' -and $path -match '^/api/v2/runs/([^/]+)$') {
        $r = $s.hcpRuns[$Matches[1]]
        if (-not $r) { return @{ status = 404; body = @{} } }
        return @{ status = 200; body = @{ data = (Hcp-Run $r) } }
    }
    if ($method -eq 'GET' -and $path -match '^/api/v2/(plans|applies)/((plan|apply)-(.+))$') {
        return @{ status = 200; body = @{ data = @{ attributes = @{ 'log-read-url' = "http://localhost:$Port/hcp-log/$($Matches[2])" } } } }
    }
    if ($method -eq 'POST' -and $path -match '^/api/v2/workspaces/([^/]+)/actions/safe-delete$') {
        $ws = Ws-ById $s $Matches[1]
        if (-not $ws) { return @{ status = 404; body = @{} } }
        $ws.deleted = $true
        Save-DeployState $s
        return @{ status = 204 }
    }
    @{ status = 404; body = @{ errors = @(@{ status = '404' }) } }
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
    if ($path.StartsWith('/api/v2/')) { return Handle-Hcp $method $path $body $ctx }
    if ($method -eq 'GET' -and $path -match '^/hcp-log/(.+)$') {
        $key = $Matches[1]
        $text = (Deploy-State).logs[$key] ?? $(if ($key -like 'apply-*') { 'Apply complete!' } else { '' })
        return @{ status = 200; text = $text }
    }
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
    if ($rest -match '^/(actions|environments)/') {
        $denied = Denied-Permission $o $n $method $rest
        if ($denied) {
            return @{ status = 403; body = @{ message = 'Resource not accessible by personal access token' }; headers = @{ 'X-Accepted-GitHub-Permissions' = $denied } }
        }
        return Handle-Actions $method $o $n $rest $body $ctx
    }
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
        if ($r.Contains('headers')) { foreach ($k in $r.headers.Keys) { $ctx.Response.Headers[$k] = $r.headers[$k] } }
        if ($r.Contains('text')) { Send-Text $ctx $r.status $r.text } else { Send $ctx $r.status $r.body }
    } catch {
        Write-Warning "github stub: $_"
        try { Send $ctx 500 @{ message = "$_" } } catch { }
    }
}
