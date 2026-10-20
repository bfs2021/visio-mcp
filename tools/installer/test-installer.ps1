<#
.SYNOPSIS  Exercises install, update, rollback guard, MCP registration commands and uninstall in a scratch folder and a scratch registry key.
.DESCRIPTION  Touches nothing of a real install: every path and registry key is redirected. Exit code 0 means all checks passed.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ("agentic-installer-test-" + [guid]::NewGuid().ToString('N'))
$regRoot = 'HKCU:\Software\AgenticDiagramInstallerTests'
$key = "$regRoot\Uninstall"
$assocRoot = 'Software\AgenticDiagramInstallerTests\Classes'
$failures = New-Object System.Collections.Generic.List[string]
function Check($ok, [string]$what) { if ([bool]$ok) { Write-Output "  ok   $what" } else { Write-Output "  FAIL $what"; $failures.Add($what) } }
function Throws([scriptblock]$body) { try { & $body | Out-Null; $false } catch { $true } }

# a pretend release: ping.exe stands in for the app so a "running app" can be started from the install folder
function New-Package([string]$version, [string]$notes) {
    $p = Join-Path $work "pkg-$version"
    New-Item -ItemType Directory -Force -Path "$p\app", "$p\mcp" | Out-Null
    Copy-Item "$env:SystemRoot\System32\PING.EXE" "$p\app\Diagram.App.exe"
    Copy-Item "$env:SystemRoot\System32\PING.EXE" "$p\mcp\Diagram.Mcp.exe"
    foreach ($f in 'lib.ps1', 'install.ps1', 'uninstall.ps1', 'register-mcp.ps1') { Copy-Item "$here\$f" $p }
    Set-Content "$p\version.txt" $version
    Set-Content "$p\RELEASE-NOTES.md" $notes
    $p
}
$notesText = @"
# Release notes

## 1.2.0 - later
- Adds the palette.

## 1.1.0
- Adds find and replace.

## 1.0.0
- First version.
"@

