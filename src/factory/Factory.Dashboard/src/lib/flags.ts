/** What each flag reason means for the human, shown above the flag's own detail in the job modal (contract, "Blocked"). */
export const FLAG_MEANS: Record<string, string> = {
  escalated: 'the factory stopped short of the gate and needs your decision before this goes further.',
  'max-rounds': 'reviewers still asked for changes after the last build round.',
  'gate-failed': 'a gate command exited non-zero; gate.log has the output.',
  'merge-blocked': 'the merge was refused.',
  environment: 'the devops agent judged the environment or the factory at fault, which cannot be fixed by an agent. Fix the cause, then Retry deploy.',
  'deploy-fix-rounds': 'two deploy-fix rounds did not get the change deployed; the diagnosis is below. Rework it, or discard it.',
  'deploy-failed': 'the preview did not deploy in five attempts; deploy-<n>.log has the GitHub and HCP logs, and the preview is left up for you to look at.',
  'out-of-scope': 'the build changed the paths below, which match none of the Scope: In globs in the spec, so it stopped before review. Rework with a line "Scope +: <glob>, <glob>" to keep them (the spec is widened), or with a note asking the builder to remove them.',
  'spec-stale': 'a job from the same idea merged after this spec was written, and the builder found the spec no longer holds against main (its reasons are below). Rework with its proposed "Scope +: <glob>" line and AC wording, or discard.',
  'usage-limit': 'claude stopped on a usage limit (the subscription limit, or a Foundry quota), not on a decision, and the queue waits. Once the limit has reset, Resume continues the same claude session in the stage it stopped in; nothing is re-run.',
  'github-permission': "GitHub refused the deploy because the GitHub App lacks the permission named below. Add it in the App's settings and accept it on the installation, then run factory redeploy <id>; no deploy attempt was used.",
}
