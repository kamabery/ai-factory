param([string]$PreviewA = 'j029', [string]$PreviewB = 'j030', [string]$Prefix = 'proof')
# Live isolation checks for two previews that are up together on the shared platform, run by hand (it is not in
# Pester). It opens the server's firewall to this machine for about a minute and reads both previews' connection
# strings from the vault, so a person runs it: the agent's auto mode refuses it (docs/PLAN-PR1-PROOFS.md).
# Needs Docker, for psql. Prints PASS or FAIL per check, and never prints a password.
# The platform names are setup-platform.ps1's for this subscription.
$rg = 'rg-factory-platform'; $srv = $env:FACTORY_PLATFORM_PG ?? 'psql-factory-3c2b3241'; $kv = $env:FACTORY_PLATFORM_KV ?? 'kv-factory-3c2b3241'; $rule = 'isolation-probe'
$script:fails = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') { "$(if ($ok) { 'PASS' } else { $script:fails++; 'FAIL' })  $name$(if ($detail) { "  [$detail]" })" }

function Conn([string]$p) {
    $raw = az keyvault secret show --vault-name $kv -n "$Prefix-$p-appdb-connection" --query value -o tsv 2>$null
    $h = @{}; foreach ($kvp in "$raw" -split ';') { if ($kvp -match '^\s*([^=]+)=(.*)$') { $h[$Matches[1].Trim().ToLowerInvariant()] = $Matches[2] } }
    [pscustomobject]@{ Host = $h['host']; Db = $h['database']; User = $h['username']; Pw = $h['password'] }
}
# Runs SQL as a role against a database. Returns exit code and output, with the password only in the child's environment.
function Sql($as, [string]$db, [string]$sql) {
    $env:PGPASSWORD = $as.Pw
    $out = & docker run --rm -e PGPASSWORD -e PGSSLMODE=require postgres:16 psql -h $as.Host -U $as.User -d $db -v ON_ERROR_STOP=1 -At -c $sql 2>&1
    $code = $LASTEXITCODE
    $env:PGPASSWORD = $null
    [pscustomobject]@{ Code = $code; Out = (($out | ForEach-Object { "$_" }) -join ' ').Replace($as.Pw, '***') }
}

$ca = Conn $PreviewA; $cb = Conn $PreviewB; if (-not $ca.Pw -or -not $cb.Pw) { 'could not read a connection string'; exit 99 }
"preview A: db=$($ca.Db) role=$($ca.User)"
"preview B: db=$($cb.Db) role=$($cb.User)"
Check 'each preview has its own database and role' ($ca.Db -and $cb.Db -and $ca.Db -ne $cb.Db -and $ca.User -ne $cb.User)

$ip = (Invoke-RestMethod https://api.ipify.org).Trim()
az postgres flexible-server firewall-rule create -g $rg -s $srv -n $rule --start-ip-address $ip --end-ip-address $ip -o none 2>$null
try {
    $made = @(az postgres flexible-server firewall-rule list -g $rg -s $srv -o json 2>$null | ConvertFrom-Json | Where-Object { $_.name -eq $rule }).Count
    if ($made -ne 1) { 'could not create the firewall rule'; exit 98 }
    Start-Sleep 20
    $r = Sql $ca $ca.Db 'select current_user, current_database()'; Check 'control: A reaches its own database' ($r.Code -eq 0) $r.Out
    $r = Sql $cb $cb.Db 'select current_user, current_database()'; Check 'control: B reaches its own database' ($r.Code -eq 0) $r.Out

    $r = Sql $ca $cb.Db 'select 1'; Check 'A cannot connect to B''s database' ($r.Code -ne 0 -and $r.Out -match 'permission denied') $r.Out
    $r = Sql $cb $ca.Db 'select 1'; Check 'B cannot connect to A''s database' ($r.Code -ne 0 -and $r.Out -match 'permission denied') $r.Out
    $r = Sql $ca $ca.Db "set role `"$($cb.User)`""; Check 'A cannot assume B''s role' ($r.Code -ne 0 -and $r.Out -match 'permission denied') $r.Out
    $r = Sql $cb $cb.Db "set role `"$($ca.User)`""; Check 'B cannot assume A''s role' ($r.Code -ne 0 -and $r.Out -match 'permission denied') $r.Out

    # A row written to A must not appear in B. The marker row is removed afterwards.
    $marker = "isolation-$([guid]::NewGuid().ToString('N').Substring(0,12))"
    $r = Sql $ca $ca.Db "insert into `"Items`" (`"Name`", `"Quantity`", `"UpdatedOn`") values ('$marker', 1, now())"
    if ($r.Code -ne 0) { "note: insert failed: $($r.Out)"; $cols = (Sql $ca $ca.Db "select string_agg(column_name||':'||is_nullable, ',') from information_schema.columns where table_name='Items'").Out; "note: columns $cols" }
    Check 'a marker row writes to A' ($r.Code -eq 0)
    $seen = Sql $ca $ca.Db "select count(*) from `"Items`" where `"Name`"='$marker'"; Check 'A sees the marker' ($seen.Out.Trim() -eq '1') $seen.Out
    $seen = Sql $cb $cb.Db "select count(*) from `"Items`" where `"Name`"='$marker'"; Check 'B does not see the marker' ($seen.Code -eq 0 -and $seen.Out.Trim() -eq '0') $seen.Out
    $del = Sql $ca $ca.Db "delete from `"Items`" where `"Name`"='$marker'"; Check 'marker removed' ($del.Code -eq 0)

    # J-029's migration: the approver could not see this, because gh is denied to it.
    $idx = Sql $ca $ca.Db "select i.indisvalid, i.indisready from pg_index i join pg_class c on c.oid=i.indexrelid where c.relname='IX_Items_Quantity'"
    "index on A: IX_Items_Quantity valid|ready = '$($idx.Out)'"
} finally {
    az postgres flexible-server firewall-rule delete -g $rg -s $srv -n $rule --yes -o none 2>$null
    $left = @(az postgres flexible-server firewall-rule list -g $rg -s $srv -o json 2>$null | ConvertFrom-Json | Where-Object { $_.name -eq $rule }).Count
    Check 'firewall rule removed' ($left -eq 0)
}

foreach ($p in $PreviewA, $PreviewB) {
    $other = if ($p -eq $PreviewA) { $PreviewB } else { $PreviewA }
    $id = az identity show -g "rg-$Prefix-$p" -n "id-$Prefix-$p" -o json 2>$null | ConvertFrom-Json
    if (-not $id) { "note: no identity id-$Prefix-$p in rg-$Prefix-$p"; continue }
    $scopes = @(az role assignment list --assignee $id.principalId --all -o json 2>$null | ConvertFrom-Json | ForEach-Object { "$($_.roleDefinitionName)@$($_.scope -replace '^.*/providers/', '')" })
    $scopes | ForEach-Object { "  $p identity: $_" }
    $vaultWide = @($scopes | Where-Object { $_ -match 'Key Vault' -and $_ -match "vaults/$kv$" })
    $foreign = @($scopes | Where-Object { $_ -match "$Prefix-$other-" })
    Check "$p identity has no vault-wide secret role" ($vaultWide.Count -eq 0)
    Check "$p identity has no role on $other's secrets" ($foreign.Count -eq 0)
    $own = @($scopes | Where-Object { $_ -match "secrets/$Prefix-$p-" })
    Check "$p identity can read its own secret" ($own.Count -ge 1)
}
"failures: $script:fails"
exit $script:fails
