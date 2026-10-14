#!/usr/bin/env bash
# Builds Pintura in place and links it onto PATH. A Debug build serves wwwroot from source, so only changes to the C#
# need a rebuild.
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
command_directory="${PINTURA_BIN_DIR:-${HOME}/.local/bin}"

if command -v dotnet >/dev/null; then
  dotnet_command="$(command -v dotnet)"
elif [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  dotnet_command="${HOME}/.dotnet/dotnet"
else
  echo "The .NET 10 SDK was not found on PATH or at ~/.dotnet/dotnet; see https://dot.net/download." >&2
  exit 1
fi

"${dotnet_command}" build "${repository}/src/Pintura" --configuration Debug
mkdir -p "${command_directory}"
ln -sfn "${repository}/src/Pintura/bin/Debug/net10.0/Pintura" "${command_directory}/pintura"
"${command_directory}/pintura" --help >/dev/null

echo
echo "Installed ${command_directory}/pintura"
if [[ ":${PATH}:" != *":${command_directory}:"* ]]; then
  echo "Add ${command_directory} to PATH to run pintura from any directory."
fi
