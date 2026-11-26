# PearUI system setup / teardown. Used by the installer (PearUI-Setup.exe) and by install.ps1.
#
# Setup (default):
#   * first-run settings (only if this user has no PearUI settings yet)
#   * macOS-style Terminal: Windows Terminal with Command Prompt as the default, translucent "Pro" look,
#     Windows Terminal as the default terminal app
#   * Linux commands in Command Prompt (ls, grep, cat, touch, sed, awk, ...) from Git for Windows
#     (installed with winget if missing) + ll/la/pwd/clear aliases
# Teardown (-Uninstall): reverses all of the above and stops PearUI.

param(
    [Parameter(Mandatory)][string]$InstallDir,
    [switch]$Uninstall,
    [switch]$SkipTerminal,
    [switch]$SkipLinuxTools,
    [switch]$NoAutoStart
)

$ErrorActionPreference = 'Continue'
$appExe      = Join-Path $InstallDir 'PearUI.exe'
$appDll      = Join-Path $InstallDir 'PearUI.dll'
$settingsDir = Join-Path $env:APPDATA 'PearUI'
$aliasFile   = Join-Path $InstallDir 'aliases.doskey'
$stateKey    = 'HKCU:\Software\PearUI'
$cmdKey      = 'HKCU:\Software\Microsoft\Command Processor'

function Step($text) { Write-Host "==> $text" }