$opts = @{ InstallRoot = "$work\Programs\AgenticDiagram"; DataRoot = "$work\Data"; StartMenuDir = "$work\StartMenu"; UninstallKey = $key }
try {
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $v100 = New-Package '1.0.0' $notesText; $v110 = New-Package '1.1.0' $notesText; $v120 = New-Package '1.2.0' $notesText

    Write-Output 'fresh install'
    $out = & "$v100\install.ps1" @opts | Out-String
    Check ($out -match 'Installed Agentic Diagram 1.0.0') 'says it installed 1.0.0'
    Check (Test-Path "$($opts.InstallRoot)\versions\1.0.0\Diagram.App.exe") 'app copied to versions\1.0.0'
    Check (Test-Path "$($opts.InstallRoot)\versions\1.0.0\mcp\Diagram.Mcp.exe") 'MCP companion copied beside it'
    Check ((Get-Item "$($opts.InstallRoot)\current" -Force).Target -match '1\.0\.0') 'current points at 1.0.0'
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut("$work\StartMenu\Agentic Diagram.lnk")
    Check ($lnk.TargetPath -eq "$($opts.InstallRoot)\current\Diagram.App.exe") 'Start menu entry names current\ (survives updates)'
    $reg = Get-ItemProperty $key
    Check ($reg.DisplayVersion -eq '1.0.0' -and $reg.DisplayName -eq 'Agentic Diagram') 'listed in Add/Remove Programs with its version'
    Check ($reg.UninstallString -match 'uninstall\.ps1') 'uninstall command recorded'
    Check (Test-Path "$($opts.InstallRoot)\tools\uninstall.ps1") 'uninstall tool kept with the install'
    Check (-not $out.Contains('What changed since')) 'a first install does not claim to be an update'
    Check ($out -match 'First version' -and -not ($out -match 'Adds')) 'a first install shows only its own version''s notes'

    Write-Output 'user data'
    New-Item -ItemType Directory -Force -Path "$work\Data\library", "$work\Data\recovery" | Out-Null
    Set-Content "$work\Data\library\logo.png" 'picture'; Set-Content "$work\Data\workspace.json" '{}'

    Write-Output 'same version again'
    $again = & "$v100\install.ps1" @opts | Out-String
    Check ($again -match 'already installed') 'reinstalling the same version is a no-op without -Force'

    Write-Output 'update 1.0.0 -> 1.2.0'
    $upd = & "$v120\install.ps1" @opts | Out-String
    Check ($upd -match 'Updated Agentic Diagram from 1.0.0 to 1.2.0') 'says it updated'
    Check ((Get-Item "$($opts.InstallRoot)\current" -Force).Target -match '1\.2\.0') 'current now points at 1.2.0'
    Check ((Get-ItemProperty $key).DisplayVersion -eq '1.2.0') 'listed version updated'
    Check ($upd -match 'Adds the palette' -and $upd -match 'Adds find and replace') 'shows every change since 1.0.0'
    Check (-not ($upd -match 'First version')) 'does not repeat what the old version already had'
    Check ((Get-Content "$work\Data\library\logo.png") -eq 'picture' -and (Test-Path "$work\Data\workspace.json") -and (Test-Path "$work\Data\recovery")) 'library, recovery and settings untouched'
    Check (Test-Path "$($opts.InstallRoot)\versions\1.0.0") 'previous version kept for rollback'

    Write-Output 'older package'
    Check (Throws { & "$v110\install.ps1" @opts }) 'refuses to go back to 1.1.0 without -Force'
    Check ((Get-ItemProperty $key).DisplayVersion -eq '1.2.0') '... and changes nothing'
    & "$v110\install.ps1" @opts -Force | Out-Null
    Check ((Get-ItemProperty $key).DisplayVersion -eq '1.1.0') '-Force rolls back'
    Check (-not (Test-Path "$($opts.InstallRoot)\versions\1.0.0")) 'only the current and one other version are kept'
    & "$v120\install.ps1" @opts | Out-Null

    Write-Output 'running app'
    $proc = Start-Process "$($opts.InstallRoot)\versions\1.2.0\Diagram.App.exe" -ArgumentList '-n', '120', '127.0.0.1' -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Milliseconds 500
        Check (Throws { & "$v110\install.ps1" @opts -Force }) 'update refuses while the app is running'
        Check (Throws { & "$($opts.InstallRoot)\tools\uninstall.ps1" @opts -KeepData }) 'uninstall refuses while the app is running'
        Check ((Get-ItemProperty $key).DisplayVersion -eq '1.2.0') 'nothing changed'
    } finally { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; $proc.WaitForExit(5000) | Out-Null }

    Write-Output 'MCP registration commands'
    $dry = & "$($opts.InstallRoot)\tools\register-mcp.ps1" -Client both -InstallRoot $opts.InstallRoot -DryRun | Out-String
    Check ($dry -match [regex]::Escape("mcp add diagram --scope user -- $($opts.InstallRoot)\current\mcp\Diagram.Mcp.exe")) 'Claude Code command names current\mcp (stable across updates)'
    Check ($dry -match [regex]::Escape("codex mcp add diagram -- $($opts.InstallRoot)\current\mcp\Diagram.Mcp.exe")) 'Codex command'
    $print = & "$($opts.InstallRoot)\tools\register-mcp.ps1" -Client print -InstallRoot $opts.InstallRoot | Out-String
    Check ($print -match '"mcpServers"') 'print mode gives a JSON snippet for other clients'
    $withMcp = & "$v120\install.ps1" @opts -Force -RegisterMcp claude -DryRunMcp | Out-String
    Check ($withMcp -match 'would run: claude mcp add diagram') 'install -RegisterMcp registers during install'
    Set-Content "$($opts.InstallRoot)\mcp-registered.txt" 'claude'   # as a real registration would leave it

    Write-Output 'uninstall, keeping data'
    $rm = & "$($opts.InstallRoot)\tools\uninstall.ps1" @opts -KeepData -DryRunMcp | Out-String
    Check ($rm -match 'would run: claude mcp remove diagram') 'undoes the MCP registration it made'
    Check (-not (Test-Path $opts.InstallRoot)) 'program folder removed'
    Check (-not (Test-Path "$work\StartMenu\Agentic Diagram.lnk")) 'Start menu entry removed'
    Check (-not (Test-Path $key)) 'removed from Add/Remove Programs'
    Check ((Test-Path "$work\Data\library\logo.png") -and ($rm -match 'data was kept')) 'data kept and the folder is named'

    Write-Output 'uninstall, deleting data'
    & "$v100\install.ps1" @opts | Out-Null
    $del = & "$($opts.InstallRoot)\tools\uninstall.ps1" @opts -DeleteData | Out-String
    Check (-not (Test-Path "$work\Data") -and ($del -match 'data was deleted')) '-DeleteData removes the data folder'

    Write-Output 'uninstall with no one to ask'
    & "$v100\install.ps1" @opts | Out-Null
    New-Item -ItemType Directory -Force -Path "$work\Data" | Out-Null; Set-Content "$work\Data\keep.txt" 'x'
    $silent = "" | powershell.exe -NoProfile -File "$($opts.InstallRoot)\tools\uninstall.ps1" -InstallRoot $opts.InstallRoot -DataRoot $opts.DataRoot -StartMenuDir $opts.StartMenuDir -UninstallKey $key | Out-String
    Check ((Test-Path "$work\Data\keep.txt")) 'without a flag and without a console, data is kept'
    Check (-not (Test-Path $opts.InstallRoot)) '... and the program is still removed'
    Check ((Throws { & "$v100\uninstall.ps1" @opts -KeepData -DeleteData })) 'conflicting flags are refused'
    Check (((& "$v100\uninstall.ps1" @opts -KeepData | Out-String) -match 'not installed')) 'uninstalling when not installed says so'

    Write-Output 'bad package'
    $bad = Join-Path $work 'bad'; New-Item -ItemType Directory $bad | Out-Null; foreach ($f in 'lib.ps1', 'install.ps1') { Copy-Item "$here\$f" $bad }
    Check (Throws { & "$bad\install.ps1" @opts }) 'a folder that is not a release package is refused'
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $regRoot -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force "HKCU:\$assocRoot" -ErrorAction SilentlyContinue
}
Write-Output ''
if ($failures.Count) { Write-Output "$($failures.Count) check(s) failed."; exit 1 }
Write-Output 'All installer checks passed.'
