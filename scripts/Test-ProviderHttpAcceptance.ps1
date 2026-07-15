[CmdletBinding()]
param(
    [ValidateSet('All', 'Success', 'PermanentError')]
    [string]$Mode = 'All',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$OutputDirectory,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\provider-http-acceptance'
}
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$fixtureDll = Join-Path $root "tests\AiMentor.ProviderHttpFixture\bin\$Configuration\net10.0\AiMentor.ProviderHttpFixture.dll"
$evaluationDll = Join-Path $root "src\AiMentor.Evaluation\bin\$Configuration\net10.0\AiMentor.Evaluation.dll"
$evaluationFile = Join-Path $root 'AI-Agent-V1合成数据包\evaluation\evaluation-critical-v2.jsonl'
$knowledgeRoot = Join-Path $root 'AI-Agent-V1合成数据包\knowledge'
$suiteFile = Join-Path $root 'AI-Agent-V1合成数据包\evaluation\evaluation-suite-v2.json'
$apiKey = 'fixture-' + [Guid]::NewGuid().ToString('N')

function Quote-ProcessArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Require-Acceptance([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw [InvalidOperationException]::new($Code) }
}

function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally { $listener.Stop() }
}

function Start-ProviderFixture([string]$FixtureMode, [bool]$RateLimitOnce) {
    $port = Get-FreeLoopbackPort
    $baseUrl = "http://127.0.0.1:$port"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.Arguments = Quote-ProcessArgument $fixtureDll
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['ASPNETCORE_URLS'] = $baseUrl
    $start.EnvironmentVariables['AIMENTOR_PROVIDER_FIXTURE_API_KEY'] = $apiKey
    $start.EnvironmentVariables['AIMENTOR_PROVIDER_FIXTURE_MODE'] = $FixtureMode
    $start.EnvironmentVariables['AIMENTOR_PROVIDER_FIXTURE_429_ONCE'] = $RateLimitOnce.ToString().ToLowerInvariant()
    $process = [Diagnostics.Process]::Start($start)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw [InvalidOperationException]::new('PROVIDER_FIXTURE_EARLY_EXIT') }
        try {
            $health = Invoke-RestMethod "$baseUrl/health" -TimeoutSec 1
            if ($health.status -eq 'Ready') {
                return @{ Process = $process; BaseUrl = $baseUrl }
            }
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    try { $process.Kill() } catch { }
    throw [InvalidOperationException]::new('PROVIDER_FIXTURE_NOT_READY')
}

function Stop-ProviderFixture($Fixture) {
    if ($null -ne $Fixture -and -not $Fixture.Process.HasExited) {
        try { $Fixture.Process.Kill() } catch { }
        $Fixture.Process.WaitForExit(5000) | Out-Null
    }
    if ($null -ne $Fixture) { $Fixture.Process.Dispose() }
}

function Invoke-ProviderComparison($Fixture, [string]$ReportPath) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $arguments = @($evaluationDll, $evaluationFile, $knowledgeRoot, $suiteFile,
        '--compare', '--output', $ReportPath) | ForEach-Object { Quote-ProcessArgument $_ }
    $start.Arguments = $arguments -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $endpoint = $Fixture.BaseUrl
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_ALLOW_INSECURE_LOOPBACK'] = 'true'
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_MODEL_PROVIDER'] = 'OpenAI'
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_MODEL_ENDPOINT'] = $endpoint
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_MODEL_API_KEY'] = $apiKey
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_MODEL_ID'] = 'fixture-chat-v1'
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_EMBEDDING_PROVIDER'] = 'OpenAI'
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_EMBEDDING_ENDPOINT'] = $endpoint
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_EMBEDDING_API_KEY'] = $apiKey
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_EMBEDDING_ID'] = 'fixture-embedding-v1'
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_EMBEDDING_DIMENSIONS'] = '256'
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_EMBEDDING_INDEX_VERSION'] = 'fixture-index-v1'
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_RERANKER_PROVIDER'] = 'HttpSemantic'
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_RERANKER_ENDPOINT'] = $endpoint
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_RERANKER_API_KEY'] = $apiKey
    $start.EnvironmentVariables['AIMENTOR_EVALUATION_RERANKER_ID'] = 'fixture-reranker-v1'
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout.GetAwaiter().GetResult() | Out-Null
    $stderr.GetAwaiter().GetResult() | Out-Null
    $exitCode = $process.ExitCode
    $process.Dispose()
    Require-Acceptance (Test-Path $ReportPath) 'PROVIDER_REPORT_MISSING'
    return $exitCode
}

function Get-FixtureState($Fixture) {
    return Invoke-RestMethod "$($Fixture.BaseUrl)/__fixture/state" -Headers @{ Authorization = "Bearer $apiKey" }
}

