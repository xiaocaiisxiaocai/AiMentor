[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixtureDll = Join-Path $root "tests\AiMentor.OidcHttpFixture\bin\$Configuration\net10.0\AiMentor.OidcHttpFixture.dll"
$apiDll = Join-Path $root "src\AiMentor.Api\bin\$Configuration\net10.0\AiMentor.Api.dll"
$knowledgeRoot = Join-Path $root 'AI-Agent-V1合成数据包\knowledge'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('aimentor-oidc-' + [Guid]::NewGuid().ToString('N'))
$adminKey = 'fixture-' + [Guid]::NewGuid().ToString('N')
$audience = 'aimentor-api'
$httpClient = [Net.Http.HttpClient]::new()
$httpClient.Timeout = [TimeSpan]::FromSeconds(10)

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

function Start-DotNetProcess([string]$Dll, [hashtable]$Environment) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.Arguments = Quote-ProcessArgument $Dll
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($entry in $Environment.GetEnumerator()) {
        $start.EnvironmentVariables[$entry.Key] = [string]$entry.Value
    }
    $process = [Diagnostics.Process]::Start($start)
    return @{
        Process = $process
        Stdout = $process.StandardOutput.ReadToEndAsync()
        Stderr = $process.StandardError.ReadToEndAsync()
    }
}

function Stop-DotNetProcess($Handle) {
    if ($null -eq $Handle) { return }
    if (-not $Handle.Process.HasExited) {
        try { $Handle.Process.Kill() } catch { }
        $Handle.Process.WaitForExit(5000) | Out-Null
    }
    $Handle.Process.Dispose()
}

function Invoke-Http([string]$Method, [string]$Uri, [string]$Token = $null, [object]$Body = $null) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $Uri)
    try {
        if (-not [string]::IsNullOrWhiteSpace($Token)) {
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token)
        }
        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json -Depth 8 -Compress
            $request.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $httpClient.SendAsync($request).GetAwaiter().GetResult()
        try {
            return @{
                Status = [int]$response.StatusCode
                Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Wait-Ready([string]$Url, $Handle, [string]$FailureCode) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Handle.Process.HasExited) { throw [InvalidOperationException]::new($FailureCode) }
        try {
            if ((Invoke-Http 'GET' $Url).Status -eq 200) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    }
    throw [InvalidOperationException]::new($FailureCode)
}

function Issue-Token($Fixture, [hashtable]$Claims) {
    $response = Invoke-Http 'POST' "$Fixture/__fixture/tokens" $adminKey $Claims
    Require-Acceptance ($response.Status -eq 200) 'OIDC_TOKEN_ISSUE_FAILED'
    return ($response.Content | ConvertFrom-Json).access_token
}

