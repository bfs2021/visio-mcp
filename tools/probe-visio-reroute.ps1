<#
.SYNOPSIS  Measures what Visio does to glued connectors' geometry when an attached shape is moved (R08c).
.PARAMETER Path         The .vsdx to open (a copy is opened, never the original).
.PARAMETER ShapeText    Text of the shape to move.
.PARAMETER ConnectorId  Shape IDs of connectors to report; default is every connector glued to the moved shape.
.PARAMETER Cell         Optional ShapeSheet cell to set on those connectors before the move, as name=value (e.g. ConFixedCode=1).
.PARAMETER Dx           Distance to move the shape to the right, in inches.
.PARAMETER Dy           Distance to move the shape up, in inches (Visio y is up).
.PARAMETER SaveReopen   Also save the moved document, reopen it and report the geometry again.
.OUTPUTS    One "before" and one "after" line per connector: ShapeSheet ConFixedCode and the Geometry1 vertices (local inches).
#>
param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ShapeText, [int[]]$ConnectorId = @(), [string[]]$Cell = @(), [double]$Dx = 0.2, [double]$Dy = 0, [switch]$SaveReopen)
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP "probe-$([guid]::NewGuid().ToString('N')).vsdx"
Copy-Item $Path $tmp
function Geo($s) { $n = $s.RowCount(10); $o = @(); for ($r = 0; $r -lt $n; $r++) { $o += '({0:N2},{1:N2})' -f $s.CellsSRC(10, $r, 0).ResultIU, $s.CellsSRC(10, $r, 1).ResultIU }; "rows=$n " + ($o -join ' ') }
function Report($p, $ids, $label) { foreach ($i in $ids) { $c = $p.Shapes.ItemFromID($i); "$i $label ConFixedCode=$($c.CellsU('ConFixedCode').ResultIU): $(Geo $c)" } }
$v = New-Object -ComObject Visio.Application; $v.Visible = $false
try {
  $d = $v.Documents.OpenEx($tmp, 0x4); $p = $d.Pages.Item(1)
  $shape = $p.Shapes | Where-Object { $_.Text -eq $ShapeText } | Select-Object -First 1
  if (-not $ConnectorId.Count) {
    $ConnectorId = @()
    foreach ($s in $p.Shapes) { if ($s.OneD) { foreach ($k in $s.Connects) { if ($k.ToSheet.ID -eq $shape.ID) { $ConnectorId += $s.ID; break } } } }
  }
  foreach ($i in $ConnectorId) { $c = $p.Shapes.ItemFromID($i); foreach ($kv in $Cell) { $k, $val = $kv.Split('='); $c.CellsU($k).FormulaU = $val } }
  Report $p $ConnectorId 'before'
  $shape.CellsU('PinX').ResultIU = $shape.CellsU('PinX').ResultIU + $Dx
  $shape.CellsU('PinY').ResultIU = $shape.CellsU('PinY').ResultIU + $Dy
  Report $p $ConnectorId 'after '
  if ($SaveReopen) {
    $out = Join-Path $env:TEMP "probe-saved-$([guid]::NewGuid().ToString('N')).vsdx"
    $d.SaveAs($out); $d.Close()
    $d = $v.Documents.OpenEx($out, 0x4); $p = $d.Pages.Item(1)
    Report $p $ConnectorId 'reopen'
    $d.Saved = $true; $d.Close(); Remove-Item $out -ErrorAction SilentlyContinue
  } else { $d.Saved = $true; $d.Close() }
} finally { $v.Quit(); Remove-Item $tmp -ErrorAction SilentlyContinue }
