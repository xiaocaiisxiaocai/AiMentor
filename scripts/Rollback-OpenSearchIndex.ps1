param(
    [Parameter(Mandatory = $true)][string]$Endpoint,
    [string]$CurrentAlias = 'aimentor-knowledge-current',
    [string]$PreviousAlias = 'aimentor-knowledge-previous',
    [ValidateRange(1, 300)][int]$HttpTimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$base = $Endpoint.TrimEnd('/')
$http = @{ TimeoutSec = $HttpTimeoutSeconds }
$aliases = Invoke-RestMethod "$base/_alias" @http
$current = $null
$previous = $null
foreach ($property in $aliases.psobject.Properties) {
    if ($property.Value.aliases.$CurrentAlias) { $current = $property.Name }
    if ($property.Value.aliases.$PreviousAlias) { $previous = $property.Name }
}
if (-not $current -or -not $previous) { throw 'OPENSEARCH_ROLLBACK_NOT_READY' }
$actions = @(
    @{ remove = @{ index = $current; alias = $CurrentAlias } }
    @{ remove = @{ index = $previous; alias = $PreviousAlias } }
    @{ add = @{ index = $previous; alias = $CurrentAlias } }
    @{ add = @{ index = $current; alias = $PreviousAlias } }
)
Invoke-RestMethod "$base/_aliases" -Method Post -ContentType 'application/json' @http `
    -Body (@{ actions = $actions } | ConvertTo-Json -Depth 8) | Out-Null
