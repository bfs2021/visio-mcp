# Shared by install.ps1, uninstall.ps1 and register-mcp.ps1. Dot-source it: . "$PSScriptRoot\lib.ps1"

$script:AppName = 'Agentic Diagram'
$script:DefaultUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgenticDiagram'

function Get-InstallLayout {
    <# Where everything lives. Each part can be pointed elsewhere so tests never touch the real install. #>
    param([string]$InstallRoot, [string]$DataRoot, [string]$StartMenuDir, [string]$UninstallKey)
    if (-not $InstallRoot)   { $InstallRoot   = Join-Path $env:LOCALAPPDATA 'Programs\AgenticDiagram' }
    if (-not $DataRoot)      { $DataRoot      = Join-Path $env:LOCALAPPDATA 'AgenticDiagram' }
    if (-not $StartMenuDir)  { $StartMenuDir  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs' }
    if (-not $UninstallKey)  { $UninstallKey  = $script:DefaultUninstallKey }
    [pscustomobject]@{
        InstallRoot = $InstallRoot
        Versions    = Join-Path $InstallRoot 'versions'
        Current     = Join-Path $InstallRoot 'current'      # a junction to the active version: shortcuts and MCP registrations name this, so an update never breaks them
        Tools       = Join-Path $InstallRoot 'tools'
        DataRoot    = $DataRoot                             # library, recovery, settings: never inside the install, so an update or reinstall cannot touch it
        Shortcut    = Join-Path $StartMenuDir "$($script:AppName).lnk"
        UninstallKey = $UninstallKey
        McpMarker   = Join-Path $InstallRoot 'mcp-registered.txt'
    }
}

function Get-InstalledVersion {
    param($Layout)
    if (-not (Test-Path -LiteralPath $Layout.UninstallKey)) { return $null }
    $v = (Get-ItemProperty -LiteralPath $Layout.UninstallKey -ErrorAction SilentlyContinue).DisplayVersion
    if ($v) { try { return [version]$v } catch { return $null } }
    $null
}

function Test-AppRunning {
    <# True when a program started from under the install folder is running. The update and the uninstall refuse to replace files in use. #>
    param($Layout)
    $prefix = [IO.Path]::GetFullPath($Layout.InstallRoot).TrimEnd('\') + '\'
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        $path = $null
        try { $path = $p.Path } catch { }
        if ($path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    $false
}

function Get-ReleaseNotes {
    <# Sections of a "## <version> - <date>" changelog that are newer than $Since and not newer than $Upto (only $Upto's when there is no $Since). #>
    param([string]$File, [version]$Since, [version]$Upto)
    if (-not (Test-Path -LiteralPath $File)) { return @() }
    $out = @(); $take = $false
    foreach ($line in Get-Content -LiteralPath $File) {
        if ($line -match '^##\s+v?(\d+(\.\d+){1,3})') {
            $v = [version]$Matches[1]
            $take = if ($Since) { ($v -le $Upto) -and ($v -gt $Since) } else { $v -eq $Upto } # a first install shows its own version's notes, not the whole history
        }
        if ($take) { $out += $line }
    }
    $out
}

function Set-Junction {
    param([string]$Link, [string]$Target)
    if (Test-Path -LiteralPath $Link) { (Get-Item -LiteralPath $Link -Force).Delete() } # removes the junction, never what it points at
    New-Item -ItemType Junction -Path $Link -Value $Target | Out-Null
}

function New-AppShortcut {
    param([string]$Path, [string]$Target, [string]$WorkingDirectory)
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($Path)
    $s.TargetPath = $Target; $s.WorkingDirectory = $WorkingDirectory; $s.Description = $script:AppName; $s.Save()
}
