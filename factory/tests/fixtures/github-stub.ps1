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
    GET /repos/o/n/actions/oidc/customization/sub (<owner>/<name>.oidc.json, else {use_default:true})
    POST|PATCH /repos/o/n/environments/<e>/variables[/<name>] · POST|PATCH /repos/o/n/actions/variables[/<name>]
  A write under /actions or /environments answers 403 with X-Accepted-GitHub-Permissions when
  <owner>/<name>.deny lists the permission it needs (a PAT missing it).
  A dispatched run completes at once: `failure` when the branch head has deploy-fail.txt (its text is
  the error), else `success`, which writes the workspace's outputs. HCP Terraform's API is served
  under /api/v2 from the same process, so the two agree: projects, varset attachment, workspaces,
  current-state-version-outputs, state-version-outputs, runs (a destroy run applies at once and
  clears the outputs), plans and applies with a log-read-url under /hcp-log/, safe-delete, and a
  variable set's variables (<GitBase>/varset-vars.json, else two fixed ones). A j000 preview (the
  canary's) gets urls under /preview/<o>/<n>/j000/ that this stub serves: web/api/health answers 200
  Healthy, or 503 while <owner>/<name>.unhealthy exists, api/health answers 401 and web 200.
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
    # Like GitHub: an input the branch's deploy.yml does not declare is refused (environment is
    # always declared; the stub's branches carry no workflow file unless a test commits one).
    $wf = (Invoke-StubGit $bare show "$($sha):.github/workflows/deploy.yml").Out
    $extra = @($body.inputs.Keys | Where-Object { $_ -ne 'environment' -and "$wf" -notmatch "(?m)^\s+$($_):" })
    if ($extra) { return @{ status = 422; body = @{ message = "Unexpected inputs provided: [$($extra -join ', ')]" } } }
    $fail = (Invoke-StubGit $bare show "$($sha):deploy-fail.txt")
    $s = Deploy-State
    $id = Next-Id $s
    $envName = $body.inputs.environment
    $conclusion = if ($fail.Code -eq 0) { 'failure' } else { 'success' }
    $s.runs["$id"] = [ordered]@{ id = $id; repo = "$o/$n"; head_branch = $body.ref; head_sha = $sha; event = 'workflow_dispatch'
        status = 'completed'; conclusion = $conclusion; environment = $envName; html_url = "https://github.test/$o/$n/actions/runs/$id"
        inputs = $body.inputs }
    # <owner>/<name>.slow: the run stays in_progress until the file is gone (a real one takes ~60 minutes).
    if (Test-Path (Join-Path $GitBase "$o/$n.slow")) { $s.runs["$id"].status = 'in_progress'; $s.runs["$id"].conclusion = $null; $s.runs["$id"].final = $conclusion }
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
        # The canary's j000 smoke-checks its urls, so they point back at this stub (see /preview below).
        $api = if ($envName -eq 'j000') { "http://localhost:$Port/preview/$o/$n/j000/api" } else { "https://ca-$n-api.$envName.test" }
        $web = if ($envName -eq 'j000') { "http://localhost:$Port/preview/$o/$n/j000/web" } else { "https://stapp-$n-react-$envName.test" }
        $s.outputs[$ws.id] = [ordered]@{
            api_url = @{ value = $api; sensitive = $false }
            react_url = @{ value = $web; sensitive = $false }
            qa_user = @{ value = "qa-$envName@$n.test"; sensitive = $false }
            qa_password = @{ value = "pw-$envName"; sensitive = $true }
        }
        # <owner>/<name>.noauth: a target without the auth add-on, so no QA user to sign in as.
        if (Test-Path (Join-Path $GitBase "$o/$n.noauth")) { $s.outputs[$ws.id].Remove('qa_user'); $s.outputs[$ws.id].Remove('qa_password') }
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
        if ($r.status -ne 'completed' -and -not (Test-Path (Join-Path $GitBase "$($r.repo).slow"))) {
            $r.status = 'completed'; $r.conclusion = $r.final; Save-DeployState $s
        }
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
    if ($method -eq 'GET' -and $rest -eq '/actions/oidc/customization/sub') {
        $f = Join-Path $GitBase "$o/$n.oidc.json"
        return @{ status = 200; body = if (Test-Path $f) { Get-Content $f -Raw | ConvertFrom-Json } else { @{ use_default = $true } } }
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
        $destroy = [bool]$body.data.attributes.'is-destroy'
        # <GitBase>/destroy-no-config: a workspace no apply ever reached (J-018).
        if ($destroy -and (Test-Path (Join-Path $GitBase 'destroy-no-config'))) {
            return @{ status = 422; body = @{ errors = @(@{ status = '422'; title = 'invalid'; detail = 'Configuration version is missing' }) } }
        }
        $rid = "run-$(Next-Id $s)"
        $s.hcpRuns[$rid] = [ordered]@{ id = $rid; ws = $wsId; status = 'applied'; isDestroy = $destroy }
        # <GitBase>/destroy-fails: a destroy errors as Azure's late-added alert rule made canary-8's.
        if ($destroy -and (Test-Path (Join-Path $GitBase 'destroy-fails'))) {
            $s.hcpRuns[$rid].status = 'errored'
            $s.logs["apply-$rid"] = 'Error: deleting Resource Group "rg-x": the Resource Group still contains Resources.'
        # <GitBase>/destroy-hangs: the run stays applying until the file goes, as a Container Apps environment's delete does.
        } elseif ($destroy -and (Test-Path (Join-Path $GitBase 'destroy-hangs'))) {
            $s.hcpRuns[$rid].status = 'applying'
        # <GitBase>/destroy-nothing: the workspace is already empty, so HCP has nothing to apply (J-025).
        } elseif ($destroy -and (Test-Path (Join-Path $GitBase 'destroy-nothing'))) {
            $s.hcpRuns[$rid].status = 'planned_and_finished'
        } elseif ($destroy) { $s.outputs.Remove($wsId) }
        Save-DeployState $s
        return @{ status = 201; body = @{ data = (Hcp-Run $s.hcpRuns[$rid]) } }
    }
    if ($method -eq 'GET' -and $path -match '^/api/v2/runs/([^/]+)$') {
        $r = $s.hcpRuns[$Matches[1]]
        if (-not $r) { return @{ status = 404; body = @{} } }
        if ($r.status -eq 'applying' -and -not (Test-Path (Join-Path $GitBase 'destroy-hangs'))) {
            $r.status = 'applied'
            $s.outputs.Remove($r.ws)
            Save-DeployState $s
        }
        return @{ status = 200; body = @{ data = (Hcp-Run $r) } }
    }
    if ($method -eq 'GET' -and $path -match '^/api/v2/(plans|applies)/((plan|apply)-(.+))$') {
        return @{ status = 200; body = @{ data = @{ attributes = @{ 'log-read-url' = "http://localhost:$Port/hcp-log/$($Matches[2])" } } } }
    }
    # The variable set's variables: <GitBase>/varset-vars.json, else two fixed ones.
    if ($method -eq 'GET' -and $path -match '^/api/v2/varsets/([^/]+)/relationships/vars$') {
        $f = Join-Path $GitBase 'varset-vars.json'
        $vars = if (Test-Path $f) { @(Get-Content $f -Raw | ConvertFrom-Json) } else {
            @(@{ key = 'TFC_AZURE_PROVIDER_AUTH'; value = 'true'; category = 'env'; sensitive = $false }
              @{ key = 'TFC_AZURE_RUN_CLIENT_ID'; value = 'run-client'; category = 'env'; sensitive = $false }) }
        return @{ status = 200; body = @{ data = @($vars | ForEach-Object { @{ id = "var-$($_.key)"; type = 'vars'; attributes = $_ } }) } }
    }
    # A new variable, or a change to one, kept in <GitBase>/varset-vars.json.
    if ($method -in 'POST', 'PATCH' -and $path -match '^/api/v2/varsets/([^/]+)/relationships/vars') {
        $f = Join-Path $GitBase 'varset-vars.json'
        $have = @(if (Test-Path $f) { Get-Content $f -Raw | ConvertFrom-Json })
        $a = $body.data.attributes
        $vars = @($have | Where-Object { $_.key -ne $a.key }) + @([pscustomobject]@{ key = $a.key; value = $a.value; category = $a.category; hcl = [bool]$a.hcl; sensitive = [bool]$a.sensitive })
        Set-Content $f (ConvertTo-Json -InputObject @($vars) -Depth 5)
        return @{ status = $(if ($method -eq 'POST') { 201 } else { 200 }); body = @{ data = @{ id = "var-$($a.key)"; type = 'vars'; attributes = $a } } }
    }
    if ($method -eq 'POST' -and $path -match '^/api/v2/workspaces/([^/]+)/actions/safe-delete$') {
        $ws = Ws-ById $s $Matches[1]
        if (-not $ws) { return @{ status = 404; body = @{} } }
        # <GitBase>/safe-delete-busy holds how many more calls answer 409, as HCP does while it
        # processes the state a destroy just wrote (J-025).
        $busy = Join-Path $GitBase 'safe-delete-busy'
        if (Test-Path $busy) {
            $left = [int](Get-Content $busy -Raw)
            if ($left -gt 0) {
                Set-Content $busy ($left - 1)
                return @{ status = 409; body = @{ errors = @(@{ status = '409'; title = 'conflict'; detail = 'Latest workspace state is being processed to discover resources, please try again later' }) } }
            }
        }
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
    # Into the PR's base branch, as GitHub does: main, or the branch a stacked PR points at.
    $into = if ($pr.base) { $pr.base } else { 'main' }
    try {
        Invoke-StubGit $GitBase clone -q --branch $into $bare $tmp | Out-Null
        $r = Invoke-StubGit $tmp merge --no-ff "origin/$($pr.head)" -m ($body.commit_title ?? "Merge pull request #$num")
        if ($r.Code -ne 0) { return @{ status = 405; body = @{ message = 'Pull Request is not mergeable' } } }
        $r = Invoke-StubGit $tmp push -q origin "HEAD:$into"
        if ($r.Code -ne 0) { return @{ status = 500; body = @{ message = $r.Out } } }
        $sha = (Invoke-StubGit $tmp rev-parse HEAD).Out
    } finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
    $pr.state = 'closed'; $pr.merged = $true; $pr.merge_commit_sha = $sha; $pr.head_sha = $head
    Save-Prs $o $n $prs
    @{ status = 200; body = @{ sha = $sha; merged = $true; message = 'Pull Request successfully merged' } }
}

