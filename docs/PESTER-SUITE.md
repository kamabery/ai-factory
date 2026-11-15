# The Pester suite

Pester was the factory's test suite, and then the oracle the .NET port was held to, until roadmap spec 12 retired the
PowerShell control plane. Each of its 387 cases is now covered by a .NET unit or integration test;
[roadmap/12-pester-mapping.md](roadmap/12-pester-mapping.md) names the test for each one, and
[TESTING.md](TESTING.md) is how the factory is tested now. Spec 20 moved the simulator to .NET (`Factory.Simulator`;
its `-Reset` case is `SimulatorTests`), and the tests' PowerShell fakes to Factory.TestHost's modes.

What is left in `factory/tests/*.Tests.ps1` tests the PowerShell still in the repo, until spec 16 replaces it with
`factory init previews` and this file goes:

| File | Script under test |
| --- | --- |
| `setup-azure.Tests.ps1` | `factory/scripts/setup-azure.ps1`, the one-time preview identities ([DEPLOY.md](DEPLOY.md)) |
| `setup-platform.Tests.ps1` | `factory/scripts/setup-platform.ps1`, the shared platform |

```powershell
dotnet build src/factory          # the stub host setup-platform.Tests.ps1 starts (Factory.TestHost)
Invoke-Pester factory/tests       # about 30 tests, all offline
```

`az` is replaced by `fixtures/az-setup-stub.ps1` and `fixtures/az-platform-stub.ps1` through `FACTORY_AZ_CMD` (which
these scripts, unlike the CLI, run as a script with `pwsh -File`), and GitHub and HCP Terraform by Factory.TestHost's
stub (`fixtures/github.ps1` starts it). Requires PowerShell 7 and Pester 6; in WSL, `setup.sh pester` and
`run.sh pester`.
