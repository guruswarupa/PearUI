# PearUI developer install: builds PearUI from source (needs the .NET 8 SDK) and installs it for this user.
# End users get PearUI from the installer on the GitHub Releases page instead (see installer\build-installer.ps1).
#
# Installs to %LOCALAPPDATA%\PearUI\app, adds a Start Menu shortcut, then runs installer\configure.ps1 for the
# system setup (macOS-style Terminal, Linux commands in Command Prompt, first-run settings, start at sign-in).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 [-NoLaunch] [-SkipTerminal] [-SkipLinuxTools]
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall

param(
    [switch]$Uninstall,
    [switch]$NoLaunch,
    [switch]$SkipTerminal,
    [switch]$SkipLinuxTools
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$project   = Join-Path $root 'src\PearUI\PearUI.csproj'
$configure = Join-Path $root 'installer\configure.ps1'
$installTo = Join-Path $env:LOCALAPPDATA 'PearUI\app'
$appExe    = Join-Path $installTo 'PearUI.exe'
$appDll    = Join-Path $installTo 'PearUI.dll'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'PearUI.lnk'
$dotnet    = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

$sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState

function Stop-PearUI {
    if (-not (Test-Path $appDll)) { return }
    if ((Test-Path $appExe) -and (-not $sac)) { Start-Process $appExe -ArgumentList '--quit' -Wait -WindowStyle Hidden }
    else { & $dotnet $appDll --quit | Out-Null }
    Start-Sleep -Seconds 3
}

if ($Uninstall) {
    Step 'Removing PearUI'
    & $configure -InstallDir $installTo -Uninstall
    Remove-Item $startMenu -ErrorAction SilentlyContinue
    Remove-Item (Split-Path $installTo) -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'PearUI removed.'
    return
}

Step 'Building PearUI (Release)'
Stop-PearUI
& $dotnet publish $project -c Release -o $installTo --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Step 'Creating the Start Menu shortcut'
$iconPath = Join-Path $installTo 'PearUI.ico'
if (-not (Test-Path $iconPath)) { Copy-Item (Join-Path $root 'src\PearUI\PearUI.ico') $iconPath -ErrorAction SilentlyContinue }
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
# Run PearUI.exe directly (Windows lists it as "PearUI" by "Guru Swarupa"); with Smart App Control on, which blocks
# unsigned programs, go through the signed .NET host instead.
if ((Test-Path $appExe) -and (-not $sac)) { $lnk.TargetPath = $appExe; $lnk.Arguments = '' }
else { $lnk.TargetPath = $dotnet; $lnk.Arguments = '"' + $appDll + '"' }
$lnk.WorkingDirectory = $installTo
$lnk.IconLocation = $iconPath
$lnk.WindowStyle = 7   # minimized: no console flash
$lnk.Description = 'macOS-style menu bar, Dock, search and Control Center'
$lnk.Save()

$cfg = @{ InstallDir = $installTo }
if ($SkipTerminal)    { $cfg.SkipTerminal = $true }
if ($SkipLinuxTools)  { $cfg.SkipLinuxTools = $true }
& $configure @cfg

Write-Host ''
Write-Host "Installed to $installTo" -ForegroundColor Green
Write-Host 'Start Menu shortcut: PearUI. Open a NEW terminal for the Linux commands.'

if (-not $NoLaunch) { Start-Process $startMenu }
