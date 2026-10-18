# Run after CatalogRoundTripTests. Opens an independent, invisible Visio instance.
$ErrorActionPreference = 'Stop'
$evidence = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\r03-icons'))
$visio = New-Object -ComObject Visio.Application
$visio.Visible = $false
$visio.AlertResponse = 7
try {
    $document = $visio.Documents.Open((Join-Path $evidence 'icon-gallery.vsdx'))
    $page = $document.Pages.Item(1)
    if ($page.Shapes.Count -ne 26) { throw 'Expected 26 editable icons' }
    $checks = @(foreach ($shape in $page.Shapes) {
        if ($shape.Type -ne 3) { throw 'Icon is not an editable native shape' }
        $metadata = $shape.CellsU('User.AgentMetadata').ResultStr(0) | ConvertFrom-Json
        $id = $metadata.'icon.id'
        if (-not $id.StartsWith('lucide:') -or -not $metadata.'icon.license') { throw 'Missing icon identity or licence' }
        $rows = $shape.RowCount(10)
        if ($rows -lt 2) { throw 'Missing native geometry' }
        if ([Math]::Abs($shape.CellsU('Width').ResultIU * 72 - 64) -gt 0.01) { throw 'Wrong icon width' }
        if ([Math]::Abs($shape.CellsU('LineWeight').ResultIU * 72 - 4) -gt 0.01) { throw 'Wrong stroke width' }
        if ($shape.CellsU('LineCap').ResultIU -ne 0) { throw 'Expected round line caps' }
        [pscustomobject]@{ IconId = $id; Editable = $true; GeometryRows = $rows; WidthPt = 64; StrokePt = 4 }
    })
    $page.Export((Join-Path $evidence 'visio-gallery.png'))
    $document.SaveAs((Join-Path $evidence 'visio-resaved-gallery.vsdx'))
    [pscustomobject]@{ VisioVersion = $visio.Version; ShapeCount = $page.Shapes.Count; Icons = $checks } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'visio-validation.json')
    $document.Saved = $true
    $document.Close()
    Write-Output 'Verified 26 editable native icons, geometry, dimensions, stroke and round caps in Visio.'
} finally { $visio.Quit() }
