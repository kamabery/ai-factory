#!/usr/bin/env bash
# The Linux toolchain for the factory's tests, installed in the user's home only (no sudo, nothing
# system-wide): the .NET 10 SDK (~/.dotnet10), pwsh (~/.pwsh), Pester 6, and a current git from
# conda-forge via micromamba (~/.gitenv; Ubuntu 20.04's git 2.25 lacks `git init -b`). Idempotent.
# Run from Windows: wsl.exe -d Ubuntu -- bash /mnt/d/code/ai-factory/factory/tests/linux/setup.sh
set -euo pipefail
cd ~

if ! ~/.dotnet10/dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -4 -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet10 > /tmp/dotnet-install.log 2>&1
fi

if [ ! -x ~/.pwsh/pwsh ]; then
  v=$(curl -4 -sSL https://api.github.com/repos/PowerShell/PowerShell/releases/latest | sed -nE 's/.*"tag_name": *"v([^"]+)".*/\1/p' | head -1)
  mkdir -p ~/.pwsh
  curl -4 -sSL "https://github.com/PowerShell/PowerShell/releases/download/v$v/powershell-$v-linux-x64.tar.gz" | tar -xz -C ~/.pwsh
  chmod +x ~/.pwsh/pwsh
fi

~/.pwsh/pwsh -NoProfile -Command '
  if (-not (Get-Module Pester -ListAvailable | Where-Object Version -ge 6.0.0)) {
    Install-Module Pester -MinimumVersion 6.0.0 -Scope CurrentUser -Force -AllowPrerelease -SkipPublisherCheck
  }' > /dev/null

if [ ! -x ~/.gitenv/bin/git ]; then
  mkdir -p ~/.local/bin
  if [ ! -x ~/.local/bin/micromamba ]; then
    curl -4 -sSL -o ~/.local/bin/micromamba https://github.com/mamba-org/micromamba-releases/releases/latest/download/micromamba-linux-64
    chmod +x ~/.local/bin/micromamba
  fi
  ~/.local/bin/micromamba create -y -q -p ~/.gitenv -c conda-forge git > /tmp/gitenv.log 2>&1 || { tail -20 /tmp/gitenv.log; exit 1; }
fi

echo "sdk:    $(~/.dotnet10/dotnet --list-sdks | tail -1)"
echo "pwsh:   $(~/.pwsh/pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"
echo "pester: $(~/.pwsh/pwsh -NoProfile -Command '(Get-Module Pester -ListAvailable | Sort-Object Version -Descending | Select-Object -First 1).Version.ToString()')"
echo "git:    $(~/.gitenv/bin/git --version)"
