# Run after LayoutComponentTests. Uses an independent invisible native Visio instance.
$ErrorActionPreference='Stop'
$evidence=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\r04-components'))
$source=Get-Content (Join-Path $evidence 'snapshot.json') -Raw | ConvertFrom-Json
$expected=@{}; foreach($element in $source.document.pages[0].elements){$expected[$element.id]=$element}
$visio=New-Object -ComObject Visio.Application
$visio.Visible=$false; $visio.AlertResponse=7
try {
 $document=$visio.Documents.Open((Join-Path $evidence 'components.vsdx'))
 $page=$document.Pages.Item(1)
 if($page.Shapes.Count -ne 4){throw 'Expected four native component groups'}
 $checks=@(foreach($group in $page.Shapes){
  if($group.Type -ne 2){throw 'Component is not an editable native group'}
  $metadata=$group.CellsU('User.AgentMetadata').ResultStr(0) | ConvertFrom-Json
  if(-not $metadata.'layout.template'){throw 'Missing component recipe'}
  foreach($shape in $group.Shapes){
   $id=$shape.CellsU('User.AgentId').ResultStr(0); $element=$expected[$id]
   if(-not $element -or $shape.Type -ne 3){throw 'Expected an editable named native part'}
   $width=$shape.CellsU('Width').ResultIU*72; $height=$shape.CellsU('Height').ResultIU*72
   if([Math]::Abs($width-$element.bounds.width) -gt .01 -or [Math]::Abs($height-$element.bounds.height) -gt .01){throw 'Native part dimensions changed'}
   if($element.kind -eq 'text'){
    if($shape.Text.TrimEnd("`r","`n") -ne $element.text.value){throw 'Native text differs'}
    $shape.AddNamedRow(242,'R04TextHeight',0) | Out-Null
    $shape.CellsU('User.R04TextHeight').FormulaU='TEXTHEIGHT(TheText,Width)'
    $needed=$shape.CellsU('User.R04TextHeight').ResultIU*72
    if($needed -gt $height+.1){throw "Native text does not fit: $($element.text.value); needs $needed, has $height"}
    [pscustomobject]@{Template=$metadata.'layout.template';Role=$element.metadata.'layout.role';RequiredHeightPt=$needed;AvailableHeightPt=$height;Fits=$true}
   }
  }
 })
 $page.Export((Join-Path $evidence 'visio.png'))
 [pscustomobject]@{VisioVersion=$visio.Version;GroupCount=4;MeasuredTextCount=$checks.Count;AllTextFits=$true;Text=$checks} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'visio-validation.json')
 $document.Saved=$true; $document.Close()
 Write-Output "Verified four editable native component groups and $($checks.Count) text boxes; all text fits."
} finally {$visio.Quit()}
