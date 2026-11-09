# 09: Port deploy, HCP and canary

**Status:** not started. **Branch:** `feature/09-port-deploy`. **Depends on:** 06.

## Commands and scripts

`deploy` (the full "Deploy" section: workflow dispatch, `since` for front-end-only deploys, polling,
the sign-in smoke, `First-Error`), `deploy-log`, `deploy-fix`, `block`, `redeploy`, `destroy`,
`sweep`, `canary` / `canary -Full` / `canary-destroy`, `qa-login`. Behind them, `deploy.ps1`,
`hcp.ps1` and `canary.ps1` become `IHcpClient`, `IDeployService` and `ICanaryService`.

## Notes

- `gh` and `az` stay external processes, overridable as today (`FACTORY_GH_CMD`, `FACTORY_AZ_CMD`),
  so the stubs keep working. HCP is called over its API with the team token.
- The live lessons are encoded in the contract and must survive the port: the 600 s shell limit vs a
  25-minute apply, a destroy racing a rework's apply (J-022), Entra replication lag on the smoke,
  `az rest` hitting the home tenant, and HCP's auto-destroy firing once.
- `setup-azure.ps1` and `setup-platform.ps1` are **not** ported here; they become
  `factory init previews` in 16.

## Tests

`canary.Tests.ps1` and the deploy `Describe` blocks pass both ways. The live check (one preview from
the .NET implementation: a real job reaches `preview: up`, merges, and its destroy leaves no resource
group, checked in Azure) moved to 16, which runs it with previews on; until then deploys are off.

## Done when

The suites are green both ways.