function Assert-SafeReport([string]$ReportPath) {
    $raw = Get-Content -Raw $ReportPath
    $forbidden = @($apiKey, 'alice.fixture@example.test', '13800138000', 'sk_live_1234567890abcdef',
        '11010519491231002X', 'INDIRECT-INJECTION-CANARY-20260714')
    foreach ($value in $forbidden) {
        Require-Acceptance ($raw.IndexOf($value, [StringComparison]::Ordinal) -lt 0) 'PROVIDER_REPORT_SENSITIVE_DATA'
    }
    return $raw | ConvertFrom-Json
}

try {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $root 'AiMentor.slnx') -c $Configuration --nologo
        Require-Acceptance ($LASTEXITCODE -eq 0) 'PROVIDER_ACCEPTANCE_BUILD_FAILED'
    }
    Require-Acceptance (Test-Path $fixtureDll) 'PROVIDER_FIXTURE_BINARY_MISSING'
    Require-Acceptance (Test-Path $evaluationDll) 'PROVIDER_EVALUATION_BINARY_MISSING'

    $summary = [ordered]@{ status = 'Passed'; protocol = 'OpenAI-compatible HTTP fixture'; success = $null; failure = $null }
    if ($Mode -in @('All', 'Success')) {
        $fixture = $null
        try {
            $fixture = Start-ProviderFixture 'Success' $true
            $path = Join-Path $OutputDirectory 'provider-http-success.json'
            $exitCode = Invoke-ProviderComparison $fixture $path
            $report = Assert-SafeReport $path
            $state = Get-FixtureState $fixture
            Require-Acceptance ($exitCode -eq 0 -and $report.Passed) 'PROVIDER_SUCCESS_COMPARISON_FAILED'
            Require-Acceptance ($report.Candidate.Aggregate.Passed -eq 16) 'PROVIDER_SUCCESS_CASE_COUNT_INVALID'
            Require-Acceptance ($state.rateLimited.chat -eq 1) 'PROVIDER_CHAT_429_RETRY_NOT_OBSERVED'
            Require-Acceptance ($state.rateLimited.embeddings -eq 1) 'PROVIDER_EMBEDDING_429_RETRY_NOT_OBSERVED'
            Require-Acceptance ($state.rateLimited.reranker -eq 1) 'PROVIDER_RERANKER_429_RETRY_NOT_OBSERVED'
            Require-Acceptance ($state.sensitivePayloads -eq 0) 'PROVIDER_NETWORK_SENSITIVE_DATA'
            $summary.success = [ordered]@{
                candidatePassed = $report.Candidate.Aggregate.Passed
                candidateNotReady = $report.Candidate.Aggregate.NotReady
                rateLimitedRequests = 3
                report = $path
            }
        }
        finally { Stop-ProviderFixture $fixture }
    }

    if ($Mode -in @('All', 'PermanentError')) {
        $fixture = $null
        try {
            $fixture = Start-ProviderFixture 'PermanentError' $false
            $path = Join-Path $OutputDirectory 'provider-http-not-ready.json'
            $exitCode = Invoke-ProviderComparison $fixture $path
            $report = Assert-SafeReport $path
            $state = Get-FixtureState $fixture
            Require-Acceptance ($exitCode -eq 2 -and -not $report.Passed) 'PROVIDER_ERROR_EXIT_CODE_INVALID'
            Require-Acceptance ($report.Candidate.Aggregate.NotReady -eq 16) 'PROVIDER_ERROR_NOT_READY_COUNT_INVALID'
            Require-Acceptance ($report.Candidate.Aggregate.Errors -eq 16) 'PROVIDER_ERROR_COUNT_INVALID'
            Require-Acceptance ($state.requests.embeddings -eq 48) 'PROVIDER_ERROR_RETRY_BUDGET_INVALID'
            Require-Acceptance ($null -eq $state.requests.chat -and $null -eq $state.requests.reranker) `
                'PROVIDER_ERROR_FALLBACK_OBSERVED'
            Require-Acceptance ($state.sensitivePayloads -eq 0) 'PROVIDER_NETWORK_SENSITIVE_DATA'
            $summary.failure = [ordered]@{
                candidateNotReady = $report.Candidate.Aggregate.NotReady
                candidateErrors = $report.Candidate.Aggregate.Errors
                embeddingRequests = $state.requests.embeddings
                report = $path
            }
        }
        finally { Stop-ProviderFixture $fixture }
    }

    $summary | ConvertTo-Json -Depth 5
    exit 0
}
catch {
    # 只输出稳定错误码和异常类型，子进程输出、路径、请求内容与凭据都不进入失败报告。
    [ordered]@{
        status = 'Failed'
        code = if ($_.Exception.Message -match '^PROVIDER_[A-Z0-9_]+$') { $_.Exception.Message } else { 'PROVIDER_ACCEPTANCE_ERROR' }
        errorType = $_.Exception.GetType().Name
    } | ConvertTo-Json
    exit 1
}