# PearUI.exe normally; the signed .NET host only when Smart App Control blocks unsigned programs
# (possible for framework-dependent developer builds only).
function Invoke-App([string[]]$AppArgs) {
    $sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
    if ((Test-Path $appExe) -and (-not $sac)) {
        Start-Process -FilePath $appExe -ArgumentList $AppArgs -Wait -WindowStyle Hidden
    }
    else {
        $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
        if ($dotnet -and (Test-Path $appDll)) { Start-Process -FilePath $dotnet -ArgumentList (@("`"$appDll`"") + $AppArgs) -Wait -WindowStyle Hidden }
    }
}

function Winget-Install($id) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Write-Warning "winget is not available; please install $id manually."; return }
    & winget install --id $id -e --silent --accept-package-agreements --accept-source-agreements | Out-Host
}

function Broadcast-EnvChange {
    Add-Type -Namespace PearUI -Name Env -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr h, uint m, System.IntPtr w, string l, uint f, uint t, out System.IntPtr r);
'@ -ErrorAction SilentlyContinue
    $r = [IntPtr]::Zero
    [void][PearUI.Env]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [IntPtr]::Zero, 'Environment', 2, 2000, [ref]$r)
}

function Find-GitUsrBin {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Git\usr\bin'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\usr\bin'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\usr\bin')
    )
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) { $candidates += (Join-Path (Split-Path (Split-Path $git.Source)) 'usr\bin') }
    $candidates | Where-Object { $_ -and (Test-Path (Join-Path $_ 'ls.exe')) } | Select-Object -First 1
}

function Get-UserPath { [Environment]::GetEnvironmentVariable('Path', 'User') }
function Set-UserPath($value) { [Environment]::SetEnvironmentVariable('Path', $value, 'User') }

# ============================================================================ teardown
if ($Uninstall) {
    Step 'Stopping PearUI'
    Invoke-App @('--quit')
    Start-Sleep -Seconds 2
    Invoke-App @('--restore-taskbar')   # in case PearUI ended abnormally while the taskbar was hidden

    Step 'Removing start-up entries'
    schtasks.exe /Delete /TN 'PearUI' /F 2>$null | Out-Null
    Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'PearUI.lnk') -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PearUI' -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize' -Name 'StartupDelayInMSec' -ErrorAction SilentlyContinue

    Step 'Removing Linux command setup'
    $state = Get-ItemProperty $stateKey -ErrorAction SilentlyContinue
    if ($state.AddedPath) {
        $parts = (Get-UserPath) -split ';' | Where-Object { $_ -and ($_ -ne $state.AddedPath) }
        Set-UserPath ($parts -join ';')
        Broadcast-EnvChange
    }
    $auto = (Get-ItemProperty $cmdKey -ErrorAction SilentlyContinue).AutoRun
    if ($auto -and $auto -like '*PearUI*') {
        $rest = ($auto -split ' & ' | Where-Object { $_ -notlike '*PearUI*' }) -join ' & '
        if ($rest) { Set-ItemProperty $cmdKey -Name AutoRun -Value $rest } else { Remove-ItemProperty $cmdKey -Name AutoRun }
    }

    Step 'Restoring Windows Terminal settings'
    Get-ChildItem "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal*\LocalState\settings.json.pearui-backup",
                  "$env:LOCALAPPDATA\Microsoft\Windows Terminal\settings.json.pearui-backup" -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item $_.FullName ($_.FullName -replace '\.pearui-backup$', '') -Force; Remove-Item $_.FullName }
    if (Test-Path 'HKCU:\Console\%%Startup') {
        Set-ItemProperty 'HKCU:\Console\%%Startup' -Name DelegationConsole  -Value '{00000000-0000-0000-0000-000000000000}'
        Set-ItemProperty 'HKCU:\Console\%%Startup' -Name DelegationTerminal -Value '{00000000-0000-0000-0000-000000000000}'
    }

    Remove-Item $stateKey -Recurse -ErrorAction SilentlyContinue
    Write-Host 'PearUI settings in %APPDATA%\PearUI were kept. Git for Windows, if installed, was left in place.'
    return
}

# ============================================================================ setup
New-Item $stateKey -Force | Out-Null

# First install on this account: the full PearUI experience (every item can be changed in PearUI Settings).
if (-not (Test-Path (Join-Path $settingsDir 'settings.json')) -and -not (Test-Path (Join-Path $env:APPDATA 'MacUI\settings.json'))) {
    Step 'Writing first-run settings'
    New-Item $settingsDir -ItemType Directory -Force | Out-Null
    @{ StartWithWindows = (-not $NoAutoStart); HideTaskbar = $true; ReplaceStartMenu = $true; DockAutoHide = $true } |
        ConvertTo-Json | Set-Content (Join-Path $settingsDir 'settings.json') -Encoding UTF8
}

if (-not $SkipTerminal) {
    Step 'Setting up the Terminal (Command Prompt, macOS look)'
    if (-not (Get-AppxPackage Microsoft.WindowsTerminal -ErrorAction SilentlyContinue)) {
        Write-Host '    Windows Terminal not found: installing it'
        Winget-Install 'Microsoft.WindowsTerminal'
    }
    Invoke-App @('--setup-terminal')
    $report = Join-Path $env:TEMP 'pearui-terminal-setup.txt'
    if (Test-Path $report) { Get-Content $report | ForEach-Object { Write-Host "    $_" } }
}

if (-not $SkipLinuxTools) {
    Step 'Adding Linux commands to Command Prompt'
    $usrBin = Find-GitUsrBin
    if (-not $usrBin) {
        Write-Host '    Git for Windows not found: installing it (provides ls, grep, cat, sed, awk, ...)'
        Winget-Install 'Git.Git'
        $usrBin = Find-GitUsrBin
    }
    if ($usrBin) {
        # Appended to the *user* PATH, so built-in Windows commands (find, sort, where, ...) keep priority.
        $parts = (Get-UserPath) -split ';' | Where-Object { $_ }
        if ($parts -notcontains $usrBin) {
            Set-UserPath ((@($parts) + $usrBin) -join ';')
            Set-ItemProperty $stateKey -Name AddedPath -Value $usrBin
            Broadcast-EnvChange
        }
        Write-Host "    Linux tools: $usrBin"

        @(
            'ls=ls --color=auto $*'
            'll=ls -la --color=auto $*'
            'la=ls -A --color=auto $*'
            'grep=grep --color=auto $*'
            'pwd=cd'
            'clear=cls'
        ) | Set-Content $aliasFile -Encoding ASCII
        $autoCmd = "doskey /macrofile=`"$aliasFile`""   # the path contains "PearUI": that's how uninstall finds it
        $auto = (Get-ItemProperty $cmdKey -ErrorAction SilentlyContinue).AutoRun
        if (-not (Test-Path $cmdKey)) { New-Item $cmdKey -Force | Out-Null }
        if (-not $auto) { Set-ItemProperty $cmdKey -Name AutoRun -Value $autoCmd }
        elseif ($auto -notlike '*PearUI*') { Set-ItemProperty $cmdKey -Name AutoRun -Value "$auto & $autoCmd" }
    }
    else { Write-Warning 'Could not find or install Git for Windows; Linux commands were not added.' }
}

Write-Host 'Setup finished.'
