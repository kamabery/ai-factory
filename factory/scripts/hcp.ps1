# HCP Terraform access for factory.ps1, which dot-sources this file after github.ps1. See "Deploy" in
# docs/FACTORY-CONTRACT.md. HCP_TOKEN is read from .env into $script:Hcp and goes out only as a
# request header. Helpers return the parsed JSON:API body, or throw a one-line message.

$script:Hcp = $null

function HcpConfig {
    if ($script:Hcp) { return $script:Hcp }
    $vars = Read-EnvFile (EnvFilePath)
    $script:Hcp = @{
        org = $vars['HCP_ORG']
        token = $vars['HCP_TOKEN']
        teamToken = $vars['HCP_TEAM_TOKEN']
        varset = $vars['HCP_AZURE_VARSET']
        api = $(if ($vars['HCP_API_URL']) { $vars['HCP_API_URL'] } else { 'https://app.terraform.io' }).TrimEnd('/')
        azureClientId = $vars['AZURE_GH_CLIENT_ID']
        azureTenantId = $vars['AZURE_TENANT_ID']
        azureSubscriptionId = $vars['AZURE_SUBSCRIPTION_ID']
    }
    $script:Hcp
}

function Deploys-On { [bool](HcpConfig).org }

# Returns the parsed body (or $null for 204); throws on any other status.
function Invoke-Hcp([string] $Method, [string] $Path, $Body = $null, [int[]] $Ok = @(200, 201, 202, 204), [switch] $AllowNotFound) {
    $c = HcpConfig
    if (-not $c.token) { throw 'HCP_TOKEN is not set in .env' }
    $p = @{
        Method = $Method; Uri = "$($c.api)/api/v2$Path"; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'
        Headers = @{ Authorization = "Bearer $($c.token)" }; ContentType = 'application/vnd.api+json'
    }
    if ($null -ne $Body) { $p.Body = $Body | ConvertTo-Json -Depth 10 -Compress }
    try { $res = Invoke-RestMethod @p } catch { throw "HCP $Method $($Path): $($_.Exception.Message)" }
    if ($AllowNotFound -and $status -eq 404) { return $null }
    if ($status -notin $Ok) {
        $why = try { (@($res.errors) | ForEach-Object { $_.detail ?? $_.title }) -join '; ' } catch { "$res" }
        throw "HCP $status on $Method $($Path): $why"
    }
    $res
}

# The project's id, created if missing, with the Azure variable set attached.
function Ensure-HcpProject([string] $name) {
    $c = HcpConfig
    $found = (Invoke-Hcp GET "/organizations/$($c.org)/projects?filter%5Bnames%5D=$([uri]::EscapeDataString($name))").data |
        Where-Object { $_.attributes.name -eq $name } | Select-Object -First 1
    $id = if ($found) { $found.id } else {
        (Invoke-Hcp POST "/organizations/$($c.org)/projects" @{ data = @{ type = 'projects'; attributes = @{ name = $name } } }).data.id
    }
    if ($c.varset) {
        Invoke-Hcp POST "/varsets/$($c.varset)/relationships/projects" @{ data = @(@{ type = 'projects'; id = $id }) } | Out-Null
    }
    $id
}

# The workspace's id, created in the project if missing. $autoDestroyAt (ISO 8601) is set on create
# and on every call, so a redeploy pushes the timer out again.
function Ensure-HcpWorkspace([string] $name, [string] $projectId, [string] $autoDestroyAt = $null) {
    $c = HcpConfig
    $ws = Invoke-Hcp GET "/organizations/$($c.org)/workspaces/$name" -AllowNotFound
    if (-not $ws) {
        $attrs = @{ name = $name; 'execution-mode' = 'remote' }
        if ($autoDestroyAt) { $attrs['auto-destroy-at'] = $autoDestroyAt }
        return (Invoke-Hcp POST "/organizations/$($c.org)/workspaces" @{ data = @{ type = 'workspaces'; attributes = $attrs
            relationships = @{ project = @{ data = @{ type = 'projects'; id = $projectId } } } } }).data.id
    }
    if ($autoDestroyAt) {
        Invoke-Hcp PATCH "/workspaces/$($ws.data.id)" @{ data = @{ type = 'workspaces'; attributes = @{ 'auto-destroy-at' = $autoDestroyAt } } } | Out-Null
    }
    $ws.data.id
}

