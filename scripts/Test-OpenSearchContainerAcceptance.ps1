param(
    [string]$Endpoint = 'http://127.0.0.1:9200',
    [string]$Image = 'opensearchproject/opensearch:3.5.0',
    [string]$ContainerName = 'aimentor-opensearch-acceptance',
    [ValidateRange(10, 1800)][int]$StartupTimeoutSeconds = 180,
    [ValidateRange(10, 3600)][int]$PullTimeoutSeconds = 600,
    [switch]$PullImage
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$startedContainer = $false
$createdIndexes = [System.Collections.Generic.List[string]]::new()
$suffix = "$(Get-Date -Format 'yyyyMMddHHmmss')-$PID"
$indexV1 = "aimentor-acceptance-v1-$suffix"
$indexV2 = "aimentor-acceptance-v2-$suffix"
$currentAlias = "aimentor-acceptance-current-$suffix"
$previousAlias = "aimentor-acceptance-previous-$suffix"
$manifestV1 = Join-Path ([System.IO.Path]::GetTempPath()) "aimentor-opensearch-v1-$suffix.json"
$manifestV2 = Join-Path ([System.IO.Path]::GetTempPath()) "aimentor-opensearch-v2-$suffix.json"
$base = $Endpoint.TrimEnd('/')
$http = @{ TimeoutSec = 10 }

function Write-NotReady([string]$Code) {
    Write-Output "OPENSEARCH_ACCEPTANCE_NOT_READY code=$Code"
    exit 2
}

function Require-Acceptance([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw $Code }
}

function Test-Endpoint {
    try {
        $root = Invoke-RestMethod $base @http
        return $null -ne $root.version.number
    }
    catch { return $false }
}

function Invoke-Docker([string[]]$Arguments) {
    # PowerShell 7 可把原生命令 stderr 提升为 ErrorRecord；先保留退出码，再统一转换为稳定错误码。
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & docker @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($exitCode -ne 0) { throw "DOCKER_COMMAND_FAILED:$($Arguments[0])" }
}

function Test-DockerCommand([string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & docker @Arguments *> $null
        return $LASTEXITCODE -eq 0
    }
    finally { $ErrorActionPreference = $previousPreference }
}

function Pull-ImageBounded {
    $stdout = Join-Path ([System.IO.Path]::GetTempPath()) "aimentor-opensearch-pull-$suffix.out"
    $stderr = Join-Path ([System.IO.Path]::GetTempPath()) "aimentor-opensearch-pull-$suffix.err"
    try {
        $process = Start-Process docker -ArgumentList @('pull', $Image) -NoNewWindow -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        if (-not $process.WaitForExit($PullTimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            Write-NotReady "IMAGE_PULL_TIMEOUT timeoutSeconds=$PullTimeoutSeconds"
        }
        if ($process.ExitCode -ne 0) {
            # Registry/代理错误正文可能包含内部地址或认证信息，验收输出只保留稳定状态码。
            Write-NotReady 'IMAGE_PULL_FAILED'
        }
    }
    finally {
        Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

function New-PhysicalIndex([string]$Index, [string]$DocumentId, [string]$Content) {
    $vector = [float[]]::new(256)
    $vector[0] = 1
    $mapping = @{
        settings = @{ index = @{ knn = $true } }
        mappings = @{
            dynamic = 'strict'
            properties = @{
                chunk_id = @{ type = 'keyword' }; document_id = @{ type = 'keyword' }
                version = @{ type = 'keyword' }; title = @{ type = 'text' }
                section = @{ type = 'text' }; content = @{ type = 'text' }
                tenant_id = @{ type = 'keyword' }; allowed_groups = @{ type = 'keyword' }
                source_path = @{ type = 'keyword'; index = $false }
                embedding = @{
                    type = 'knn_vector'; dimension = 256
                    method = @{ name = 'hnsw'; engine = 'lucene'; space_type = 'cosinesimil' }
                }
            }
        }
    } | ConvertTo-Json -Depth 12 -Compress
    Invoke-RestMethod "$base/$Index" -Method Put -ContentType 'application/json' -Body $mapping @http | Out-Null
    $createdIndexes.Add($Index)

    $metadata = @{ index = @{ _index = $Index; _id = "chunk-$DocumentId" } } | ConvertTo-Json -Compress
    $document = @{
        chunk_id = "chunk-$DocumentId"; document_id = $DocumentId; version = '1.0'
        title = "title-$DocumentId"; section = 'acceptance'; content = $Content
        tenant_id = 'tenant-acceptance'; allowed_groups = @('readers')
        source_path = 'acceptance.md'; embedding = $vector
    } | ConvertTo-Json -Depth 6 -Compress
    $bulk = "$metadata`n$document`n"
    $result = Invoke-RestMethod "$base/_bulk?refresh=true" -Method Post `
        -ContentType 'application/x-ndjson' -Body $bulk @http
    Require-Acceptance (-not $result.errors) 'OPENSEARCH_BULK_PARTIAL_FAILURE'
}

function Get-AliasTarget([string]$Alias) {
    $aliases = Invoke-RestMethod "$base/_alias/$Alias" @http
    return @($aliases.psobject.Properties.Name)
}

function Assert-AliasDocument([string]$Alias, [string]$DocumentId) {
    $body = @{ query = @{ term = @{ document_id = $DocumentId } } } | ConvertTo-Json -Depth 5 -Compress
    $result = Invoke-RestMethod "$base/$Alias/_search" -Method Post -ContentType 'application/json' -Body $body @http
    Require-Acceptance ($result.hits.total.value -eq 1) "OPENSEARCH_ALIAS_QUERY_FAILED:${Alias}:${DocumentId}"
}

try {
    if (-not (Test-Endpoint)) {
        if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) { Write-NotReady 'DOCKER_CLI_MISSING' }
        if (-not (Test-DockerCommand -Arguments @('info', '--format', '{{.ServerVersion}}'))) {
            Write-NotReady 'DOCKER_ENGINE_UNAVAILABLE'
        }
        if (-not (Test-DockerCommand -Arguments @('image', 'inspect', $Image))) {
            if (-not $PullImage) { Write-NotReady 'IMAGE_MISSING' }
            Pull-ImageBounded
        }
        Test-DockerCommand -Arguments @('rm', '-f', $ContainerName) | Out-Null
        Invoke-Docker -Arguments @('run', '--detach', '--rm', '--name', $ContainerName,
            '--publish', '9200:9200', '--publish', '9600:9600',
            '--env', 'discovery.type=single-node', '--env', 'DISABLE_SECURITY_PLUGIN=true',
            '--env', 'OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx512m', $Image) | Out-Null
        $startedContainer = $true
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        while (-not (Test-Endpoint) -and [DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2 }
        if (-not (Test-Endpoint)) {
            & docker logs --tail 80 $ContainerName 2>&1 | Write-Output
            Write-NotReady 'OPENSEARCH_STARTUP_TIMEOUT'
        }
    }

    $root = Invoke-RestMethod $base @http
    Require-Acceptance ($root.version.number -eq '3.5.0') "OPENSEARCH_VERSION_UNEXPECTED:$($root.version.number)"

    New-PhysicalIndex $indexV1 'DOC-V1' 'atlas acceptance version one'
    $countV1 = Invoke-RestMethod "$base/$indexV1/_count" @http
    $mappingV1 = Invoke-RestMethod "$base/$indexV1/_mapping" @http
    Require-Acceptance ($countV1.count -eq 1) 'OPENSEARCH_V1_COUNT_INVALID'
    Require-Acceptance ($mappingV1.$indexV1.mappings.properties.embedding.dimension -eq 256) `
        'OPENSEARCH_V1_MAPPING_INVALID'
    & (Join-Path $scriptRoot 'Publish-OpenSearchIndex.ps1') -Endpoint $base -PhysicalIndex $indexV1 `
        -ExpectedChunks 1 -ExpectedDimensions 256 -CurrentAlias $currentAlias -PreviousAlias $previousAlias `
        -ManifestPath $manifestV1 -HttpTimeoutSeconds 10
    Assert-AliasDocument $currentAlias 'DOC-V1'

    New-PhysicalIndex $indexV2 'DOC-V2' 'atlas acceptance version two'
    $countV2 = Invoke-RestMethod "$base/$indexV2/_count" @http
    $mappingV2 = Invoke-RestMethod "$base/$indexV2/_mapping" @http
    Require-Acceptance ($countV2.count -eq 1) 'OPENSEARCH_V2_COUNT_INVALID'
    Require-Acceptance ($mappingV2.$indexV2.mappings.properties.embedding.dimension -eq 256) `
        'OPENSEARCH_V2_MAPPING_INVALID'
    & (Join-Path $scriptRoot 'Publish-OpenSearchIndex.ps1') -Endpoint $base -PhysicalIndex $indexV2 `
        -ExpectedChunks 1 -ExpectedDimensions 256 -CurrentAlias $currentAlias -PreviousAlias $previousAlias `
        -ManifestPath $manifestV2 -HttpTimeoutSeconds 10
    $currentTargets = @(Get-AliasTarget $currentAlias)
    $previousTargets = @(Get-AliasTarget $previousAlias)
    Require-Acceptance ($currentTargets.Count -eq 1 -and $currentTargets[0] -eq $indexV2) `
        'OPENSEARCH_CURRENT_ALIAS_INVALID'
    Require-Acceptance ($previousTargets.Count -eq 1 -and $previousTargets[0] -eq $indexV1) `
        'OPENSEARCH_PREVIOUS_ALIAS_INVALID'
    Assert-AliasDocument $currentAlias 'DOC-V2'
    Assert-AliasDocument $previousAlias 'DOC-V1'

    & (Join-Path $scriptRoot 'Rollback-OpenSearchIndex.ps1') -Endpoint $base `
        -CurrentAlias $currentAlias -PreviousAlias $previousAlias -HttpTimeoutSeconds 10
    $currentTargets = @(Get-AliasTarget $currentAlias)
    $previousTargets = @(Get-AliasTarget $previousAlias)
    Require-Acceptance ($currentTargets.Count -eq 1 -and $currentTargets[0] -eq $indexV1) `
        'OPENSEARCH_ROLLBACK_CURRENT_INVALID'
    Require-Acceptance ($previousTargets.Count -eq 1 -and $previousTargets[0] -eq $indexV2) `
        'OPENSEARCH_ROLLBACK_PREVIOUS_INVALID'
    Assert-AliasDocument $currentAlias 'DOC-V1'

    Write-Output "OPENSEARCH_ACCEPTANCE_PASSED version=$($root.version.number) current=$indexV1 previous=$indexV2"
    exit 0
}
catch {
    Write-Output "OPENSEARCH_ACCEPTANCE_FAILED code=$($_.Exception.Message)"
    exit 1
}
finally {
    foreach ($index in $createdIndexes) {
        try { Invoke-RestMethod "$base/$index" -Method Delete @http | Out-Null } catch { }
    }
    Remove-Item -LiteralPath $manifestV1, $manifestV2 -Force -ErrorAction SilentlyContinue
    if ($startedContainer) { Test-DockerCommand -Arguments @('rm', '-f', $ContainerName) | Out-Null }
}
