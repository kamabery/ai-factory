#!/usr/bin/env bash
# Runs one of the factory's test suites on Linux, from a copy of this working tree in ~/factory-test
# (building in place on /mnt/... would share artifacts/ with the Windows runs). setup.sh first.
# It takes a suite name only, never a command, so an allow rule for it grants nothing else.
#   sync         copy the working tree and template-projects (beside it) into ~/factory-test
#   unit         sync, then the .NET unit tests
#   integration  sync, then the .NET integration tests
#   ported       sync, then Pester -Tag Ported (each block on PowerShell and on .NET)
#   dotnet       sync, then the whole Pester suite on .NET (-Impl dotnet)
#   diagnose [b] sync, then one block of factory.Tests.ps1 (default abandon) with detailed output, to see why it fails
#   versions     print the toolchain the suites run on
# Run from Windows: wsl.exe -d Ubuntu -- bash /mnt/d/code/ai-factory/factory/tests/linux/run.sh <suite>
set -euo pipefail

# A clean Linux PATH: WSL appends the Windows PATH, whose git.exe and pwsh.exe must not stand in for Linux's.
export DOTNET_ROOT="$HOME/.dotnet10"
export PATH="$HOME/.gitenv/bin:$HOME/.dotnet10:$HOME/.pwsh:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# .NET keeps named mutexes (the board lock) under XDG_RUNTIME_DIR and refuses one others can write. WSLg sets it
# to the world-writable /mnt/wslg/runtime-dir; a Linux desktop has a private /run/user/<uid>, so use one like it.
export XDG_RUNTIME_DIR="$HOME/.cache/xdg-runtime"
mkdir -p "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"

src="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
dest="$HOME/factory-test"

sync() {
  mkdir -p "$dest"
  rsync -a --delete --exclude artifacts --exclude node_modules --exclude .factory-runs --exclude TestResults \
    --exclude bin --exclude obj "$src/" "$dest/ai-factory/"
  if [ -d "$src/../template-projects" ]; then
    rsync -a --delete --exclude node_modules --exclude bin --exclude obj "$src/../template-projects/" "$dest/template-projects/"
  fi
}

case "${1:-}" in
  sync) sync ;;
  unit) sync; cd "$dest/ai-factory/src/factory"; dotnet test Factory.UnitTests -v q ;;
  integration) sync; cd "$dest/ai-factory/src/factory"; dotnet test Factory.IntegrationTests -v q ;;
  ported) sync; cd "$dest/ai-factory"; pwsh -NoProfile -File factory/tests/run-all.ps1 -Tag Ported ;;
  dotnet) sync; cd "$dest/ai-factory"; pwsh -NoProfile -File factory/tests/run-all.ps1 -Impl dotnet ;;
  diagnose)
    # A block of factory.Tests.ps1 by name (letters, digits, spaces and dashes only: a filter, never a command).
    block="${2:-abandon}"
    [[ "$block" =~ ^[A-Za-z0-9\ -]+$ ]] || { echo "diagnose: '$block' is not a block name" >&2; exit 2; }
    sync; cd "$dest/ai-factory"
    BLOCK="$block" pwsh -NoProfile -Command '$c = New-PesterConfiguration; $c.Run.Path = "factory/tests/factory.Tests.ps1"; $c.Filter.FullName = "factory.ps1.$env:BLOCK.*"; $c.Output.Verbosity = "Detailed"; Invoke-Pester -Configuration $c' ;;
  versions) git --version; dotnet --version; pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' ;;
  *) echo "usage: run.sh sync|unit|integration|ported|dotnet|diagnose|versions" >&2; exit 2 ;;
esac
