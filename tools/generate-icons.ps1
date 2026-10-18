param([switch]$Fetch)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendor=Join-Path $repoRoot 'vendor/lucide'
$commit='1ef60af944de207e0d5176905a0759ba12e6ee8d'
$version='1.59.0'
$names=@('git-branch','git-pull-request','git-merge','container','package','box','code-xml','server','cloud','database','shield-check','shield','lock','lock-keyhole','circle-check','check','network','workflow','file-code','folder-git-2','book-open','cpu','layers','arrow-down','chevron-down','globe')
New-Item -ItemType Directory -Path (Join-Path $vendor 'icons') -Force | Out-Null
if($Fetch){
 Invoke-WebRequest -Uri "https://raw.githubusercontent.com/lucide-icons/lucide/$commit/LICENSE" -OutFile (Join-Path $vendor 'LICENSE')
 foreach($name in $names){Invoke-WebRequest -Uri "https://raw.githubusercontent.com/lucide-icons/lucide/$commit/icons/$name.svg" -OutFile (Join-Path $vendor "icons/$name.svg")}
}
$inv=[Globalization.CultureInfo]::InvariantCulture
function N([double]$number){($number*100/24).ToString('0.#####',$inv)}
function Num($node,$key){[double]::Parse($node.GetAttribute($key),$inv)}
$icons=@()
foreach($name in $names){
 $file=Join-Path $vendor "icons/$name.svg"
 [xml]$svg=Get-Content -LiteralPath $file -Raw
 if($svg.DocumentElement.GetAttribute('viewBox') -ne '0 0 24 24'){throw "Unexpected viewBox in $name"}
 $parts=[Collections.Generic.List[string]]::new()
 foreach($node in $svg.DocumentElement.ChildNodes){
  if($node.NodeType -ne [Xml.XmlNodeType]::Element){continue}
  $geometry=switch($node.LocalName){
   'path' {[Windows.Media.Geometry]::Parse($node.GetAttribute('d'))}
   'circle' {[Windows.Media.EllipseGeometry]::new([Windows.Point]::new((Num $node cx),(Num $node cy)),(Num $node r),(Num $node r))}
   'ellipse' {[Windows.Media.EllipseGeometry]::new([Windows.Point]::new((Num $node cx),(Num $node cy)),(Num $node rx),(Num $node ry))}
   'rect' {
    $x=if($node.HasAttribute('x')){Num $node x}else{0}; $y=if($node.HasAttribute('y')){Num $node y}else{0}
    $rx=if($node.HasAttribute('rx')){Num $node rx}else{0}; $ry=if($node.HasAttribute('ry')){Num $node ry}else{$rx}
    [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new($x,$y,(Num $node width),(Num $node height)),$rx,$ry)
   }
   'line' {[Windows.Media.Geometry]::Parse(('M {0} {1} L {2} {3}' -f $node.x1,$node.y1,$node.x2,$node.y2))}
   {$_ -in 'polyline','polygon'} {[Windows.Media.Geometry]::Parse('M '+$node.GetAttribute('points')+$(if($_ -eq 'polygon'){' Z'}else{''}))}
   default {throw "Unsupported SVG primitive $($node.LocalName) in $name"}
  }
  $flat=$geometry.GetFlattenedPathGeometry(0.01,[Windows.Media.ToleranceType]::Absolute)
  foreach($figure in $flat.Figures){
   $parts.Add('M '+(N $figure.StartPoint.X)+' '+(N $figure.StartPoint.Y))
   foreach($segment in $figure.Segments){
    if($segment -is [Windows.Media.PolyLineSegment]){foreach($pt in $segment.Points){$parts.Add('L '+(N $pt.X)+' '+(N $pt.Y))}}
    elseif($segment -is [Windows.Media.LineSegment]){$parts.Add('L '+(N $segment.Point.X)+' '+(N $segment.Point.Y))}
    else{throw 'Path was not flattened'}
   }
   if($figure.IsClosed){$parts.Add('Z')}
  }
 }
 $tags=@($name.Split('-'))
 if($name -in @('folder-git-2','book-open')){$tags+=@('repository','repo','source')}
 if($name -eq 'container'){$tags+=@('docker','image','deployment','azure')}
 if($name -like 'git-*'){$tags+=@('repository','source','version','branch')}
 if($name -eq 'code-xml'){$tags+=@('application','development','code')}
 if($name -like 'shield*'){$tags+=@('security','approval','policy')}
 if($name -like 'lock*'){$tags+=@('security','access','private')}
 $path=$parts -join ' '
 # Hash canonical upstream LF bytes even in a CRLF Windows checkout.
 $sourceBytes=[Text.Encoding]::UTF8.GetBytes((Get-Content -LiteralPath $file -Raw).Replace("`r`n","`n"))
 $sourceHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
 $pathHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($path))).ToLowerInvariant()
 $icons+=[ordered]@{id="lucide:$name";name=($name -replace '-',' ');tags=@($tags|Select-Object -Unique);path=$path;sha256=$pathHash;sourceSha256=$sourceHash}
}
$catalog=[ordered]@{version="lucide-$version-normalized-1";sourceVersion=$version;sourceCommit=$commit;normalization='24-unit SVG flattened at 0.01 source units, scaled to a 100x100 canonical path; M/L/Z only';licence='ISC and MIT; see vendor/lucide/LICENSE';licenceText=(Get-Content -LiteralPath (Join-Path $vendor 'LICENSE') -Raw).Replace("`r`n","`n");icons=$icons}
$target=Join-Path $repoRoot 'contracts/icons/catalog.json'
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
$catalog|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $target -Encoding utf8
Write-Output "Generated $($icons.Count) pinned icons from Lucide $version ($commit)."