function Handle($ctx, [string] $method, [string] $path, $body) {
    if ($method -eq 'GET' -and $path -eq '/') { return @{ status = 200 } }
    # A j000 preview's app, reached as a browser does through the front end's /api: /health answers
    # Healthy, or 503 while <owner>/<name>.unhealthy exists. The API's own address answers 401, as
    # a Static Web App's linked backend does.
    if ($method -eq 'GET' -and $path -match '^/preview/([^/]+)/([^/]+)/[^/]+/(api/health|web|web/api/health)$') {
        if ($Matches[3] -eq 'web') { return @{ status = 200; text = '<!doctype html>' } }
        if ($Matches[3] -eq 'api/health') { return @{ status = 401 } }
        if (Test-Path (Join-Path $GitBase "$($Matches[1])/$($Matches[2]).unhealthy")) { return @{ status = 503; text = 'Unhealthy' } }
        return @{ status = 200; text = 'Healthy' }
    }
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
        return @{ status = 200; body = @($list | ForEach-Object { @{ number = $_.number; html_url = $_.html_url; state = $_.state; head = @{ ref = $_.head }; base = @{ ref = $_.base } } }) }
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
                                          merge_commit_sha = $pr.merge_commit_sha; head = @{ ref = $pr.head; sha = $head }; base = @{ ref = $pr.base } } }
    }
    if ($method -eq 'PATCH' -and $rest -match '^/pulls/(\d+)$') {
        $prs = Prs $o $n
        $pr = $prs | Where-Object { $_.number -eq [int]$Matches[1] } | Select-Object -First 1
        if (-not $pr) { return @{ status = 404; body = @{ message = 'Not Found' } } }
        if ($body.state) { $pr.state = $body.state }
        if ($body.base) {
            if (-not (Invoke-StubGit $bare rev-parse --verify --quiet "refs/heads/$($body.base)").Out) { return @{ status = 422; body = @{ message = 'Validation Failed: base' } } }
            $pr.base = $body.base
        }
        Save-Prs $o $n $prs
        return @{ status = 200; body = @{ number = $pr.number; state = $pr.state; base = @{ ref = $pr.base } } }
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
        $gone = $Matches[1]
        $r = Invoke-StubGit $bare branch --delete --force $gone
        if ($r.Code -ne 0) { return @{ status = 422; body = @{ message = 'Reference does not exist' } } }
        # As GitHub does: an open PR whose base branch is deleted is closed.
        $prs = Prs $o $n
        $closed = @($prs | Where-Object { $_.state -eq 'open' -and $_.base -eq $gone })
        if ($closed) { foreach ($p in $closed) { $p.state = 'closed' }; Save-Prs $o $n $prs }
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
