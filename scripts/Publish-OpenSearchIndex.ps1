param(
    [Parameter(Mandatory = $true)][string]$Endpoint,
    [Parameter(Mandatory = $true)][string]$PhysicalIndex,
    [Parameter(Mandatory = $true)][int]$ExpectedChunks,
    [Parameter(Mandatory = $true)][int]$ExpectedDimensions,
    [string]$CurrentAlias = 'aimentor-knowledge-current',
    [string]$PreviousAlias = 'aimentor-knowledge-previous',
    [string]$ManifestPath = 'opensearch-index-manifest.json'
)
$ErrorActionPreference = 'Stop'
$base = $Endpoint.TrimEnd('/')
$count = Invoke-RestMethod "$base/$PhysicalIndex/_count"
if ($count.count -ne $ExpectedChunks) { throw 'OPENSEARCH_PUBLISH_COUNT_MISMATCH' }
$mapping = Invoke-RestMethod "$base/$PhysicalIndex/_mapping"
$dimensions = $mapping.$PhysicalIndex.mappings.properties.embedding.dimension
if ($dimensions -ne $ExpectedDimensions) { throw 'OPENSEARCH_PUBLISH_DIMENSION_MISMATCH' }
try { $aliases = Invoke-RestMethod "$base/_alias" } catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    $aliases = [pscustomobject]@{}
}
$actions = [System.Collections.Generic.List[object]]::new()
foreach ($property in $aliases.psobject.Properties) {
    if ($property.Value.aliases.$PreviousAlias) {
        $actions.Add(@{ remove = @{ index = $property.Name; alias = $PreviousAlias } })
    }
    if ($property.Value.aliases.$CurrentAlias) {
        $actions.Add(@{ remove = @{ index = $property.Name; alias = $CurrentAlias } })
        $actions.Add(@{ add = @{ index = $property.Name; alias = $PreviousAlias } })
    }
}
$actions.Add(@{ add = @{ index = $PhysicalIndex; alias = $CurrentAlias } })
# OpenSearch 在一次 _aliases 请求中原子提交全部 remove/add，失败时旧读路径保持不变。
Invoke-RestMethod "$base/_aliases" -Method Post -ContentType 'application/json' `
    -Body (@{ actions = $actions } | ConvertTo-Json -Depth 8) | Out-Null
@{
    schemaVersion = '1'; physicalIndex = $PhysicalIndex; expectedChunks = $ExpectedChunks
    vectorDimensions = $ExpectedDimensions; currentAlias = $CurrentAlias
    previousAlias = $PreviousAlias; publishedAt = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath $ManifestPath -Encoding utf8
