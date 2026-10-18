# Recheck archived screenshot reproductions in an independent native Visio instance.
# Originals are copied without modification; no drawing is saved by Visio.
param([string]$SourceDirectory = 'C:/Users/claude/Documents/AgenticDiagram/Exports')
$ErrorActionPreference = 'Stop'
$archive = Join-Path $PSScriptRoot 'screenshot-files'
New-Item -ItemType Directory -Force $archive | Out-Null
$cases = @(
    @{ File='repository-deployment-light.vsdx'; Page='Repository deployment'; Text=@('Infrastructure repository','Application repository','Client Azure tenancy') },
    @{ File='compatibility-decision-light.vsdx'; Page='Compatibility decision'; Text=@('Illustrative compatibility decision','Application deployment permitted') },
    @{ File='infrastructure-request-infra-042-light.vsdx'; Page='Infrastructure request'; Text=@('Infrastructure Request','INFRA-042','Azure Service Bus queue','Dead-letter monitoring is enabled') },
    @{ File='infrastructure-release-flow-light.vsdx'; Page='Infrastructure release flow'; Text=@('Infrastructure release flow','Feature branch / Pull Request','infra-v1.4.0-rc.1','Deployment complete') },
    @{ File='application-release-flow-light.vsdx'; Page='Application release flow'; Text=@('Application release flow','Application feature branch','Infrastructure compatibility check','Record application deployment') }
)
function Read-Shapes($shapes) {
    foreach ($shape in $shapes) {
        $id = if ($shape.CellExistsU('User.AgentId', 0)) { $shape.CellsU('User.AgentId').ResultStr('') } else { '' }
        [pscustomobject]@{ Id=$id; Text=[string]$shape.Text; Type=[int]$shape.Type }
        if ($shape.Type -eq 2) { Read-Shapes $shape.Shapes }
    }
}
$visio = New-Object -ComObject Visio.Application
$visio.Visible = $false
$visio.AlertResponse = 7
try {
    $results = @(foreach ($case in $cases) {
        $source = Join-Path $SourceDirectory $case.File
        $copy = Join-Path $archive $case.File
        if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($copy)) {
            Copy-Item -LiteralPath $source -Destination $copy -Force
        }
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $document = $visio.Documents.OpenEx($copy, 2) # visOpenRO
        try {
            $page = @($document.Pages | Where-Object { $_.Name.StartsWith($case.Page) })
            if ($page.Count -ne 1) { throw "Expected one target page in $($case.File)" }
            $shapes = @(Read-Shapes $page[0].Shapes)
            $text = ($shapes.Text -join "`n")
            $checks = @($case.Text | ForEach-Object { [pscustomobject]@{ Text=$_; Found=$text.Contains($_) } })
            if (@($checks | Where-Object { !$_.Found }).Count) { throw "Missing expected text in $($case.File)" }
            $ids = @($shapes | Where-Object { $_.Id })
            $uniqueIds = @($ids.Id | Select-Object -Unique)
            if (!$shapes.Count -or $ids.Count -ne $shapes.Count -or $uniqueIds.Count -ne $ids.Count) { throw "Missing or duplicate stable IDs in $($case.File)" }
            if ($hash -ne (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) { throw 'Archive copy differs' }
            [pscustomobject]@{
                File=$case.File; Source=$source; SourceModifiedUtc=(Get-Item -LiteralPath $source).LastWriteTimeUtc.ToString('o')
                Sha256=$hash; Bytes=(Get-Item -LiteralPath $copy).Length; PageCount=$document.Pages.Count
                VerifiedPage=$page[0].Name; ShapeCount=$shapes.Count; NonemptyTextCount=@($shapes | Where-Object {$_.Text}).Count
                StableIdCount=$ids.Count; UniqueIds=$true; ExpectedText=$checks; Status='PASS'
            }
        } finally { $document.Saved=$true; $document.Close() }
    })
    [pscustomobject]@{ VerifiedUtc=[DateTime]::UtcNow.ToString('o'); VisioVersion=$visio.Version; Method='Native Visio read-only open, shape/text/UUID inspection; no edits or save, no manual UI or repair-prompt assertion'; Files=$results } |
        ConvertTo-Json -Depth 10 | Set-Content (Join-Path $PSScriptRoot 'screenshot-file-verification.json')
    $results | Select-Object File,ShapeCount,NonemptyTextCount,StableIdCount,Status | Format-Table
} finally { $visio.Quit() }
