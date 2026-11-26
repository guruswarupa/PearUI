# Builds the PearUI installer: dist\PearUI-Setup-<version>.exe
#
# Needs: .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup). The GitHub release workflow runs this.
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.0.0]
#
# With no -Version, the newest git tag (v1.2.0 -> 1.2.0) is used, so the installer, the app's About page and the
# GitHub release always carry the same number.
#
# The app is published self-contained (users don't need .NET installed) and ReadyToRun-compiled for a fast start.

param([string]$Version)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root 'src\PearUI\PearUI.csproj'
$publish = Join-Path $root 'dist\publish'

if (-not $Version) { $Version = (git -C $root describe --tags --abbrev=0 --match 'v*' 2>$null) }
if (-not $Version) { throw 'No version: pass -Version 1.2.3 or create a git tag such as v1.2.3.' }
$Version = $Version.TrimStart('v')
Write-Host "Building PearUI $Version" -ForegroundColor Cyan

$iscc = @("$env:ProgramFiles\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup' }

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true `
    -p:Version=$Version -p:PearUIVersionLocked=true -p:DebugType=none -p:DebugSymbols=false -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The number inside the program must be the one on the release.
$built = (Get-Item (Join-Path $publish 'PearUI.exe')).VersionInfo.FileVersion
if (-not $built.StartsWith($Version.Split('-')[0])) { throw "Version mismatch: asked for $Version but PearUI.exe is $built." }

& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" (Join-Path $root 'installer\PearUI.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$out = Join-Path $root "dist\PearUI-Setup-$Version.exe"
Write-Host ''
Write-Host "Installer: $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)" -ForegroundColor Green
