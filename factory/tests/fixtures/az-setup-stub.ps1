# Stands in for `az` in setup-azure.Tests.ps1 (FACTORY_AZ_CMD). Appends one JSON line per call to
# $env:FACTORY_TOOL_LOG: { args, body } with body the --body @file's contents. $env:FACTORY_AZ_STUB
# is 'empty' (nothing exists yet, so the caller creates it) or 'full' (everything already exists).
$Rest = [string[]]$args
$all = $Rest -join ' '
$at = [array]::IndexOf($Rest, '--body')
$body = if ($at -ge 0 -and $Rest[$at + 1].StartsWith('@')) { Get-Content $Rest[$at + 1].Substring(1) -Raw } else { $null }
[IO.File]::AppendAllText($env:FACTORY_TOOL_LOG, ([ordered]@{ args = @($Rest); body = $body } | ConvertTo-Json -Compress) + "`n")
$full = $env:FACTORY_AZ_STUB -eq 'full'
function Arg([string] $name) { $i = [array]::IndexOf($Rest, $name); if ($i -ge 0) { $Rest[$i + 1] } }
function App([string] $name) { @{ appId = "app-$name"; id = "obj-$name" } | ConvertTo-Json -Compress }

$history = Get-Content $env:FACTORY_TOOL_LOG -Raw
switch -Regex ($all) {
    '^login' { exit 0 }
    # A new External tenant can't be selected until az has signed in to it.
    '^account set --subscription ext-new' { if ($history -match '"login","--tenant","ext-new"') { exit 0 }; [Console]::Error.WriteLine('not found'); exit 1 }
    '^account set' { exit 0 }
    '^account show .*tenantId' { if ($env:FACTORY_AZ_STUB -ne 'notenant') { 'tenant-main' }; exit 0 }
    '^rest --method GET .*ciamDirectories' {
        if ($full -or $history -match '"--method","PUT"') {
            '{"properties":{"provisioningState":"Succeeded","tenantId":"ext-new","domainName":"newsub.onmicrosoft.com"}}'; exit 0
        }
        [Console]::Error.WriteLine('(ResourceNotFound) not found'); exit 1
    }
    '^rest --method PUT .*ciamDirectories' { '{"properties":{"provisioningState":"Creating"}}'; exit 0 }
    '^account show' { 'sub-orig'; exit 0 }
    '^ad app list' { if ($full) { '[' + (App (Arg '--display-name')) + ']' } else { '[]' }; exit 0 }
    '^ad app create' { App (Arg '--display-name'); exit 0 }
    '^ad sp list' { if ($full) { 'sp-' + ((Arg '--filter') -replace "^appId eq '(.*)'$", '$1') }; exit 0 }
    '^ad sp create' { 'sp-' + (Arg '--id'); exit 0 }
    '^ad sp show' {
        '{"id":"graph-sp","roles":[{"id":"r-app","value":"Application.ReadWrite.All"},{"id":"r-user","value":"User.ReadWrite.All"},{"id":"r-ev","value":"EventListener.ReadWrite.All"}]}'
        exit 0
    }
    '^role assignment list' { if ($full) { 'ra-1' }; exit 0 }
    '^role assignment create' { '{}'; exit 0 }
    '^rest --method GET .*federatedIdentityCredentials' { if ($full) { '{"value":[{"name":"hcp-terraform"}]}' } else { '{"value":[]}' }; exit 0 }
    '^rest --method GET .*appRoleAssignments' {
        if ($full) { '{"value":[{"appRoleId":"r-app"},{"appRoleId":"r-user"},{"appRoleId":"r-ev"}]}' } else { '{"value":[]}' }; exit 0
    }
    '^rest --method GET .*authenticationEventsFlows' {
        if ($full) { '{"value":[{"displayName":"preview-signin","id":"flow-1"}]}' } else { '{"value":[]}' }; exit 0
    }
    '^rest --method GET .*/organization' {
        '{"value":[{"verifiedDomains":[{"name":"custom.example","isInitial":false},{"name":"extinit.onmicrosoft.com","isInitial":true}]}]}'; exit 0
    }
    '^rest --method POST .*authenticationEventsFlows' { '{"id":"flow-1"}'; exit 0 }
    '^rest --method POST' { '{}'; exit 0 }
}
[Console]::Error.WriteLine("az-setup-stub: unexpected call: $all")
exit 1
