param(
    [string]$Endpoint = 'http://127.0.0.1:9200',
    [string]$Image = 'opensearchproject/opensearch:3.5.0',
    [string]$ContainerName = 'aimentor-opensearch-acceptance',
    [string]$DockerCommand = 'docker',
    [ValidateRange(1, 300)][int]$DockerCommandTimeoutSeconds = 30,
    [ValidateRange(10, 1800)][int]$StartupTimeoutSeconds = 180,
    [ValidateRange(1, 3600)][int]$PullTimeoutSeconds = 600,
    [switch]$PullImage
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$scriptExitCode = 0
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

function Throw-NotReady([string]$Code) {
    throw "OPENSEARCH_ACCEPTANCE_NOT_READY:$Code"
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
    $result = Invoke-DockerProcess $Arguments $DockerCommandTimeoutSeconds
    if ($result.TimedOut) { throw "DOCKER_COMMAND_TIMEOUT:$($Arguments[0])" }
    if ($result.ExitCode -ne 0) { throw "DOCKER_COMMAND_FAILED:$($Arguments[0])" }
}

function Test-DockerCommand([string[]]$Arguments) {
    $result = Invoke-DockerProcess $Arguments $DockerCommandTimeoutSeconds
    return -not $result.TimedOut -and $result.ExitCode -eq 0
}

function Stop-ProcessTree([System.Diagnostics.Process]$Process) {
    if ($Process.HasExited) { return }

    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        # Docker Desktop 的 CLI 可能保留子进程；taskkill /T 可确保超时后不再后台拉取。
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & "$env:SystemRoot\System32\taskkill.exe" /PID $Process.Id /T /F *> $null
        }
        finally { $ErrorActionPreference = $previousPreference }
    }
    else {
        $Process.Kill($true)
    }

    if (-not $Process.WaitForExit(5000)) { throw 'DOCKER_PROCESS_TERMINATION_TIMEOUT' }
}

function Invoke-DockerProcess([string[]]$Arguments, [int]$TimeoutSeconds) {
    $process = $null
    $stdoutTask = $null
    $stderrTask = $null
    try {
        $safeArguments = @($Arguments | ForEach-Object {
            if ($_.Contains('"')) { throw 'DOCKER_ARGUMENT_INVALID' }
            if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
        })
        $processCommand = $DockerCommand
        $processArguments = $safeArguments
        if (($IsWindows -or $env:OS -eq 'Windows_NT') -and
            [System.IO.Path]::GetExtension($DockerCommand) -in @('.cmd', '.bat')) {
            # Windows PowerShell 对批处理文件返回的 Process 不提供 ExitCode，显式经 cmd.exe 执行。
            $commandLine = '"' + $DockerCommand + '" ' + ($safeArguments -join ' ')
            $processCommand = "$env:SystemRoot\System32\cmd.exe"
            $processArguments = @('/d', '/s', '/c', '"' + $commandLine + '"')
        }

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $processCommand
        $startInfo.Arguments = $processArguments -join ' '
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw 'DOCKER_PROCESS_START_FAILED' }
        # 同时抽干两个管道，避免 Docker 进度输出填满缓冲区后与父进程互相等待。
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTree $process
            $timedOut = $true
            $exitCode = -1
        }
        else {
            $timedOut = $false
            $exitCode = $process.ExitCode
        }
        if (-not $stdoutTask.Wait(5000) -or -not $stderrTask.Wait(5000)) {
            throw 'DOCKER_OUTPUT_DRAIN_TIMEOUT'
        }
        return [pscustomobject]@{ TimedOut = $timedOut; ExitCode = $exitCode }
    }
    finally {
        if ($null -ne $process) { $process.Dispose() }
        # 原生命令输出可能包含代理、认证或容器环境，只在内存中抽干且永不回显。
        $stdoutTask = $null
        $stderrTask = $null
    }
}

function Pull-ImageBounded {
    $result = Invoke-DockerProcess @('pull', $Image) $PullTimeoutSeconds
    if ($result.TimedOut) { Throw-NotReady "IMAGE_PULL_TIMEOUT timeoutSeconds=$PullTimeoutSeconds" }
    # Registry/代理错误正文可能包含内部地址或认证信息，验收输出只保留稳定状态码。
    if ($result.ExitCode -ne 0) { Throw-NotReady 'IMAGE_PULL_FAILED' }
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
        if ($null -eq (Get-Command $DockerCommand -ErrorAction SilentlyContinue)) { Throw-NotReady 'DOCKER_CLI_MISSING' }
        if (-not (Test-DockerCommand -Arguments @('info', '--format', '{{.ServerVersion}}'))) {
            Throw-NotReady 'DOCKER_ENGINE_UNAVAILABLE'
        }
        if (-not (Test-DockerCommand -Arguments @('image', 'inspect', $Image))) {
            if (-not $PullImage) { Throw-NotReady 'IMAGE_MISSING' }
            Pull-ImageBounded
        }
        Test-DockerCommand -Arguments @('rm', '-f', $ContainerName) | Out-Null
        # 在 run 前取得该唯一容器名的清理责任，覆盖 CLI 超时但引擎已完成创建的窗口。
        $startedContainer = $true
        Invoke-Docker -Arguments @('run', '--detach', '--rm', '--name', $ContainerName,
            '--publish', '9200:9200', '--publish', '9600:9600',
            '--env', 'discovery.type=single-node', '--env', 'DISABLE_SECURITY_PLUGIN=true',
            '--env', 'OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx512m', $Image) | Out-Null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        while (-not (Test-Endpoint) -and [DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2 }
        if (-not (Test-Endpoint)) {
            # 容器日志可能包含环境或插件细节，公开输出保持为稳定状态码。
            Throw-NotReady 'OPENSEARCH_STARTUP_TIMEOUT'
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
}
catch {
    if ($_.Exception.Message.StartsWith('OPENSEARCH_ACCEPTANCE_NOT_READY:', [StringComparison]::Ordinal)) {
        $code = $_.Exception.Message.Substring('OPENSEARCH_ACCEPTANCE_NOT_READY:'.Length)
        Write-Output "OPENSEARCH_ACCEPTANCE_NOT_READY code=$code"
        $scriptExitCode = 2
    }
    else {
        Write-Output "OPENSEARCH_ACCEPTANCE_FAILED code=$($_.Exception.Message)"
        $scriptExitCode = 1
    }
}
finally {
    foreach ($index in $createdIndexes) {
        try { Invoke-RestMethod "$base/$index" -Method Delete @http | Out-Null } catch { }
    }
    Remove-Item -LiteralPath $manifestV1, $manifestV2 -Force -ErrorAction SilentlyContinue
    if ($startedContainer) { Test-DockerCommand -Arguments @('rm', '-f', $ContainerName) | Out-Null }
}

exit $scriptExitCode
