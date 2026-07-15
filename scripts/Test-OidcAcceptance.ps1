param(
    [string]$ApiBase = $env:AIMENTOR_OIDC_API_BASE,
    [ValidateRange(1, 300)][int]$HttpTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$tokenA = $env:AIMENTOR_OIDC_TOKEN_A
$tokenB = $env:AIMENTOR_OIDC_TOKEN_B
if ([string]::IsNullOrWhiteSpace($ApiBase) -or
    [string]::IsNullOrWhiteSpace($tokenA) -or
    [string]::IsNullOrWhiteSpace($tokenB)) {
    Write-Output 'OIDC_ACCEPTANCE_NOT_READY code=OIDC_ENV_MISSING'
    exit 2
}

function Get-TokenHash([string]$Token) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Token)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 12)
}

function Invoke-Stable([string]$Method, [string]$Uri, [string]$Token, [string]$Body = $null) {
    try {
        $parameters = @{
            Method = $Method
            Uri = $Uri
            Headers = @{ Authorization = "Bearer $Token" }
            TimeoutSec = $HttpTimeoutSeconds
        }
        if ($null -ne $Body) {
            $parameters.ContentType = 'application/json'
            $parameters.Body = $Body
        }
        $response = Invoke-WebRequest @parameters
        return @{ Status = [int]$response.StatusCode; Content = $response.Content }
    }
    catch {
        if ($null -ne $_.Exception.Response) {
            return @{ Status = [int]$_.Exception.Response.StatusCode; Content = '' }
        }
        Write-Output 'OIDC_ACCEPTANCE_NOT_READY code=OIDC_TRANSPORT_FAILED'
        exit 2
    }
}

$base = $ApiBase.TrimEnd('/')
$self = Invoke-Stable 'GET' "$base/api/v1/knowledge/stats" $tokenA
if ($self.Status -ne 200) {
    Write-Output "OIDC_ACCEPTANCE_FAILED code=TOKEN_A_REJECTED status=$($self.Status) tokenHash=$(Get-TokenHash $tokenA)"
    exit 1
}

$created = Invoke-Stable 'POST' "$base/api/v1/incidents/atlasid/runs" $tokenA '{"region":"oidc-acceptance"}'
if ($created.Status -ne 201) {
    Write-Output "OIDC_ACCEPTANCE_FAILED code=RUN_CREATE_FAILED status=$($created.Status) tokenHash=$(Get-TokenHash $tokenA)"
    exit 1
}
$run = $created.Content | ConvertFrom-Json
$runId = $run.runId
$version = [long]$run.version
$crossUser = Invoke-Stable 'GET' "$base/api/v1/incidents/atlasid/runs/$runId" $tokenB
$cleanupBody = @{ expectedVersion = $version } | ConvertTo-Json -Compress
$cleanup = Invoke-Stable 'POST' "$base/api/v1/incidents/atlasid/runs/$runId/cancel" $tokenA $cleanupBody
if ($cleanup.Status -ne 200) {
    # 验收不能在目标环境遗留活动运行；无法清理时保持 NotReady，交由运营人员确认状态。
    Write-Output "OIDC_ACCEPTANCE_NOT_READY code=OIDC_TEST_RUN_CLEANUP_FAILED status=$($cleanup.Status)"
    exit 2
}
if ($crossUser.Status -ne 403) {
    Write-Output "OIDC_ACCEPTANCE_FAILED code=CROSS_USER_NOT_FORBIDDEN status=$($crossUser.Status) tokenHash=$(Get-TokenHash $tokenB)"
    exit 1
}

Write-Output "OIDC_ACCEPTANCE_PASSED code=OIDC_TWO_SUBJECT_BOUNDARY tokenAHash=$(Get-TokenHash $tokenA) tokenBHash=$(Get-TokenHash $tokenB)"
exit 0