try {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $root 'AiMentor.slnx') -c $Configuration --nologo
        Require-Acceptance ($LASTEXITCODE -eq 0) 'OIDC_ACCEPTANCE_BUILD_FAILED'
    }
    Require-Acceptance (Test-Path $fixtureDll) 'OIDC_FIXTURE_BINARY_MISSING'
    Require-Acceptance (Test-Path $apiDll) 'OIDC_API_BINARY_MISSING'
    $null = New-Item -ItemType Directory -Path $temporaryDirectory -Force

    $fixturePort = Get-FreeLoopbackPort
    $fixtureBase = "http://127.0.0.1:$fixturePort"
    $fixture = Start-DotNetProcess $fixtureDll @{
        ASPNETCORE_URLS = $fixtureBase
        AIMENTOR_OIDC_FIXTURE_ISSUER = $fixtureBase
        AIMENTOR_OIDC_FIXTURE_AUDIENCE = $audience
        AIMENTOR_OIDC_FIXTURE_ADMIN_KEY = $adminKey
        Logging__LogLevel__Default = 'None'
    }
    Wait-Ready "$fixtureBase/health" $fixture 'OIDC_FIXTURE_NOT_READY'

    $apiPort = Get-FreeLoopbackPort
    $apiBase = "http://127.0.0.1:$apiPort"
    $memoryKeyBytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($memoryKeyBytes) } finally { $random.Dispose() }
    $memoryKey = [Convert]::ToBase64String($memoryKeyBytes)
    $api = Start-DotNetProcess $apiDll @{
        ASPNETCORE_URLS = $apiBase
        ASPNETCORE_ENVIRONMENT = 'Development'
        Authentication__Mode = 'OidcJwt'
        Authentication__Authority = $fixtureBase
        Authentication__Audience = $audience
        Authentication__RequireHttpsMetadata = 'false'
        Authentication__RefreshIntervalSeconds = '1'
        Memory__EncryptionKey = $memoryKey
        Memory__StorePath = (Join-Path $temporaryDirectory 'memory.json')
        AIMENTOR_KNOWLEDGE_ROOT = $knowledgeRoot
        Logging__LogLevel__Default = 'None'
    }
    Wait-Ready "$apiBase/health/live" $api 'OIDC_API_NOT_READY'

    $userAOld = Issue-Token $fixtureBase @{ subject = 'oidc-user-a'; tenantId = 'tenant-a'; groups = @('tool-approvers') }
    $userANew = Issue-Token $fixtureBase @{ subject = 'oidc-user-a'; tenantId = 'tenant-a'; groups = @('readers') }
    $userB = Issue-Token $fixtureBase @{ subject = 'oidc-user-b'; tenantId = 'tenant-a'; groups = @('readers') }
    Require-Acceptance ((Invoke-Http 'GET' "$apiBase/api/v1/knowledge/stats" $userAOld).Status -eq 200) `
        'OIDC_VALID_TOKEN_REJECTED'

    $created = Invoke-Http 'POST' "$apiBase/api/v1/incidents/atlasid/runs" $userB @{ region = 'oidc-http-acceptance' }
    Require-Acceptance ($created.Status -eq 201) 'OIDC_SUBJECT_RUN_CREATE_FAILED'
    $runId = ($created.Content | ConvertFrom-Json).runId
    Require-Acceptance ((Invoke-Http 'GET' "$apiBase/api/v1/incidents/atlasid/runs/$runId" $userAOld).Status -eq 403) `
        'OIDC_CROSS_SUBJECT_NOT_FORBIDDEN'

    $wrongIssuer = Issue-Token $fixtureBase @{ subject = 'oidc-user-a'; tenantId = 'tenant-a'; issuer = 'https://wrong-issuer.example'; groups = @('readers') }
    $wrongAudience = Issue-Token $fixtureBase @{ subject = 'oidc-user-a'; tenantId = 'tenant-a'; audience = 'wrong-audience'; groups = @('readers') }
    $expired = Issue-Token $fixtureBase @{ subject = 'oidc-user-a'; tenantId = 'tenant-a'; expiresInSeconds = -120; groups = @('readers') }
    $duplicateSubject = Issue-Token $fixtureBase @{ subject = 'oidc-user-a'; duplicateSubject = 'oidc-user-b'; tenantId = 'tenant-a'; groups = @('readers') }
    foreach ($token in @($wrongIssuer, $wrongAudience, $expired, $duplicateSubject)) {
        Require-Acceptance ((Invoke-Http 'GET' "$apiBase/api/v1/knowledge/stats" $token).Status -eq 401) `
            'OIDC_INVALID_TOKEN_ACCEPTED'
    }

    $approval = Invoke-Http 'POST' "$apiBase/api/v1/tool-approvals" $userB @{
        toolName = 'memory.delete'
        arguments = @{ memoryId = 'fixture-memory'; expectedVersion = 1 }
        justification = 'OIDC group withdrawal acceptance'
    }
    Require-Acceptance ($approval.Status -eq 202) 'OIDC_APPROVAL_CREATE_FAILED'
    $approvalId = ($approval.Content | ConvertFrom-Json).id
    $decisionBody = @{ approved = $false; reason = 'OIDC group withdrawal acceptance' }
    Require-Acceptance ((Invoke-Http 'POST' "$apiBase/api/v1/tool-approvals/$approvalId/decision" $userANew $decisionBody).Status -eq 403) `
        'OIDC_REVOKED_GROUP_ACCEPTED'
    # 自包含旧 Token 在 TTL+ClockSkew 内仍携带旧组；本验收只承诺新签发 Token 反映撤权。
    Require-Acceptance ((Invoke-Http 'POST' "$apiBase/api/v1/tool-approvals/$approvalId/decision" $userAOld $decisionBody).Status -eq 200) `
        'OIDC_OLD_GROUP_TOKEN_UNEXPECTEDLY_REJECTED'

    $stateBefore = (Invoke-Http 'GET' "$fixtureBase/__fixture/state" $adminKey).Content | ConvertFrom-Json
    Require-Acceptance ($stateBefore.discoveryRequests -ge 1 -and $stateBefore.jwksRequests -ge 1) `
        'OIDC_DISCOVERY_NOT_OBSERVED'
    Require-Acceptance ((Invoke-Http 'POST' "$fixtureBase/__fixture/rotate" $adminKey @{}).Status -eq 200) `
        'OIDC_ROTATION_FAILED'
    $rotated = Issue-Token $fixtureBase @{ subject = 'oidc-user-a'; tenantId = 'tenant-a'; groups = @('readers') }
    $rotatedAccepted = $false
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $rotatedAccepted) {
        $rotatedAccepted = (Invoke-Http 'GET' "$apiBase/api/v1/knowledge/stats" $rotated).Status -eq 200
        if (-not $rotatedAccepted) { Start-Sleep -Milliseconds 250 }
    }
    Require-Acceptance $rotatedAccepted 'OIDC_ROTATED_KID_NOT_ACCEPTED'
    $stateAfter = (Invoke-Http 'GET' "$fixtureBase/__fixture/state" $adminKey).Content | ConvertFrom-Json
    Require-Acceptance ($stateAfter.jwksRequests -gt $stateBefore.jwksRequests) 'OIDC_JWKS_REFRESH_NOT_OBSERVED'
    Require-Acceptance ($stateAfter.discoveryRequests -gt $stateBefore.discoveryRequests) `
        'OIDC_DISCOVERY_REFRESH_NOT_OBSERVED'

    [ordered]@{
        status = 'Passed'
        protocol = 'OIDC discovery and JWKS over independent HTTP processes'
        subjects = 2
        issuerRejected = $true
        audienceRejected = $true
        expiryRejected = $true
        ambiguousSubjectRejected = $true
        revokedNewTokenForbidden = $true
        boundedOldTokenAccepted = $true
        rotatedKidAccepted = $true
        jwksRefreshObserved = $true
        discoveryRequests = $stateAfter.discoveryRequests
        jwksRequests = $stateAfter.jwksRequests
    } | ConvertTo-Json -Compress
    exit 0
}
catch {
    [ordered]@{
        status = 'Failed'
        code = if ($_.Exception.Message -match '^OIDC_[A-Z0-9_]+$') { $_.Exception.Message } else { 'OIDC_ACCEPTANCE_ERROR' }
        errorType = $_.Exception.GetType().Name
    } | ConvertTo-Json -Compress
    exit 1
}
finally {
    Stop-DotNetProcess $api
    Stop-DotNetProcess $fixture
    $httpClient.Dispose()
    if (Test-Path $temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
}
