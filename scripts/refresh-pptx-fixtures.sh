#!/usr/bin/env bash
# Re-exports the fixture presentations through editide's API, reads each deck back, and updates the offline .pptx and
# .reads.json fixtures in src/Pintura.Tests/fixtures/pptx/. Exporting bills slides on your editide account.
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
fixtures_directory="${repository}/src/Pintura.Tests/fixtures/pptx"

if command -v dotnet >/dev/null; then
  dotnet_command="$(command -v dotnet)"
elif [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  dotnet_command="${HOME}/.dotnet/dotnet"
else
  echo "The .NET 10 SDK was not found on PATH or at ~/.dotnet/dotnet; see https://dot.net/download." >&2
  exit 1
fi

names=("$@")
if [[ "${#names[@]}" -eq 0 ]]; then
  names=(example default-theme balance-sheet board-update)
fi

"${dotnet_command}" build "${repository}/src/Pintura" --configuration Debug
pintura="${repository}/src/Pintura/bin/Debug/net10.0/Pintura"

scratch="$(mktemp -d)"
trap 'rm -rf "${scratch}"' EXIT

mkdir -p "${fixtures_directory}"

for name in "${names[@]}"; do
  source="${repository}/presentations/${name}"
  if [[ ! -d "${source}" ]]; then
    echo "No presentation folder at ${source}" >&2
    exit 1
  fi

  cache="${scratch}/cache-${name}"
  deck="${scratch}/${name}.pptx"
  imported="${scratch}/imported-${name}"

  echo "==> ${name}: exporting"
  PINTURA_CACHE="${cache}" "${pintura}" "${source}" --export "${deck}"

  echo "==> ${name}: reading back"
  PINTURA_CACHE="${cache}" "${pintura}" --import "${deck}" "${imported}"

  reads=("${cache}/reads/"*.json)
  if [[ "${#reads[@]}" -ne 1 || ! -f "${reads[0]}" ]]; then
    echo "Expected one cached read file under ${cache}/reads for ${name}" >&2
    exit 1
  fi

  cp "${deck}" "${fixtures_directory}/${name}.pptx"
  cp "${reads[0]}" "${fixtures_directory}/${name}.reads.json"
  echo "Updated ${fixtures_directory}/${name}.pptx and ${name}.reads.json"
done
