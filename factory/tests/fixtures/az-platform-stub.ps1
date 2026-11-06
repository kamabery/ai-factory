# Stands in for az in setup-platform.ps1 (FACTORY_AZ_CMD). Logs each call to $env:FACTORY_TOOL_LOG.
# FACTORY_AZ_STUB=empty: nothing exists yet. =exists: everything does.
# FACTORY_AZ_STUB_SECRET_FAILS: how many `keyvault secret set` calls answer Forbidden first, as a role
# that has not reached the vault yet does; the count lives in the file it names.
$a = @($args)
[IO.File]::AppendAllText($env:FACTORY_TOOL_LOG, (([ordered]@{ args = $a } | ConvertTo-Json -Compress) + "`n"))
$line = $a -join ' '
$exists = $env:FACTORY_AZ_STUB -eq 'exists'
switch -Regex ($line) {
    '^account show' { 'sub-1'; break }
    '^resource list' { if ($exists) { '/subscriptions/sub-1/some/existing/id' }; break }
    '^ad signed-in-user show' { 'user-me'; break }
    '^ad sp list .*factory-hcp-terraform' { 'sp-hcp'; break }
    '^ad sp list .*factory-github-actions' { 'sp-gh'; break }
    '^role assignment list' { if ($exists) { 'role-existing' }; break }
    '^monitor log-analytics workspace show .*customerId' { 'law-customer-id'; break }
    '^monitor log-analytics workspace show' { '/subscriptions/sub-1/law'; break }
    '^monitor log-analytics workspace get-shared-keys' { 'law-key'; break }
    '^containerapp env show .*defaultDomain' { 'calm.centralus.azurecontainerapps.io'; break }
    '^containerapp env show' { '/subscriptions/sub-1/cae'; break }
    '^keyvault show' { '/subscriptions/sub-1/kv'; break }
    '^keyvault secret set' {
        $f = $env:FACTORY_AZ_STUB_SECRET_FAILS
        if ($f -and [int](Get-Content $f -Raw) -gt 0) {
            Set-Content $f ([int](Get-Content $f -Raw) - 1)
            [Console]::Error.WriteLine('ERROR: (Forbidden) Caller is not authorized')
            exit 1
        }
        break
    }
    '^postgres flexible-server show .*fullyQualifiedDomainName' { 'psql-x.postgres.database.azure.com'; break }
    '^postgres flexible-server show' { '/subscriptions/sub-1/psql'; break }
}
exit 0
