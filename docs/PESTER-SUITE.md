# The Pester suite

Pester was the factory's test suite, and then the oracle the .NET port was held to. Spec 12 retired it for the control
plane ([roadmap/12-pester-mapping.md](roadmap/12-pester-mapping.md) names the test for each case), and spec 16 retired
the last of it, the preview setup scripts' Pester, when they became Terraform
([roadmap/16-pester-mapping.md](roadmap/16-pester-mapping.md)). The repo has no PowerShell; how it is tested now is
[TESTING.md](TESTING.md).
