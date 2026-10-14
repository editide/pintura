#!/usr/bin/env bash
# From WSL, publishes a self-contained Debug build for Windows and copies the themes and presentations beside it, for
# Windows apps that can't reach WSL. Debug skips the plan check; its recorded source root is a Linux path Windows can't
# open, so it serves the wwwroot it ships with, and page edits need a rerun.
# Usage: scripts/install-windows-from-wsl.sh [windows-folder], defaulting to %USERPROFILE%\dev.
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

if command -v dotnet >/dev/null; then
  dotnet_command="$(command -v dotnet)"
elif [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  dotnet_command="${HOME}/.dotnet/dotnet"
else
  echo "The .NET 10 SDK was not found on PATH or at ~/.dotnet/dotnet; see https://dot.net/download." >&2
  exit 1
fi

if [[ $# -gt 0 ]]; then
  target="$(wslpath -u "$1")"
else
  target="$(wslpath -u "$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')")/dev"
fi

# A running Pintura.exe, such as Claude Desktop's, locks its DLLs against the publish.
powershell.exe -NoProfile -Command "Get-Process Pintura -ErrorAction SilentlyContinue |
  Where-Object { \$_.Path -like '$(wslpath -w "${target}/pintura")\\*' } | Stop-Process -Force -PassThru |
  Wait-Process -Timeout 10 -ErrorAction SilentlyContinue; exit 0"

"${dotnet_command}" publish "${repository}/src/Pintura" --configuration Debug --runtime win-x64 --self-contained \
  --output "${target}/pintura"
mkdir -p "${target}/pintura-decks"
rm -rf "${target}/pintura-decks/themes" "${target}/pintura-decks/presentations"
cp -r "${repository}/themes" "${repository}/presentations" "${target}/pintura-decks/"
"${target}/pintura/Pintura.exe" --help >/dev/null

echo
echo "Installed $(wslpath -w "${target}/pintura")\\Pintura.exe, with the decks in $(wslpath -w "${target}/pintura-decks")"
echo "Add $(wslpath -w "${target}/pintura") to the Windows PATH to run pintura from any directory."
