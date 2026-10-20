<#
.SYNOPSIS  Installs or updates Agentic Diagram for the current user (no administrator needed).
.DESCRIPTION
  Run from an extracted release package (see package.ps1). Installing again with a newer package is the update: your library, recovery
  files and settings live outside the install folder and are never touched, and what changed since the installed version is shown.
  The previous version is kept so a bad update can be rolled back by running that package's install.ps1 -Force.
.EXAMPLE   powershell -File install.ps1
.EXAMPLE   powershell -File install.ps1 -OpenWith -RegisterMcp claude
#>
[CmdletBinding()]
param(
    [switch]$OpenWith,                                   # list the app under Open with for .json and .vsdx (needs a build with W15)
    [ValidateSet('none', 'claude', 'codex', 'both')][string]$RegisterMcp = 'none',
    [switch]$Force,                                      # reinstall the same version, or go back to an older one
    [string]$InstallRoot, [string]$DataRoot, [string]$StartMenuDir, [string]$UninstallKey,
    [switch]$DryRunMcp                                   # tests: show the MCP commands instead of running them
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$L = Get-InstallLayout -InstallRoot $InstallRoot -DataRoot $DataRoot -StartMenuDir $StartMenuDir -UninstallKey $UninstallKey

$versionFile = Join-Path $PSScriptRoot 'version.txt'
if (-not (Test-Path $versionFile) -or -not (Test-Path (Join-Path $PSScriptRoot 'app\Diagram.App.exe'))) { throw 'This is not a release package: app\Diagram.App.exe or version.txt is missing. Build one with tools/installer/package.ps1.' }
$new = [version](Get-Content $versionFile -Raw).Trim()
$old = Get-InstalledVersion $L

if ($old -and $old -eq $new -and -not $Force) { Write-Output "$($script:AppName) $new is already installed. Use -Force to reinstall it."; return }
if ($old -and $old -gt $new -and -not $Force) { throw "Version $old is installed and this package is $new. Use -Force to go back to the older version." }
if (Test-AppRunning $L) { throw "$($script:AppName) is running. Close it (and any agent sessions using its tools) and run this again." }

$dest = Join-Path $L.Versions "$new"
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Recurse -Force (Join-Path $PSScriptRoot 'app\*') $dest
if (Test-Path (Join-Path $PSScriptRoot 'mcp')) { Copy-Item -Recurse -Force (Join-Path $PSScriptRoot 'mcp') (Join-Path $dest 'mcp') }

# the tools that uninstall and register the MCP companion stay with the install, so they work without the package
New-Item -ItemType Directory -Force -Path $L.Tools | Out-Null
foreach ($f in 'lib.ps1', 'uninstall.ps1', 'register-mcp.ps1') { Copy-Item -Force (Join-Path $PSScriptRoot $f) $L.Tools }
if (Test-Path (Join-Path $PSScriptRoot 'RELEASE-NOTES.md')) { Copy-Item -Force (Join-Path $PSScriptRoot 'RELEASE-NOTES.md') $L.InstallRoot }

Set-Junction -Link $L.Current -Target $dest
New-AppShortcut -Path $L.Shortcut -Target (Join-Path $L.Current 'Diagram.App.exe') -WorkingDirectory $L.Current

New-Item -Force -Path $L.UninstallKey | Out-Null
$uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $L.Tools 'uninstall.ps1')`""
if ($InstallRoot) { $uninstall += " -InstallRoot `"$InstallRoot`"" }
$props = @{
    DisplayName = $script:AppName; DisplayVersion = "$new"; Publisher = 'Agentic Diagram'; InstallLocation = $L.InstallRoot
    UninstallString = $uninstall; NoModify = 1; NoRepair = 1; DisplayIcon = (Join-Path $L.Current 'Diagram.App.exe')
}
foreach ($k in $props.Keys) { Set-ItemProperty -LiteralPath $L.UninstallKey -Name $k -Value $props[$k] }

if ($OpenWith) { & (Join-Path $L.Current 'Diagram.App.exe') --register-file-types }

# keep this version and the one before it; older folders are only disk space
Get-ChildItem $L.Versions -Directory | Where-Object { $_.Name -ne "$new" } |
    Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending | Select-Object -Skip 1 | Remove-Item -Recurse -Force

if ($RegisterMcp -ne 'none') {
    $mcpArgs = @{ Client = $RegisterMcp; InstallRoot = $L.InstallRoot }
    if ($DryRunMcp) { $mcpArgs.DryRun = $true }
    & (Join-Path $L.Tools 'register-mcp.ps1') @mcpArgs
}

if ($old) { Write-Output "Updated $($script:AppName) from $old to $new." } else { Write-Output "Installed $($script:AppName) $new." }
Write-Output "Your library, recovery files and settings are in $($L.DataRoot) and were not changed."
$notes = @(Get-ReleaseNotes -File (Join-Path $L.InstallRoot 'RELEASE-NOTES.md') -Since $old -Upto $new)
if ($notes.Count) { Write-Output ''; Write-Output ($(if ($old) { "What changed since ${old}:" } else { 'Release notes:' })); $notes | ForEach-Object { Write-Output $_ } }
