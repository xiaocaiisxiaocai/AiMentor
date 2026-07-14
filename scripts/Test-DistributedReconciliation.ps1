[CmdletBinding()]
param(
    [string]$SqlContainer = 'aimentor-sqlserver',
    [int]$SqlHostPort = 1433,
    [int]$BasePort = 5510,
    [switch]$UseEphemeralSqlServer
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$apiDll = Join-Path $repositoryRoot 'src\AiMentor.Api\bin\Debug\net10.0\AiMentor.Api.dll'
$ownsSqlContainer = $false
if ($UseEphemeralSqlServer) {
    $SqlContainer = 'aimentor-sqlserver-distributed-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $password = 'Aa1!' + [Guid]::NewGuid().ToString('N')
    $env:AIMENTOR_SQLSERVER_SA_PASSWORD = $password
    docker run -d --name $SqlContainer -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$password" `
        -p "${SqlHostPort}:1433" mcr.microsoft.com/mssql/server:2022-latest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '无法启动临时 SQL Server 容器。' }
    $ownsSqlContainer = $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 70; $attempt++) {
        try {
            docker exec -e "SQLCMDPASSWORD=$password" $SqlContainer /opt/mssql-tools18/bin/sqlcmd `
                -S localhost -U sa -C -b -Q 'SELECT 1' 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) {
        docker rm -f $SqlContainer 2>$null | Out-Null
        throw '临时 SQL Server 容器未就绪。'
    }
} else {
    $password = $env:AIMENTOR_SQLSERVER_SA_PASSWORD
    if ([string]::IsNullOrWhiteSpace($password)) {
        throw '请通过 AIMENTOR_SQLSERVER_SA_PASSWORD 提供正在运行的测试 SQL Server sa 密码。'
    }
    if (-not (docker inspect $SqlContainer 2>$null)) {
        throw "找不到 SQL Server 容器：$SqlContainer"
    }
}

$database = 'AiMentorDistributedTest_' + [Guid]::NewGuid().ToString('N')
$remoteMigrations = '/tmp/aimentor-migrations-' + [Guid]::NewGuid().ToString('N')
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('aimentor-distributed-' + [Guid]::NewGuid().ToString('N'))
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$client = [System.Net.Http.HttpClient]::new()
$succeeded = $false

function Invoke-Sql([string]$Query, [string]$DatabaseName = 'master') {
    docker exec -e "SQLCMDPASSWORD=$password" $SqlContainer /opt/mssql-tools18/bin/sqlcmd `
        -S localhost -U sa -C -b -d $DatabaseName -Q $Query | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SQL 命令执行失败，数据库：$DatabaseName" }
}

function Invoke-SqlScalar([string]$Query, [string]$DatabaseName = $database) {
    $output = docker exec -e "SQLCMDPASSWORD=$password" $SqlContainer /opt/mssql-tools18/bin/sqlcmd `
        -S localhost -U sa -C -b -h -1 -W -d $DatabaseName -Q "SET NOCOUNT ON; $Query"
    if ($LASTEXITCODE -ne 0) { throw "SQL 标量查询失败，数据库：$DatabaseName" }
    return (($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join '').Trim()
}

function Start-Api([string]$Name, [int]$Port, [string]$SubjectId, [string[]]$Groups,
    [string]$Environment = 'Development', [string]$BarrierSignalPath = '',
    [string]$BarrierReleasePath = '') {
    $env:ASPNETCORE_ENVIRONMENT = $Environment
    $env:Workflow__Provider = 'SqlServer'
    $env:Workflow__InitializeSchema = 'false'
    # 使用构造器处理密码中的分号、引号等保留字符，避免测试脚本产生错误连接字符串。
    $connection = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $connection['Data Source'] = "127.0.0.1,$SqlHostPort"
    $connection['Initial Catalog'] = $database
    $connection['User ID'] = 'sa'
    $connection['Password'] = $password
    $connection['Encrypt'] = $true
    $connection['TrustServerCertificate'] = $true
    $env:ConnectionStrings__WorkflowSqlServer = $connection.ConnectionString
    $env:AIMENTOR_MEMORY_ENCRYPTION_KEY = $script:masterKey
    $env:Workflow__Encryption__ActiveKeyVersion = 'v1'
    $env:Workflow__Encryption__Keys__v1 = $script:masterKey
    $env:Authentication__Development__TenantId = 'tenant-distributed'
    $env:Authentication__Development__SubjectId = $SubjectId
    Remove-Item Env:Authentication__Development__Groups__0 -ErrorAction SilentlyContinue
    Remove-Item Env:Authentication__Development__Groups__1 -ErrorAction SilentlyContinue
    for ($index = 0; $index -lt $Groups.Count; $index++) {
        Set-Item "Env:Authentication__Development__Groups__$index" $Groups[$index]
    }
    Remove-Item Env:Testing__ToolExecutionBarrier__SignalPath -ErrorAction SilentlyContinue
    Remove-Item Env:Testing__ToolExecutionBarrier__ReleasePath -ErrorAction SilentlyContinue
    if (-not [string]::IsNullOrWhiteSpace($BarrierSignalPath)) {
        $env:Testing__ToolExecutionBarrier__SignalPath = $BarrierSignalPath
        $env:Testing__ToolExecutionBarrier__ReleasePath = $BarrierReleasePath
    }
    $instanceDirectory = Join-Path $temporaryRoot $Name
    New-Item -ItemType Directory -Path $instanceDirectory -Force | Out-Null
    $env:Memory__StorePath = Join-Path $instanceDirectory 'memory.json'
    $process = Start-Process dotnet -ArgumentList @($apiDll, '--urls', "http://127.0.0.1:$Port") `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $instanceDirectory 'stdout.log') `
        -RedirectStandardError (Join-Path $instanceDirectory 'stderr.log')
    $processes.Add($process)
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $health = Send-Request 'GET' "http://127.0.0.1:$Port/health"
            if ($health.StatusCode -eq 200) { return $process }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw "API 实例未就绪：$Name"
}

function Stop-Api([System.Diagnostics.Process]$Process) {
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
        Wait-Process -Id $Process.Id -ErrorAction SilentlyContinue
    }
}

function Send-Request([string]$Method, [string]$Url, $Body = $null,
    [hashtable]$Headers = @{}) {
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new($Method), $Url)
    foreach ($entry in $Headers.GetEnumerator()) {
        $null = $request.Headers.TryAddWithoutValidation($entry.Key, [string]$entry.Value)
    }
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 12 -Compress
        $request.Content = [System.Net.Http.StringContent]::new($json,
            [Text.Encoding]::UTF8, 'application/json')
    }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $request.Dispose()
    [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Content = $content
        Json = if ([string]::IsNullOrWhiteSpace($content)) { $null } else { $content | ConvertFrom-Json }
    }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message；期望=$Expected，实际=$Actual" }
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    dotnet build (Join-Path $repositoryRoot 'AiMentor.slnx') --no-restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    $keyBytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($keyBytes) } finally { $random.Dispose() }
    $script:masterKey = [Convert]::ToBase64String($keyBytes)

    Invoke-Sql "CREATE DATABASE [$database];"
    docker cp (Join-Path $repositoryRoot 'deploy\sql\.') "${SqlContainer}:$remoteMigrations" | Out-Null
    foreach ($migration in @('001_workflow.sql', '002_workflow_key_version.sql',
            '003_tool_execution_ledger.sql', '004_tool_execution_reconciliation.sql',
            '005_tool_reconciliation_reviews.sql')) {
        docker exec -e "SQLCMDPASSWORD=$password" $SqlContainer /opt/mssql-tools18/bin/sqlcmd `
            -S localhost -U sa -C -b -d $database -i "$remoteMigrations/$migration" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "迁移失败：$migration" }
    }

    $requester = Start-Api 'requester' $BasePort 'requester-a' @('users')
    $approver = Start-Api 'approver' ($BasePort + 1) 'approver-a' @('tool-approvers')
    $reviewerA = Start-Api 'reviewer-a' ($BasePort + 2) 'reviewer-a' @('tool-reconcilers')
    $reviewerB = Start-Api 'reviewer-b' ($BasePort + 3) 'reviewer-b' @('tool-reconcilers')
    $reviewerC = Start-Api 'reviewer-c' ($BasePort + 4) 'reviewer-c' @('tool-reconcilers')

    # 仅 Testing 实例启用执行屏障，在 SQL 已持久化 Executing 后、真实工具调用前暴露确定窗口。
    $barrierDirectory = Join-Path $temporaryRoot 'executing-kill-barrier'
    New-Item -ItemType Directory -Path $barrierDirectory -Force | Out-Null
    $barrierSignal = Join-Path $barrierDirectory 'executing.signal'
    $barrierRelease = Join-Path $barrierDirectory 'executing.release'
    $crashRequester = Start-Api 'executing-kill-requester' ($BasePort + 5) 'crash-requester-a' @('users') `
        'Testing' $barrierSignal $barrierRelease
    $crashArguments = @{ memoryId = 'must-not-run-during-executing-kill'; expectedVersion = 1 }
    $crashApproval = Send-Request 'POST' "http://127.0.0.1:$($BasePort + 5)/api/v1/tool-approvals" @{
        toolName = 'memory.delete'; arguments = $crashArguments; justification = '精确 Executing 窗口强杀验收'
    }
    Assert-Equal 202 $crashApproval.StatusCode '强杀场景审批申请失败'
    $crashDecision = Send-Request 'POST' "http://127.0.0.1:$($BasePort + 1)/api/v1/tool-approvals/$($crashApproval.Json.id)/decision" @{
        approved = $true; reason = '独立批准精确强杀验收'
    }
    Assert-Equal 200 $crashDecision.StatusCode '强杀场景审批裁决失败'

    $crashBody = @{
        arguments = $crashArguments; approvalId = $crashApproval.Json.id
    } | ConvertTo-Json -Depth 8 -Compress
    $crashRequest = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "http://127.0.0.1:$($BasePort + 5)/api/v1/tools/memory.delete/execute")
    $null = $crashRequest.Headers.TryAddWithoutValidation('Idempotency-Key', 'distributed-executing-kill-001')
    $crashRequest.Content = [System.Net.Http.StringContent]::new(
        $crashBody, [Text.Encoding]::UTF8, 'application/json')
    $crashTask = $client.SendAsync($crashRequest)

    $signalObserved = $false
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        if (Test-Path -LiteralPath $barrierSignal) { $signalObserved = $true; break }
        Start-Sleep -Milliseconds 25
    }
    if (-not $signalObserved) { throw '未观察到 Executing 屏障信号。' }
    $signalParts = (Get-Content -LiteralPath $barrierSignal -Raw).Split('|', 2)
    $crashExecutionKey = $signalParts[0]
    if ($crashExecutionKey -notmatch '^[A-F0-9]{64}$') { throw '屏障返回的执行键格式无效。' }
    Assert-Equal '1' (Invoke-SqlScalar "SELECT Status FROM dbo.AiMentorToolExecutions WHERE ExecutionKey='$crashExecutionKey';") `
        '强杀前 SQL 账本尚未进入 Executing'

    # 不写释放文件，直接终止进程，模拟副作用边界上的节点掉电。
    Stop-Api $crashRequester
    $crashRequest.Dispose()
    Start-Sleep -Seconds 46

    # 替代实例没有故障屏障；过期 Executing 必须被冻结，禁止自动调用 memory.delete。
    $crashReplacement = Start-Api 'executing-kill-replacement' ($BasePort + 5) 'crash-requester-a' @('users')
    $crashReplay = Send-Request 'POST' "http://127.0.0.1:$($BasePort + 5)/api/v1/tools/memory.delete/execute" @{
        arguments = $crashArguments
    } @{ 'Idempotency-Key' = 'distributed-executing-kill-001' }
    Assert-Equal 'OutcomeUnknown' $crashReplay.Json.status '过期 Executing 被错误地自动重放'
    Assert-Equal '3' (Invoke-SqlScalar "SELECT Status FROM dbo.AiMentorToolExecutions WHERE ExecutionKey='$crashExecutionKey';") `
        '过期 Executing 未冻结为 OutcomeUnknown'

    $arguments = @{ memoryId = 'missing-memory-for-distributed-test'; expectedVersion = 1 }
    $approval = Send-Request 'POST' "http://127.0.0.1:$BasePort/api/v1/tool-approvals" @{
        toolName = 'memory.delete'; arguments = $arguments; justification = '分布式故障验收'
    }
    Assert-Equal 202 $approval.StatusCode "审批申请失败：$($approval.Content)"
    $decision = Send-Request 'POST' "http://127.0.0.1:$($BasePort + 1)/api/v1/tool-approvals/$($approval.Json.id)/decision" @{
        approved = $true; reason = '独立批准分布式故障验收'
    }
    Assert-Equal 200 $decision.StatusCode '审批裁决失败'

    $execution = Send-Request 'POST' "http://127.0.0.1:$BasePort/api/v1/tools/memory.delete/execute" @{
        arguments = $arguments; approvalId = $approval.Json.id
    } @{ 'Idempotency-Key' = 'distributed-reconciliation-001' }
    Assert-Equal 'OutcomeUnknown' $execution.Json.status '首次失败未冻结为 OutcomeUnknown'

    # 强制终止首次调用实例，证明后续对账只依赖共享 SQL 状态。
    Stop-Api $requester
    $unknown = Send-Request 'GET' "http://127.0.0.1:$($BasePort + 2)/api/v1/tool-executions/outcome-unknown?limit=10"
    Assert-Equal 200 $unknown.StatusCode '对账列表查询失败'
    $standardUnknown = @($unknown.Json | Where-Object { $_.executionKey -ne $crashExecutionKey })
    if ($standardUnknown.Count -ne 1) { throw "常规结果不确定记录数量异常：$($standardUnknown.Count)" }
    $executionKey = $standardUnknown[0].executionKey

    $firstReview = Send-Request 'POST' "http://127.0.0.1:$($BasePort + 2)/api/v1/tool-executions/$executionKey/reviews" @{
        arguments = $arguments; confirmed = $true; reason = '第一人确认目标确实不存在'
    }
    Assert-Equal 'AwaitingSecondReviewer' $firstReview.Json.status '第一人复核状态异常'
    Stop-Api $reviewerA

    # 两个不同实例并发争抢第二人裁决，SQL 可串行化事务必须只允许一个完成结案。
    $reviewBody = @{ arguments = $arguments; confirmed = $true; reason = '第二人独立确认目标不存在' } |
        ConvertTo-Json -Depth 8 -Compress
    $urlB = "http://127.0.0.1:$($BasePort + 3)/api/v1/tool-executions/$executionKey/reviews"
    $urlC = "http://127.0.0.1:$($BasePort + 4)/api/v1/tool-executions/$executionKey/reviews"
    $requestB = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $urlB)
    $requestC = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $urlC)
    $requestB.Content = [System.Net.Http.StringContent]::new($reviewBody, [Text.Encoding]::UTF8, 'application/json')
    $requestC.Content = [System.Net.Http.StringContent]::new($reviewBody, [Text.Encoding]::UTF8, 'application/json')
    $taskB = $client.SendAsync($requestB); $taskC = $client.SendAsync($requestC)
    [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($taskB, $taskC))
    $responses = @($taskB.Result, $taskC.Result)
    $resolved = @($responses | Where-Object {
        $_.IsSuccessStatusCode -and ($_.Content.ReadAsStringAsync().Result | ConvertFrom-Json).status -eq 'ResolvedApplied'
    })
    Assert-Equal 1 $resolved.Count '并发第二人裁决没有实现单一胜者'

    # 启动同身份替代实例，模拟滚动部署后从共享账本回放已裁决终态。
    $replacement = Start-Api 'requester-replacement' $BasePort 'requester-a' @('users')
    $replay = Send-Request 'POST' "http://127.0.0.1:$BasePort/api/v1/tools/memory.delete/execute" @{
        arguments = $arguments
    } @{ 'Idempotency-Key' = 'distributed-reconciliation-001' }
    Assert-Equal 200 $replay.StatusCode '滚动替代实例未能读取裁决终态'
    Assert-Equal 'Reconciled' $replay.Json.status '裁决终态发生了重复工具调用'

    $succeeded = $true

    [pscustomobject]@{
        Database = $database
        ConcurrentApiInstances = 6
        ApiProcessesStarted = 7
        ExecutingSignalObserved = $signalObserved
        ExecutingLeaseRecovery = $crashReplay.Json.status
        InitialOutcome = $execution.Json.status
        FirstReview = $firstReview.Json.status
        ConcurrentSecondReviewWinners = $resolved.Count
        RollingReplay = $replay.Json.status
        Passed = $true
    } | ConvertTo-Json -Compress
}
finally {
    foreach ($process in $processes) { Stop-Api $process }
    $client.Dispose()
    try { Invoke-Sql "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database];" } catch { }
    try { docker exec -u 0 $SqlContainer rm -rf $remoteMigrations 2>$null | Out-Null } catch { }
    if ($ownsSqlContainer) {
        try { docker rm -f $SqlContainer 2>$null | Out-Null } catch { }
    }
    if ($succeeded) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Warning "分布式验收失败，诊断日志保留于：$temporaryRoot"
    }
}
