<#
.SYNOPSIS  Builds the frontend, publishes the app and the MCP companion to a named version folder and points the Start menu shortcut at it.
.EXAMPLE   pwsh -File tools/publish-local.ps1 -Name r08-routing [-RegisterMcp]
#>
param([Parameter(Mandatory)][string]$Name, [switch]$RegisterMcp)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $env:LOCALAPPDATA "Programs\AgenticDiagram\versions\$Name"
npm --prefix "$root\src\web" run build | Out-Null
dotnet publish "$root\src\Diagram.App" -c Release -o $dest | Out-Null
dotnet publish "$root\src\Diagram.Mcp" -c Release -o "$dest\mcp" | Out-Null
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Agentic Diagram.lnk'
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath = "$dest\Diagram.App.exe"; $s.WorkingDirectory = $dest; $s.Save()
if ($RegisterMcp) { claude mcp remove diagram -s user 2>$null; claude mcp add diagram --scope user -- "$dest\mcp\Diagram.Mcp.exe" }
Write-Output $dest
