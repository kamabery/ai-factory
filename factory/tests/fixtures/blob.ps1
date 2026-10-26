# Shared by the Pester files that exercise the azure state backend (see "State backends" in the
# contract). Dot-source it in BeforeAll, call Start-BlobStub there and Stop-BlobStub in AfterAll, and
# Use-AzureState in BeforeEach to point FACTORY_STATE at a fresh container on the stub.

function Start-BlobStub([double] $LeaseScale = 1.0) {
    $stub = Join-Path $PSScriptRoot 'blob-stub.ps1'
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $script:BlobPort = $l.LocalEndpoint.Port; $l.Stop()
    $script:BlobLog = Join-Path $TestDrive 'blob-requests.jsonl'
    $script:BlobProc = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $stub, '-Port', $script:BlobPort, '-Log', $script:BlobLog,
        '-LeaseScale', $LeaseScale -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(20)
    while ($true) {
        try { Invoke-WebRequest "http://localhost:$script:BlobPort/_stub/state" -TimeoutSec 1 | Out-Null; break }
        catch { if ((Get-Date) -gt $deadline) { throw 'blob stub did not start' }; Start-Sleep -Milliseconds 200 }
    }
}

function Stop-BlobStub { if ($script:BlobProc) { Stop-Process -Id $script:BlobProc.Id -Force -ErrorAction SilentlyContinue } }

# A fresh container (its own name on the stub), the stub's token, and a local dir for the token cache.
function Use-AzureState {
    $c = 'c' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $env:FACTORY_STATE = 'azure'
    $env:FACTORY_BLOB_URL = "http://localhost:$script:BlobPort/$c"
    $env:FACTORY_STATE_TOKEN = 'test-token'
    $env:FACTORY_LOCAL_DIR = Join-Path $TestDrive "local-$c"
    $script:Container = $c
}

function Clear-AzureState {
    Remove-Item Env:FACTORY_STATE, Env:FACTORY_BLOB_URL, Env:FACTORY_STATE_TOKEN, Env:FACTORY_LOCAL_DIR, Env:FACTORY_STATE_CAP -ErrorAction SilentlyContinue
}

# The stub's blobs in the current container: name -> { type, text, leased }, and the version list.
function Blob-State {
    $s = Invoke-RestMethod "http://localhost:$script:BlobPort/_stub/state"
    $out = @{}
    foreach ($p in $s.blobs.PSObject.Properties) {
        if ($p.Name.StartsWith("$script:Container/")) { $out[$p.Name.Substring($script:Container.Length + 1)] = $p.Value }
    }
    $vers = @($s.versions | Where-Object { $_.name.StartsWith("$script:Container/") } | ForEach-Object { $_.name.Substring($script:Container.Length + 1) })
    [pscustomobject]@{ Blobs = $out; Versions = $vers }
}

function Blob-Text([string] $name) { (Blob-State).Blobs[$name].text }
