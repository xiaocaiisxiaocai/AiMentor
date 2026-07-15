[CmdletBinding()]
param(
    [string]$SqlContainer = 'aimentor-sqlserver',
    [ValidateRange(0, 65535)][int]$SqlHostPort = 0,
    [int]$BasePort = 5510,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$UseEphemeralSqlServer
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$apiDll = [IO.Path]::Combine($repositoryRoot, 'src', 'AiMentor.Api', 'bin', $Configuration, 'net10.0',
    'AiMentor.Api.dll')
$ownsSqlContainer = $false
if ($UseEphemeralSqlServer) {
    $SqlContainer = 'aimentor-sqlserver-distributed-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $password = 'Aa1!' + [Guid]::NewGuid().ToString('N')
    $env:AIMENTOR_SQLSERVER_SA_PASSWORD = $password
    $portBinding = if ($SqlHostPort -eq 0) { '127.0.0.1::1433' } else { "127.0.0.1:${SqlHostPort}:1433" }
    docker run -d --name $SqlContainer -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$password" `
        -p $portBinding mcr.microsoft.com/mssql/server:2022-latest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '无法启动临时 SQL Server 容器。' }
    $ownsSqlContainer = $true
    if ($SqlHostPort -eq 0) {
        $publishedPort = docker port $SqlContainer 1433/tcp
        if ($LASTEXITCODE -ne 0 -or $publishedPort -notmatch ':(\d+)\s*$') {
            docker rm -f $SqlContainer 2>$null | Out-Null
            throw '无法解析临时 SQL Server 的动态宿主端口。'
        }
        $SqlHostPort = [int]$Matches[1]
    }
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
    if ($SqlHostPort -eq 0) { $SqlHostPort = 1433 }
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

function ConvertTo-SqlConnectionValue([string]$Value) {
    # 双引号包裹并把内部双引号加倍，遵循 SqlClient 连接字符串语法且不依赖平台专属程序集。
    return '"' + $Value.Replace('"', '""') + '"'
}

function Start-Api([string]$Name, [int]$Port, [string]$SubjectId, [string[]]$Groups,
    [string]$Environment = 'Development', [string]$BarrierSignalPath = '',
    [string]$BarrierReleasePath = '', [string]$MemoryStorePath = '') {
    $env:ASPNETCORE_ENVIRONMENT = $Environment
    $env:Workflow__Provider = 'SqlServer'
    $env:Workflow__InitializeSchema = 'false'
    $quotedDatabase = ConvertTo-SqlConnectionValue $database
    $quotedPassword = ConvertTo-SqlConnectionValue $password
    $env:ConnectionStrings__WorkflowSqlServer =
        "Server=127.0.0.1,$SqlHostPort;Initial Catalog=$quotedDatabase;User ID=sa;" +
        "Password=$quotedPassword;Encrypt=True;TrustServerCertificate=True"
    $env:AIMENTOR_MEMORY_ENCRYPTION_KEY = $script:masterKey
    $env:Workflow__Encryption__ActiveKeyVersion = 'v1'
    $env:Workflow__Encryption__Keys__v1 = $script:masterKey
    $env:Authentication__Development__TenantId = 'tenant-distributed'
    $env:Authentication__Development__SubjectId = $SubjectId
    # 清除宿主可能预置的任意组索引，确保每个验收主体只拥有本次显式声明的权限。
    Get-ChildItem Env: | Where-Object Name -Like 'Authentication__Development__Groups__*' |
        Remove-Item -ErrorAction SilentlyContinue
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
    $env:Memory__StorePath = if ([string]::IsNullOrWhiteSpace($MemoryStorePath)) {
        Join-Path $instanceDirectory 'memory.json'
    } else { $MemoryStorePath }
    $startParameters = @{
        FilePath = 'dotnet'; ArgumentList = @($apiDll, '--urls', "http://127.0.0.1:$Port"); PassThru = $true
        RedirectStandardOutput = Join-Path $instanceDirectory 'stdout.log'
        RedirectStandardError = Join-Path $instanceDirectory 'stderr.log'
    }
    # WindowStyle 只存在于 Windows；Linux pwsh 传入该参数会在 API 尚未启动前失败。
    if ($PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows) { $startParameters.WindowStyle = 'Hidden' }
    $process = Start-Process @startParameters
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
    dotnet build (Join-Path $repositoryRoot 'AiMentor.slnx') --configuration $Configuration --no-restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    $keyBytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($keyBytes) } finally { $random.Dispose() }
    $script:masterKey = [Convert]::ToBase64String($keyBytes)

    Invoke-Sql "CREATE DATABASE [$database];"
    $migrationRoot = [IO.Path]::Combine($repositoryRoot, 'deploy', 'sql')
    docker cp $migrationRoot "${SqlContainer}:$remoteMigrations" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '无法把 SQL 迁移复制到测试容器。' }
    foreach ($migration in @('001_workflow.sql', '002_workflow_key_version.sql',
            '003_tool_execution_ledger.sql', '004_tool_execution_reconciliation.sql',
            '005_tool_reconciliation_reviews.sql', '006_agent_run_cancellation.sql',
            '007_tool_compensations.sql', '008_tool_compensation_reconciliation.sql',
            '009_atlas_incident_runs.sql', '010_operations_actions.sql')) {
        docker exec -e "SQLCMDPASSWORD=$password" $SqlContainer /opt/mssql-tools18/bin/sqlcmd `
            -S localhost -U sa -C -b -d $database -i "$remoteMigrations/$migration" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "迁移失败：$migration" }
    }

    $requester = Start-Api 'requester' $BasePort 'requester-a' @('users')
    $approver = Start-Api 'approver' ($BasePort + 1) 'approver-a' @('tool-approvers')
    $reviewerA = Start-Api 'reviewer-a' ($BasePort + 2) 'reviewer-a' @('tool-reconcilers')
    $reviewerB = Start-Api 'reviewer-b' ($BasePort + 3) 'reviewer-b' @('tool-reconcilers')
    $reviewerC = Start-Api 'reviewer-c' ($BasePort + 4) 'reviewer-c' @('tool-reconcilers')

    # Atlas 检查点必须进入共享 SQL；运营队列只返回任务摘要，不得泄露安全输入中的节点名。
    $atlasPrivateNode = 'atlas-private-node-distributed'
    $atlasRun = Send-Request 'POST' "http://127.0.0.1:$BasePort/api/v1/incidents/atlasid/runs" @{
        region = 'cn'; node = $atlasPrivateNode
    }
    Assert-Equal 201 $atlasRun.StatusCode 'Atlas SQL 运行创建失败'
    Assert-Equal 'RequiredInputs' $atlasRun.Json.status 'Atlas SQL 初始状态异常'
    $atlasOperations = Send-Request 'GET' `
        "http://127.0.0.1:$BasePort/api/v1/operations/tasks?type=incident&limit=10"
    Assert-Equal 200 $atlasOperations.StatusCode '运营任务队列查询失败'
    $atlasTask = @($atlasOperations.Json.items | Where-Object { $_.id -eq $atlasRun.Json.runId })
    Assert-Equal 1 $atlasTask.Count '运营任务队列没有聚合 Atlas SQL 运行'
    if (([string]$atlasOperations.Content).IndexOf($atlasPrivateNode, [StringComparison]::Ordinal) -ge 0) {
        throw '运营任务队列泄露 Atlas 安全输入。'
    }

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

    # 建立一条真实 memory.correct 补偿记录，随后在反向工具调用前的精确窗口强杀另一个实例。
    $proposal = Send-Request 'POST' "http://127.0.0.1:$BasePort/api/v1/memories/proposals" @{
        scope = 'UserPreference'; key = 'distributed.compensation'; value = 'before'
    }
    Assert-Equal 202 $proposal.StatusCode '补偿场景记忆提案失败'
    $memory = Send-Request 'POST' `
        "http://127.0.0.1:$BasePort/api/v1/memories/proposals/$($proposal.Json.id)/approve"
    Assert-Equal 201 $memory.StatusCode '补偿场景记忆批准失败'
    $correctArguments = @{
        memoryId = $memory.Json.id; expectedVersion = $memory.Json.version; value = 'after'
    }
    $correctApproval = Send-Request 'POST' "http://127.0.0.1:$BasePort/api/v1/tool-approvals" @{
        toolName = 'memory.correct'; arguments = $correctArguments; justification = '建立反向强杀验收记录'
    }
    Assert-Equal 202 $correctApproval.StatusCode '更正工具审批申请失败'
    $correctDecision = Send-Request 'POST' `
        "http://127.0.0.1:$($BasePort + 1)/api/v1/tool-approvals/$($correctApproval.Json.id)/decision" @{
        approved = $true; reason = '独立批准更正工具'
    }
    Assert-Equal 200 $correctDecision.StatusCode '更正工具审批裁决失败'
    $correctExecution = Send-Request 'POST' `
        "http://127.0.0.1:$BasePort/api/v1/tools/memory.correct/execute" @{
        arguments = $correctArguments; approvalId = $correctApproval.Json.id
    } @{ 'Idempotency-Key' = 'distributed-compensation-forward-001' }
    Assert-Equal 200 $correctExecution.StatusCode '更正工具执行失败'
    Assert-Equal 'Completed' $correctExecution.Json.status '更正工具没有完成正向执行'
    $compensationId = $correctExecution.Json.compensationId
    if ([string]::IsNullOrWhiteSpace($compensationId)) { throw '更正工具没有返回补偿标识。' }

    $compensationApproval = Send-Request 'POST' `
        "http://127.0.0.1:$BasePort/api/v1/tool-compensations/$compensationId/approval" @{
        justification = '验证补偿精确窗口强杀'
    }
    Assert-Equal 202 $compensationApproval.StatusCode '补偿审批申请失败'
    $compensationDecision = Send-Request 'POST' `
        "http://127.0.0.1:$($BasePort + 1)/api/v1/tool-compensations/$compensationId/decision" @{
        approvalId = $compensationApproval.Json.approvalId; approved = $true; reason = '独立批准反向操作'
    }
    Assert-Equal 200 $compensationDecision.StatusCode '补偿审批裁决失败'

    $compensationBarrierDirectory = Join-Path $temporaryRoot 'compensation-executing-kill-barrier'
    New-Item -ItemType Directory -Path $compensationBarrierDirectory -Force | Out-Null
    $compensationSignal = Join-Path $compensationBarrierDirectory 'executing.signal'
    $compensationRelease = Join-Path $compensationBarrierDirectory 'executing.release'
    $compensationCrash = Start-Api 'compensation-executing-kill' ($BasePort + 6) 'requester-a' @('users') `
        'Testing' $compensationSignal $compensationRelease
    $compensationBody = @{ approvalId = $compensationApproval.Json.approvalId } |
        ConvertTo-Json -Depth 4 -Compress
    $compensationRequest = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "http://127.0.0.1:$($BasePort + 6)/api/v1/tool-compensations/$compensationId/execute")
    $null = $compensationRequest.Headers.TryAddWithoutValidation(
        'Idempotency-Key', 'distributed-compensation-reverse-001')
    $compensationRequest.Content = [System.Net.Http.StringContent]::new(
        $compensationBody, [Text.Encoding]::UTF8, 'application/json')
    $compensationTask = $client.SendAsync($compensationRequest)

    $compensationSignalObserved = $false
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        if (Test-Path -LiteralPath $compensationSignal) { $compensationSignalObserved = $true; break }
        Start-Sleep -Milliseconds 25
    }
    if (-not $compensationSignalObserved) { throw '未观察到补偿 Executing 屏障信号。' }
    $compensationSignalParts = (Get-Content -LiteralPath $compensationSignal -Raw).Split('|', 2)
    Assert-Equal 'memory.correct.restore' $compensationSignalParts[1] '补偿屏障工具名错误'
    Assert-Equal '5' (Invoke-SqlScalar `
        "SELECT Status FROM dbo.AiMentorToolCompensations WHERE Id='$compensationId';") `
        '强杀前补偿账本尚未进入 Executing'

    # 不释放屏障即强杀；原记忆必须保持正向值，证明反向工具尚未开始。
    Stop-Api $compensationCrash
    $compensationRequest.Dispose()
    $memoryAfterKillResponse = Send-Request 'GET' "http://127.0.0.1:$BasePort/api/v1/memories"
    Assert-Equal 200 $memoryAfterKillResponse.StatusCode '强杀后目标记忆查询失败'
    $memoryAfterKill = @($memoryAfterKillResponse.Json | Where-Object { $_.id -eq $memory.Json.id })
    Assert-Equal 1 $memoryAfterKill.Count '强杀后目标记忆不可见'
    Assert-Equal 'after' $memoryAfterKill[0].value '强杀窗口越过了反向副作用边界'
    Start-Sleep -Seconds 46

    # 替代实例通过带状态过滤的只读入口触发租约归一化，只能看到冻结状态，不能接管反向执行。
    $compensationReplacement = Start-Api 'compensation-kill-replacement' ($BasePort + 6) 'requester-a' @('users')
    $unknownCompensationsResponse = Send-Request 'GET' `
        "http://127.0.0.1:$($BasePort + 6)/api/v1/tool-compensations?status=OutcomeUnknown"
    Assert-Equal 200 $unknownCompensationsResponse.StatusCode '结果不确定补偿查询失败'
    $unknownCompensations = @($unknownCompensationsResponse.Json)
    $frozenCompensation = @($unknownCompensations | Where-Object { $_.id -eq $compensationId })
    Assert-Equal 1 $frozenCompensation.Count '替代实例没有查询到结果不确定补偿'
    Assert-Equal 'OutcomeUnknown' $frozenCompensation[0].status '补偿租约过期后没有冻结'
    $compensationRetry = Send-Request 'POST' `
        "http://127.0.0.1:$($BasePort + 6)/api/v1/tool-compensations/$compensationId/execute" @{
        approvalId = $compensationApproval.Json.approvalId
    } @{ 'Idempotency-Key' = 'distributed-compensation-reverse-001' }
    Assert-Equal 409 $compensationRetry.StatusCode '结果不确定补偿被错误接管或重放'
    Assert-Equal 'TOOL_COMPENSATION_OUTCOME_UNKNOWN' $compensationRetry.Json.code '补偿冻结错误码不稳定'
    Assert-Equal '8' (Invoke-SqlScalar `
        "SELECT Status FROM dbo.AiMentorToolCompensations WHERE Id='$compensationId';") `
        '补偿账本没有持久化 OutcomeUnknown'

    # 新建独立补偿，让两个审批实例和两个执行实例同时争抢；SQL 状态机必须各自产生唯一胜者。
    $stressCorrectArguments = @{
        memoryId = $memory.Json.id; expectedVersion = $memoryAfterKill[0].version; value = 'stress-after'
    }
    $stressForwardApproval = Send-Request 'POST' "http://127.0.0.1:$BasePort/api/v1/tool-approvals" @{
        toolName = 'memory.correct'; arguments = $stressCorrectArguments; justification = '并发补偿单胜者验收'
    }
    Assert-Equal 202 $stressForwardApproval.StatusCode '并发补偿正向审批申请失败'
    $stressForwardDecision = Send-Request 'POST' `
        "http://127.0.0.1:$($BasePort + 1)/api/v1/tool-approvals/$($stressForwardApproval.Json.id)/decision" @{
        approved = $true; reason = '批准并发补偿正向操作'
    }
    Assert-Equal 200 $stressForwardDecision.StatusCode '并发补偿正向审批失败'
    $stressForward = Send-Request 'POST' "http://127.0.0.1:$BasePort/api/v1/tools/memory.correct/execute" @{
        arguments = $stressCorrectArguments; approvalId = $stressForwardApproval.Json.id
    } @{ 'Idempotency-Key' = 'distributed-compensation-stress-forward-001' }
    Assert-Equal 'Completed' $stressForward.Json.status '并发补偿正向操作未完成'
    $stressCompensationId = $stressForward.Json.compensationId
    if ([string]::IsNullOrWhiteSpace($stressCompensationId)) { throw '并发补偿没有生成补偿标识。' }
    $stressApproval = Send-Request 'POST' `
        "http://127.0.0.1:$BasePort/api/v1/tool-compensations/$stressCompensationId/approval" @{
        justification = '并发审批与执行单胜者验收'
    }
    Assert-Equal 202 $stressApproval.StatusCode '并发补偿审批申请失败'

    $approverB = Start-Api 'approver-b' ($BasePort + 7) 'approver-b' @('tool-approvers')
    $stressDecisionBody = @{
        approvalId = $stressApproval.Json.approvalId; approved = $true; reason = '并发独立批准反向操作'
    } | ConvertTo-Json -Depth 4 -Compress
    $stressDecisionRequestA = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "http://127.0.0.1:$($BasePort + 1)/api/v1/tool-compensations/$stressCompensationId/decision")
    $stressDecisionRequestB = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "http://127.0.0.1:$($BasePort + 7)/api/v1/tool-compensations/$stressCompensationId/decision")
    $stressDecisionRequestA.Content = [System.Net.Http.StringContent]::new(
        $stressDecisionBody, [Text.Encoding]::UTF8, 'application/json')
    $stressDecisionRequestB.Content = [System.Net.Http.StringContent]::new(
        $stressDecisionBody, [Text.Encoding]::UTF8, 'application/json')
    $stressDecisionTaskA = $client.SendAsync($stressDecisionRequestA)
    $stressDecisionTaskB = $client.SendAsync($stressDecisionRequestB)
    [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stressDecisionTaskA, $stressDecisionTaskB))
    $stressDecisionResponses = @($stressDecisionTaskA.Result, $stressDecisionTaskB.Result)
    $stressDecisionWinners = @($stressDecisionResponses | Where-Object {
        $_.IsSuccessStatusCode -and
        ($_.Content.ReadAsStringAsync().Result | ConvertFrom-Json).status -eq 'Approved'
    })
    Assert-Equal 1 $stressDecisionWinners.Count '并发补偿审批没有产生唯一胜者'
    Assert-Equal 1 @($stressDecisionResponses | Where-Object { [int]$_.StatusCode -eq 409 }).Count `
        '并发补偿审批失败方没有稳定返回冲突'
    $stressDecisionRequestA.Dispose(); $stressDecisionRequestB.Dispose()

    $requesterMemoryPath = Join-Path (Join-Path $temporaryRoot 'requester') 'memory.json'
    $stressRequester = Start-Api -Name 'compensation-stress-requester' -Port ($BasePort + 8) `
        -SubjectId 'requester-a' -Groups @('users') -MemoryStorePath $requesterMemoryPath
    $stressExecutionBody = @{ approvalId = $stressApproval.Json.approvalId } |
        ConvertTo-Json -Depth 4 -Compress
    $stressExecutionRequestA = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "http://127.0.0.1:$BasePort/api/v1/tool-compensations/$stressCompensationId/execute")
    $stressExecutionRequestB = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "http://127.0.0.1:$($BasePort + 8)/api/v1/tool-compensations/$stressCompensationId/execute")
    $null = $stressExecutionRequestA.Headers.TryAddWithoutValidation(
        'Idempotency-Key', 'distributed-compensation-stress-reverse-a')
    $null = $stressExecutionRequestB.Headers.TryAddWithoutValidation(
        'Idempotency-Key', 'distributed-compensation-stress-reverse-b')
    $stressExecutionRequestA.Content = [System.Net.Http.StringContent]::new(
        $stressExecutionBody, [Text.Encoding]::UTF8, 'application/json')
    $stressExecutionRequestB.Content = [System.Net.Http.StringContent]::new(
        $stressExecutionBody, [Text.Encoding]::UTF8, 'application/json')
    $stressExecutionTaskA = $client.SendAsync($stressExecutionRequestA)
    $stressExecutionTaskB = $client.SendAsync($stressExecutionRequestB)
    [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stressExecutionTaskA, $stressExecutionTaskB))
    $stressExecutionResponses = @($stressExecutionTaskA.Result, $stressExecutionTaskB.Result)
    $stressExecutionWinners = @($stressExecutionResponses | Where-Object {
        $_.IsSuccessStatusCode -and
        ($result = $_.Content.ReadAsStringAsync().Result | ConvertFrom-Json).status -eq 'Completed' -and
        -not $result.idempotentReplay
    })
    Assert-Equal 1 $stressExecutionWinners.Count '并发补偿执行没有产生唯一非重放胜者'
    Assert-Equal 1 @($stressExecutionResponses | Where-Object { [int]$_.StatusCode -eq 409 }).Count `
        '并发补偿执行失败方没有稳定返回冲突'
    Assert-Equal '6' (Invoke-SqlScalar `
        "SELECT Status FROM dbo.AiMentorToolCompensations WHERE Id='$stressCompensationId';") `
        '并发补偿执行没有持久化 Completed'
    $stressMemoryResponse = Send-Request 'GET' "http://127.0.0.1:$BasePort/api/v1/memories"
    $stressMemory = @($stressMemoryResponse.Json | Where-Object { $_.id -eq $memory.Json.id })
    Assert-Equal 1 $stressMemory.Count '并发补偿后目标记忆不可见'
    Assert-Equal 'after' $stressMemory[0].value '并发补偿没有精确恢复正向操作前的值'
    $stressExecutionRequestA.Dispose(); $stressExecutionRequestB.Dispose()

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
    $atlasRecovered = Send-Request 'GET' `
        "http://127.0.0.1:$BasePort/api/v1/incidents/atlasid/runs/$($atlasRun.Json.runId)"
    Assert-Equal 200 $atlasRecovered.StatusCode '滚动替代实例未能读取 Atlas SQL 检查点'
    Assert-Equal $atlasPrivateNode $atlasRecovered.Json.safeInput.node 'Atlas SQL 检查点内容未持久恢复'
    $atlasOperationsRecovered = Send-Request 'GET' `
        "http://127.0.0.1:$BasePort/api/v1/operations/tasks?type=incident&limit=10"
    $recoveredAtlasTask = @($atlasOperationsRecovered.Json.items |
        Where-Object { $_.id -eq $atlasRun.Json.runId })
    Assert-Equal 1 $recoveredAtlasTask.Count '滚动替代实例未恢复运营任务队列中的 Atlas 任务'
    if (([string]$atlasOperationsRecovered.Content).IndexOf(
            $atlasPrivateNode, [StringComparison]::Ordinal) -ge 0) {
        throw '滚动恢复后的运营任务队列泄露 Atlas 安全输入。'
    }

    $succeeded = $true

    [pscustomobject]@{
        Database = $database
        ConcurrentApiInstances = 8
        ApiProcessesStarted = 12
        ExecutingSignalObserved = $signalObserved
        ExecutingLeaseRecovery = $crashReplay.Json.status
        CompensationSignalObserved = $compensationSignalObserved
        CompensationLeaseRecovery = $frozenCompensation[0].status
        CompensationTargetValueAfterKill = $memoryAfterKill[0].value
        ConcurrentCompensationApprovalWinners = $stressDecisionWinners.Count
        ConcurrentCompensationExecutionWinners = $stressExecutionWinners.Count
        ConcurrentCompensationFinalStatus = 'Completed'
        InitialOutcome = $execution.Json.status
        FirstReview = $firstReview.Json.status
        ConcurrentSecondReviewWinners = $resolved.Count
        RollingReplay = $replay.Json.status
        AtlasSqlRecovery = $atlasRecovered.Json.status
        OperationsAtlasTasks = $recoveredAtlasTask.Count
        OperationsPayloadRedacted = $true
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
