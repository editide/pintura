#!/bin/sh
# Installs the latest Pintura release, which bundles its own runtime:
#   curl -fsSL https://raw.githubusercontent.com/editide/pintura/main/install.sh | sh
set -eu

release_url="${PINTURA_RELEASE_URL:-https://github.com/editide/pintura/releases/latest/download}"
application_directory="${PINTURA_INSTALL_DIR:-${HOME}/.local/lib/pintura}"
command_directory="${PINTURA_BIN_DIR:-${HOME}/.local/bin}"

case "$(uname -s)" in
  Linux) platform=linux ;;
  Darwin) platform=osx ;;
  *) echo "No Pintura release for $(uname -s); on Windows, use install.ps1." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64 | amd64) architecture=x64 ;;
  arm64 | aarch64) architecture=arm64 ;;
  *) echo "No Pintura release for $(uname -m) processors." >&2; exit 1 ;;
esac

# The directory is replaced wholesale, so refuse one that does not already hold Pintura.
if [ -e "${application_directory}" ] && [ ! -e "${application_directory}/pintura" ] \
  && [ ! -e "${application_directory}/Pintura" ]; then
  echo "${application_directory} exists and does not hold Pintura; set PINTURA_INSTALL_DIR elsewhere." >&2
  exit 1
fi

archive="pintura-${platform}-${architecture}.tar.gz"
temporary="$(mktemp -d)"
trap 'rm -rf "${temporary}"' EXIT
echo "Downloading ${release_url}/${archive}"
curl -fsSL "${release_url}/${archive}" -o "${temporary}/${archive}"
tar -xzf "${temporary}/${archive}" -C "${temporary}"
if [ "${platform}" = osx ]; then
  xattr -dr com.apple.quarantine "${temporary}/pintura" 2>/dev/null || true
fi

# The new build runs once where it was unpacked, so one that won't run on this machine leaves the installed one alone.
if ! "${temporary}/pintura/pintura" --version >/dev/null 2>&1; then
  echo "The downloaded Pintura doesn't run on this machine, so the installed one is left as it was." >&2
  exit 1
fi

mkdir -p "$(dirname "${application_directory}")" "${command_directory}"
rm -rf "${application_directory}"
mv "${temporary}/pintura" "${application_directory}"
ln -sfn "${application_directory}/pintura" "${command_directory}/pintura"
"${command_directory}/pintura" --help >/dev/null

echo
echo "Installed ${command_directory}/pintura"
case ":${PATH}:" in
  *":${command_directory}:"*) ;;
  *)
    echo
    echo "${command_directory} is not on PATH. Add this line to your shell configuration, then open a new shell:"
    echo "  export PATH=\"${command_directory}:\$PATH\""
    ;;
esac
