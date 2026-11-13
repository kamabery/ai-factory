# The Pester suite

Pester was the factory's test suite, and then the oracle the .NET port was held to, until roadmap spec 12 retired the
PowerShell control plane. Each of its 387 cases is now covered by a .NET unit or integration test;
[roadmap/12-pester-mapping.md](roadmap/12-pester-mapping.md) names the test for each one, and
[TESTING.md](TESTING.md) is how the factory is tested now.

What is left in `factory/tests/*.Tests.ps1` tests the PowerShell still in the repo:

| File | Script under test | Until |
| --- | --- | --- |
| `setup-azure.Tests.ps1` | `factory/scripts/setup-azure.ps1`, the one-time preview identities ([DEPLOY.md](DEPLOY.md)) | spec 16, `factory init previews` |
| `setup-platform.Tests.ps1` | `factory/scripts/setup-platform.ps1`, the shared platform | spec 16 |
| `simulate.Tests.ps1` | `factory/scripts/simulate.ps1`, the simulator the Playwright suite runs | the simulator moves to .NET |

```powershell
dotnet build src/factory          # the stub host setup-platform.Tests.ps1 starts (Factory.TestHost)
Invoke-Pester factory/tests       # about 30 tests, all offline
```

`az` is replaced by `fixtures/az-setup-stub.ps1` and `fixtures/az-platform-stub.ps1` through `FACTORY_AZ_CMD`, and
GitHub and HCP Terraform by Factory.TestHost's stub (`fixtures/github.ps1` starts it). Requires PowerShell 7 and Pester 6.
