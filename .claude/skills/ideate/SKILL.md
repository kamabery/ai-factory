---
name: ideate
description: Open a factory idea session (grill -> brief -> specs -> approve) in its own terminal window by launching factory/scripts/idea.ps1. Use when the user says "ideate", "start an idea session", "kick off an idea", or names a GitHub target (owner/name) or I-NNN to grill.
argument-hint: "[<owner/name> | I-NNN]"
disable-model-invocation: true
---

# ideate

The idea session is an interactive `claude` session with the factory plugin loaded. It needs a real
terminal, so start it in a new window. Don't run it in this session's shell.

1. Take the argument as it was given: a GitHub target `owner/name`, an `I-NNN` to resume, or nothing (then the
   session asks for the target). Don't guess one.
2. Launch it:

   ```powershell
   $a = @('-NoExit', '-NoProfile', '-File', "$PWD\factory\scripts\idea.ps1")
   if ('$ARGUMENTS') { $a += '$ARGUMENTS' }
   $p = Start-Process pwsh -ArgumentList $a -WorkingDirectory $PWD -PassThru
   Start-Sleep 3; "pid=$($p.Id) running=$(-not $p.HasExited)"
   ```

3. Report the pid, and whether the target is new (a bootstrap job will wait in Backlog) or
   existing. If `running=False`, say so; don't retry.

Don't call `factory.ps1` yourself. The session in the new window records the idea and its specs.
