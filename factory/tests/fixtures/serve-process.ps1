# Dot-sourced by the tests that start a dashboard (roadmap spec 08). Starts the implementation FACTORY_IMPL names:
# serve.ps1, or with FACTORY_IMPL=dotnet the .NET server itself (artifacts/bin/Factory.Server/debug/Factory.Server.dll,
# or FACTORY_SERVER), not serve.ps1's forward to it, so the process returned is the server: its PID is the one
# serve.lock holds, and stopping it stops the server. Parameters are serve.ps1's; the .NET server takes the same as
# --port, --state-dir, --env-file, --watchdog-seconds and --pr-sync-seconds. A parameter left at 0 or empty is not passed.
function Start-FactoryServe {
    param(
        [int] $Port,
        [string] $StateDir,
        [string] $EnvFile,
        [int] $WatchdogSeconds,
        [int] $PrSyncSeconds,
        [string] $RedirectStandardOutput,
        [string] $RedirectStandardError
    )
    $named = [ordered]@{ Port = $Port; StateDir = $StateDir; EnvFile = $EnvFile; WatchdogSeconds = $WatchdogSeconds; PrSyncSeconds = $PrSyncSeconds }
    $scripts = (Resolve-Path (Join-Path $PSScriptRoot '../../scripts')).Path
    if ($env:FACTORY_IMPL -eq 'dotnet') {
        $dll = if ($env:FACTORY_SERVER) { $env:FACTORY_SERVER } else { [IO.Path]::GetFullPath((Join-Path $scripts '../../artifacts/bin/Factory.Server/debug/Factory.Server.dll')) }
        if (-not (Test-Path $dll)) { throw "FACTORY_IMPL=dotnet, but the .NET server is not built at $dll (dotnet build src/factory)" }
        $file = 'dotnet'
        $argv = @($dll)
        foreach ($k in $named.Keys) {
            if ($named[$k]) { $argv += '--' + ($k -creplace '(?<=[a-z])([A-Z])', '-$1').ToLowerInvariant(); $argv += "$($named[$k])" }
        }
    } else {
        $file = 'pwsh'
        $argv = @('-NoProfile', '-File', (Join-Path $scripts 'serve.ps1'))
        foreach ($k in $named.Keys) { if ($named[$k]) { $argv += "-$k"; $argv += "$($named[$k])" } }
    }
    $p = @{ FilePath = $file; ArgumentList = $argv; PassThru = $true; WindowStyle = 'Hidden' }
    if ($RedirectStandardOutput) { $p.RedirectStandardOutput = $RedirectStandardOutput }
    if ($RedirectStandardError) { $p.RedirectStandardError = $RedirectStandardError }
    Start-Process @p
}
