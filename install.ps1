# Installs the latest Pintura release, which bundles its own runtime:
#   irm https://raw.githubusercontent.com/editide/pintura/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$releaseUrl = if ($env:PINTURA_RELEASE_URL) { $env:PINTURA_RELEASE_URL }
  else { 'https://github.com/editide/pintura/releases/latest/download' }
$applicationDirectory = if ($env:PINTURA_INSTALL_DIR) { $env:PINTURA_INSTALL_DIR }
  else { Join-Path $env:LOCALAPPDATA 'Programs\pintura' }
$executable = Join-Path $applicationDirectory 'Pintura.exe'

# The directory is replaced wholesale, so refuse one that does not already hold Pintura.
if ((Test-Path $applicationDirectory) -and -not (Test-Path $executable)) {
  throw "$applicationDirectory exists and does not hold Pintura; set PINTURA_INSTALL_DIR elsewhere."
}

$archive = 'pintura-win-x64.zip'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())
New-Item -ItemType Directory $temporary | Out-Null
try {
  Write-Host "Downloading $releaseUrl/$archive"
  Invoke-WebRequest "$releaseUrl/$archive" -OutFile (Join-Path $temporary $archive) -UseBasicParsing
  Expand-Archive (Join-Path $temporary $archive) $temporary
  $unpacked = Join-Path $temporary 'pintura'
  Get-ChildItem -Recurse $unpacked | Unblock-File

  # The new build runs once where it was unpacked, so one that won't run on this machine leaves the installed one
  # alone.
  & (Join-Path $unpacked 'Pintura.exe') --version | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "The downloaded Pintura doesn't run on this machine, so the installed one is left as it was."
  }

  if (Test-Path $applicationDirectory) {
    Remove-Item -Recurse -Force $applicationDirectory
  }
  New-Item -ItemType Directory -Force (Split-Path $applicationDirectory) | Out-Null
  Move-Item $unpacked $applicationDirectory
}
finally {
  Remove-Item -Recurse -Force $temporary
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($userPath -split ';') -contains $applicationDirectory)) {
  $newPath = if ($userPath) { "$applicationDirectory;$userPath" } else { $applicationDirectory }
  [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
  $env:Path = "$applicationDirectory;$env:Path"
}
& $executable --help | Out-Null

Write-Host ''
Write-Host "Installed $executable and added its folder to PATH for new terminals."
