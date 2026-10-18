# Run after ReferenceDiagramTextFitTests. Requires installed Microsoft Visio.
$ErrorActionPreference = 'Stop'
$evidence = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\r02-text-fit'))
$visio = New-Object -ComObject Visio.Application
$visio.Visible = $false
$visio.AlertResponse = 7
try {
    $document = $visio.Documents.Open((Join-Path $evidence 'reference-text-fit.vsdx'))
    $page = $document.Pages.Item(2)
    $page.Export((Join-Path $evidence 'visio.png'))
    $texts = @($page.Shapes | ForEach-Object { $_.Text } | Where-Object { $_ })
    $arrows = @(foreach ($shape in @($page.Shapes | Where-Object { $_.Text -eq '↓↑' })) {
        $shape.AddNamedRow(242, 'TextFitVerification', 0) | Out-Null
        $shape.CellsU('User.TextFitVerification').FormulaU = 'TEXTWIDTH(TheText)'
        $needed = $shape.CellsU('User.TextFitVerification').ResultIU * 72
        $available = ($shape.CellsU('Width').ResultIU - $shape.CellsU('LeftMargin').ResultIU - $shape.CellsU('RightMargin').ResultIU) * 72
        if ($needed -gt $available) { throw 'Visio arrow text does not fit its box' }
        [pscustomobject]@{ RequiredWidthPt = $needed; AvailableWidthPt = $available; FontSizePt = $shape.CellsU('Char.Size').ResultIU * 72; Fits = $true }
    })
    if ($page.Shapes.Count -ne 62 -or $texts.Count -ne 39 -or $arrows.Count -ne 2) { throw 'Reference diagram structure changed unexpectedly' }
    $result = [pscustomobject]@{ ShapeCount = $page.Shapes.Count; TextCount = $texts.Count; Arrows = $arrows }
    $result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'visio-validation.json')
    # Probe cells were added only to the in-memory verification copy.
    $document.Saved = $true
    $document.Close()
    $result | ConvertTo-Json -Depth 6
} finally {
    $visio.Quit()
}
