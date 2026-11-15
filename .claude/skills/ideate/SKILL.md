---
name: ideate
description: Open a factory idea session (grill -> brief -> specs -> approve) in its own terminal window by launching `factory idea`. Use when the user says "ideate", "start an idea session", "kick off an idea", or names a GitHub target (owner/name) or I-NNN to grill.
argument-hint: "[<owner/name> | I-NNN]"
disable-model-invocation: true
---

# ideate

The idea session is an interactive `claude` session with the factory plugin loaded. It needs a real
terminal, so start it in a new window. Don't run it in this session's shell.

1. Take the argument as it was given: a GitHub target `owner/name`, an `I-NNN` to resume, or nothing (then the
   session asks for the target). Don't guess one.
2. Launch it from the repo root with the CLI's build, `artifacts/bin/Factory.Cli/debug/factory.dll`, in a new
   window for this OS. Each line below is one program and its arguments; add the argument last, only if there is one.
   - **Windows:** `cmd /c start "factory idea" /D <repo root> dotnet <repo root>\artifacts\bin\Factory.Cli\debug\factory.dll idea [<argument>]`
     (`start` takes the quoted window title first).
   - **Linux with a desktop:** `x-terminal-emulator -e dotnet <repo root>/artifacts/bin/Factory.Cli/debug/factory.dll idea [<argument>]`
     (`gnome-terminal -- dotnet ...` where `x-terminal-emulator` is missing).
   - **WSL or no desktop:** there is no window to open. Tell the human to run
     `dotnet artifacts/bin/Factory.Cli/debug/factory.dll idea [<argument>]` in a terminal of their own, and stop.
3. Report that the window was opened (the launcher's exit code is 0), and whether the target is new (a
   bootstrap job will wait in Backlog) or existing. If the launcher failed, say so with its output; don't retry.

Don't call `factory` yourself. The session in the new window records the idea and its specs.