function Get-HcpWorkspaceId([string] $name) {
    $ws = Invoke-Hcp GET "/organizations/$((HcpConfig).org)/workspaces/$name" -AllowNotFound
    if ($ws) { $ws.data.id }
}

# name -> value for the workspace's current outputs, sensitive ones fetched one by one.
function Get-HcpOutputs([string] $workspaceId, [string[]] $sensitive = @()) {
    $out = @{}
    foreach ($o in @((Invoke-Hcp GET "/workspaces/$workspaceId/current-state-version-outputs").data)) {
        $name = $o.attributes.name
        $out[$name] = if ($o.attributes.sensitive) {
            if ($name -in $sensitive) { (Invoke-Hcp GET "/state-version-outputs/$($o.id)").data.attributes.value }
        } else { $o.attributes.value }
    }
    $out
}

# The plan and apply logs of the workspace's latest run, as text.
function Get-HcpRunLog([string] $workspaceId) {
    $run = @((Invoke-Hcp GET "/workspaces/$workspaceId/runs?page%5Bsize%5D=1").data) | Select-Object -First 1
    if (-not $run) { return '(no HCP run)' }
    $text = "run $($run.id): $($run.attributes.status)"
    foreach ($part in @(@('plans', $run.relationships.plan.data.id), @('applies', $run.relationships.apply.data.id))) {
        if (-not $part[1]) { continue }
        $url = (Invoke-Hcp GET "/$($part[0])/$($part[1])" -AllowNotFound).data.attributes.'log-read-url'
        if ($url) { $text += "`n--- $($part[0])`n" + (Invoke-RestMethod -Uri $url -SkipHttpErrorCheck) }
    }
    $text
}

# Queues an auto-applied destroy run and waits for it to finish; returns its final status.
function Invoke-HcpDestroy([string] $workspaceId, [int] $pollSeconds, [datetime] $deadline) {
    $run = (Invoke-Hcp POST '/runs' @{ data = @{ type = 'runs'; attributes = @{ 'is-destroy' = $true; 'auto-apply' = $true; message = 'factory: tear down the preview' }
        relationships = @{ workspace = @{ data = @{ type = 'workspaces'; id = $workspaceId } } } } }).data
    $final = 'applied', 'errored', 'canceled', 'discarded', 'force_canceled', 'planned_and_finished'
    while ($run.attributes.status -notin $final) {
        if ((Get-Date) -gt $deadline) { return 'timed out' }
        Start-Sleep -Seconds $pollSeconds
        $run = (Invoke-Hcp GET "/runs/$($run.id)").data
    }
    $run.attributes.status
}

# The Azure variable set's variables as { key, value, category, sensitive }; a sensitive value is null.
function Get-HcpVarsetVars {
    $c = HcpConfig
    if (-not $c.varset) { return @() }
    @((Invoke-Hcp GET "/varsets/$($c.varset)/relationships/vars").data | ForEach-Object { $_.attributes })
}

# HCP answers 409 while it processes the state a destroy just wrote (J-025), so that is waited out.
function Remove-HcpWorkspace([string] $workspaceId, [int] $pollSeconds = 15) {
    $deadline = (Get-Date).AddMinutes(5)
    while ($true) {
        try { Invoke-Hcp POST "/workspaces/$workspaceId/actions/safe-delete" | Out-Null; return }
        catch {
            if ($_.Exception.Message -notmatch '^HCP 409 ' -or (Get-Date) -gt $deadline) { throw }
            Start-Sleep -Seconds $pollSeconds
        }
    }
}
