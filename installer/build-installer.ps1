# Builds the PearUI installer: dist\PearUI-Setup-<version>.exe
#
# Needs: .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup). The GitHub release workflow runs this.
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.0.0]
#
# The app is published self-contained (users don't need .NET installed) and ReadyToRun-compiled for a fast start.

param([string]$Version)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root 'src\MacUI\MacUI.csproj'
$publish = Join-Path $root 'dist\publish'

if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$Version = $Version.TrimStart('v')
Write-Host "Building PearUI $Version" -ForegroundColor Cyan

$iscc = @("$env:ProgramFiles\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup' }

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true `
    -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" (Join-Path $root 'installer\PearUI.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$out = Join-Path $root "dist\PearUI-Setup-$Version.exe"
Write-Host ''
Write-Host "Installer: $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)" -ForegroundColor Green
