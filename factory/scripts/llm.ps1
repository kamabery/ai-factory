<#
.SYNOPSIS
  Dot-sourced: the run mode and the provider it decides. See "Run mode and providers" in
  docs/FACTORY-CONTRACT.md. Needs Read-EnvFile (env-file.ps1). Settings are read from the process
  environment first (a deployed factory sets them there), then the .env file.
#>

$FactoryModeProviders = [ordered]@{ local = 'subscription'; cloud = 'foundry' }
$FoundryModelDefaults = [ordered]@{
    ANTHROPIC_DEFAULT_OPUS_MODEL = 'claude-opus-5-5'
    ANTHROPIC_DEFAULT_SONNET_MODEL = 'claude-sonnet-5-5'
    ANTHROPIC_DEFAULT_HAIKU_MODEL = 'claude-haiku-5-5'
}
# Everything that would route claude anywhere but the mode's own provider when inherited.
$ProviderVars = 'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_BASE_URL', 'CLAUDE_CODE_USE_FOUNDRY',
    'ANTHROPIC_FOUNDRY_RESOURCE', 'ANTHROPIC_FOUNDRY_BASE_URL', 'ANTHROPIC_FOUNDRY_API_KEY', 'ANTHROPIC_FOUNDRY_AUTH_TOKEN'

function Get-LlmSetting([string] $name, [string] $envFile) {
    $v = [Environment]::GetEnvironmentVariable($name)
    if ($v) { return $v }
    (Read-EnvFile $envFile)[$name]
}

# local (the default) or cloud; anything else throws.
function Get-FactoryMode([string] $envFile) {
    $m = Get-LlmSetting 'FACTORY_MODE' $envFile
    if (-not $m) { return 'local' }
    $m = $m.Trim().ToLowerInvariant()
    if (-not $FactoryModeProviders.Contains($m)) { throw "FACTORY_MODE '$m' must be local or cloud" }
    $m
}

function Get-FactoryProvider([string] $envFile) { $FactoryModeProviders[(Get-FactoryMode $envFile)] }

# The claude child's environment for the run mode: name -> value, '' blanks an inherited one.
# $null when cloud mode has no Foundry resource.
function Get-ProviderEnv([string] $envFile) {
    $e = [ordered]@{}
    foreach ($n in $ProviderVars) { $e[$n] = '' }
    if ((Get-FactoryProvider $envFile) -eq 'subscription') { return $e }
    $resource = Get-LlmSetting 'FACTORY_FOUNDRY_RESOURCE' $envFile
    if (-not $resource) { return $null }
    $e.CLAUDE_CODE_USE_FOUNDRY = '1'
    $e.ANTHROPIC_FOUNDRY_RESOURCE = $resource
    foreach ($n in $FoundryModelDefaults.Keys) {
        $v = Get-LlmSetting $n $envFile
        $e[$n] = if ($v) { $v } else { $FoundryModelDefaults[$n] }
    }
    $e
}

# An Entra token for Foundry's data plane (https://ai.azure.com), held in memory and refreshed when
# under 10 minutes remain: a managed identity when deployed, else the machine's az login. Modeled on
# Get-StateToken (state.ps1). $env:FACTORY_FOUNDRY_TOKEN short-circuits it and $env:FACTORY_AZ_CMD
# replaces az, both for tests.
$script:FoundryToken = $null
function Get-FoundryToken {
    if ($env:FACTORY_FOUNDRY_TOKEN) { return $env:FACTORY_FOUNDRY_TOKEN }
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if ($script:FoundryToken -and $script:FoundryToken.expiresOn -gt $now + 600) { return $script:FoundryToken.token }
    if ($env:IDENTITY_ENDPOINT -and $env:IDENTITY_HEADER) {
        $r = Invoke-RestMethod "$($env:IDENTITY_ENDPOINT)?resource=https://ai.azure.com&api-version=2019-08-01" -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
        $script:FoundryToken = @{ token = $r.access_token; expiresOn = [long]$r.expires_on }
        return $script:FoundryToken.token
    }
    $azArgs = 'account', 'get-access-token', '--resource', 'https://ai.azure.com', '--output', 'json'
    # Piped, if only '': a child that inherited this process's stdin would wait on it.
    $out = if ($env:FACTORY_AZ_CMD) { '' | & pwsh -NoProfile -File $env:FACTORY_AZ_CMD @azArgs 2>&1 } else { '' | & az @azArgs 2>&1 }
    $text = (@($out | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $text) { throw "foundry: az account get-access-token failed (is az logged in?): $(($out | Out-String).Trim())" }
    $j = $text | ConvertFrom-Json
    $exp = if ($j.PSObject.Properties['expires_on']) { [long]$j.expires_on } else { ([DateTimeOffset][datetime]$j.expiresOn).ToUnixTimeSeconds() }
    $script:FoundryToken = @{ token = $j.accessToken; expiresOn = $exp }
    $j.accessToken
}
