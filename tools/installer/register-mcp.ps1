<#
.SYNOPSIS  Registers (or removes) the Agentic Diagram MCP companion with Claude Code or Codex.
.DESCRIPTION
  The registration names <install>\current\mcp\Diagram.Mcp.exe, which an update repoints, so you register once.
  -Client print only shows the commands and a JSON snippet for any other MCP client.
.EXAMPLE   powershell -File register-mcp.ps1 -Client claude
.EXAMPLE   powershell -File register-mcp.ps1 -Client codex -Remove
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('claude', 'codex', 'both', 'print')][string]$Client,
    [switch]$Remove,
    [string]$InstallRoot,
    [string]$Exe,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$L = Get-InstallLayout -InstallRoot $InstallRoot
if (-not $Exe) { $Exe = Join-Path $L.Current 'mcp\Diagram.Mcp.exe' }

function Invoke-Client([string]$Name, [string[]]$Arguments) {
    $line = "$Name $($Arguments -join ' ')"
    $script:ran = $false
    if ($DryRun) { Write-Output "would run: $line"; $script:ran = $true; return }
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { Write-Warning "$Name is not on PATH, so it was not registered. Run this yourself: $line"; return }
    & $Name @Arguments
    $script:ran = ($LASTEXITCODE -eq 0)
}

$clients = if ($Client -eq 'both') { 'claude', 'codex' } else { , $Client }
$done = @()
foreach ($c in $clients) {
    switch ($c) {
        'print' {
            Write-Output "claude mcp add diagram --scope user -- `"$Exe`""
            Write-Output "codex mcp add diagram -- `"$Exe`""
            Write-Output ('{ "mcpServers": { "diagram": { "command": ' + (ConvertTo-Json $Exe) + ' } } }')
        }
        'claude' {
            Invoke-Client 'claude' @('mcp', 'remove', 'diagram', '--scope', 'user')   # replacing an earlier registration; fine if there was none
            if (-not $Remove) { Invoke-Client 'claude' @('mcp', 'add', 'diagram', '--scope', 'user', '--', $Exe); if ($script:ran) { $done += 'claude' } }
        }
        'codex' {
            Invoke-Client 'codex' @('mcp', 'remove', 'diagram')
            if (-not $Remove) { Invoke-Client 'codex' @('mcp', 'add', 'diagram', '--', $Exe); if ($script:ran) { $done += 'codex' } }
        }
    }
}
if (-not $DryRun) {
    if ($Remove) { if (Test-Path $L.McpMarker) { Remove-Item $L.McpMarker -Force } }
    elseif ($done.Count -and (Test-Path $L.InstallRoot)) { Set-Content -LiteralPath $L.McpMarker -Value ($done -join ',') } # so uninstall knows what to undo
}
