<#
.SYNOPSIS  Removes Agentic Diagram, and asks whether to keep or delete your data.
.DESCRIPTION
  Removes the program, Start menu entry, Open with entries and any MCP registration this installer made. Your data (picture library,
  recovery files, settings, window state) is in %LOCALAPPDATA%\AgenticDiagram: it is kept unless you say otherwise. Files you saved
  or exported (your .diagram.json and .vsdx files, and Documents\AgenticDiagram\Exports) are never touched.
.EXAMPLE   powershell -File uninstall.ps1 -KeepData
.EXAMPLE   powershell -File uninstall.ps1 -DeleteData
#>
[CmdletBinding()]
param(
    [switch]$KeepData, [switch]$DeleteData,
    [string]$InstallRoot, [string]$DataRoot, [string]$StartMenuDir, [string]$UninstallKey,
    [string]$FileAssociationRoot = 'Software\Classes',
    [switch]$Relaunched, [switch]$DryRunMcp
)
$ErrorActionPreference = 'Stop'
if ($KeepData -and $DeleteData) { throw 'Choose -KeepData or -DeleteData, not both.' }
. (Join-Path $PSScriptRoot 'lib.ps1')
$L = Get-InstallLayout -InstallRoot $InstallRoot -DataRoot $DataRoot -StartMenuDir $StartMenuDir -UninstallKey $UninstallKey

# This script lives inside the folder it deletes: run from a temporary copy instead.
if (-not $Relaunched -and $PSCommandPath.StartsWith([IO.Path]::GetFullPath($L.InstallRoot), [StringComparison]::OrdinalIgnoreCase)) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("agentic-uninstall-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    Copy-Item $PSCommandPath, (Join-Path $PSScriptRoot 'lib.ps1'), (Join-Path $PSScriptRoot 'register-mcp.ps1') $tmp
    $forward = @{}; foreach ($k in $PSBoundParameters.Keys) { $forward[$k] = $PSBoundParameters[$k] }
    $forward.Relaunched = $true
    try { & (Join-Path $tmp 'uninstall.ps1') @forward } finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
    return
}

if (-not (Test-Path $L.InstallRoot) -and -not (Test-Path $L.UninstallKey)) { Write-Output "$($script:AppName) is not installed."; return }
if (Test-AppRunning $L) { throw "$($script:AppName) is running. Close it and run the uninstall again." }

$removeData = $DeleteData.IsPresent
if (-not $KeepData -and -not $DeleteData -and (Test-Path $L.DataRoot)) {
    if ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
        $answer = Read-Host "Also delete your data (picture library, recovery files, settings) in $($L.DataRoot)? Files you saved are not affected. [y/N]"
        $removeData = $answer -match '^\s*y'
    } # no one to ask: keep it, because deleting is the one thing that cannot be undone
}

# MCP registrations this installer made
if (Test-Path $L.McpMarker) {
    $clients = (Get-Content $L.McpMarker -Raw).Trim().Split(',') | Where-Object { $_ }
    foreach ($c in $clients) {
        $args = @{ Client = $c; Remove = $true; InstallRoot = $L.InstallRoot }
        if ($DryRunMcp) { $args.DryRun = $true }
        try { & (Join-Path $PSScriptRoot 'register-mcp.ps1') @args } catch { Write-Warning "Could not remove the $c registration: $_" }
    }
}

# Open with entries (HKCU only), the Start menu entry, and the Add/Remove Programs entry
foreach ($ext in '.json', '.vsdx') { Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "HKCU:\$FileAssociationRoot\$ext\OpenWithList\Diagram.App.exe" }
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "HKCU:\$FileAssociationRoot\Applications\Diagram.App.exe"
if (Test-Path $L.Shortcut) { Remove-Item -Force $L.Shortcut }
if (Test-Path $L.UninstallKey) { Remove-Item -Recurse -Force $L.UninstallKey }

if (Test-Path $L.Current) { (Get-Item -LiteralPath $L.Current -Force).Delete() }
if (Test-Path $L.InstallRoot) { Remove-Item -Recurse -Force $L.InstallRoot }
if ($removeData -and (Test-Path $L.DataRoot)) { Remove-Item -Recurse -Force $L.DataRoot }

Write-Output "Removed $($script:AppName)."
if (Test-Path $L.DataRoot) { Write-Output "Your data was kept in $($L.DataRoot). Delete that folder if you no longer want it." } elseif ($removeData) { Write-Output 'Your data was deleted.' }
