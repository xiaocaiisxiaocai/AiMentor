using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Data.SqlClient;

namespace AiMentor.Infrastructure;

/// <summary>
/// 在 SQL Server 中持久化工具审批，并使用可串行化事务保证裁决和消费只能成功一次。
/// 参数值不入库，只保存与租户、申请人和工具绑定的 SHA-256 指纹及参数名。
/// </summary>
public sealed class SqlServerToolApprovalService(
    IToolRegistry registry,
    IToolInvocationSafetyService safety,
    ITraceSink traceSink,
    ToolApprovalOptions approvalOptions,
    SqlServerWorkflowOptions sqlOptions,
    TimeProvider timeProvider) : IToolApprovalService, IDisposable
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;
    private static string TableName => ValidateTableName("AiMentorToolApprovals");

    /// <inheritdoc />
    public async Task<ToolApprovalRequest> RequestAsync(string toolName, JsonElement arguments, string justification,
        AccessContext requester, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        ValidateAccess(requester);
        var normalizedToolName = RequiredText(toolName, 128, "TOOL_APPROVAL_TOOL_INVALID", "工具名称无效。");
        var normalizedJustification = RequiredText(justification, 500, "TOOL_APPROVAL_JUSTIFICATION_INVALID",
            "审批理由不能为空且不能超过 500 个字符。");
        if (!registry.TryGet(normalizedToolName, out var tool) || tool is null)
            throw Failure("TOOL_NOT_REGISTERED", "请求的工具未在服务器注册表中。", ToolApprovalErrorKind.NotFound);
        if (tool.Descriptor.Risk == ToolOperationRisk.ReadOnly)
            throw Failure("TOOL_APPROVAL_NOT_REQUIRED", "只读工具不接受修改性操作审批。", ToolApprovalErrorKind.Validation);
        var normalizedArguments = NormalizeArguments(arguments);
        if (Encoding.UTF8.GetByteCount(normalizedArguments.GetRawText()) > approvalOptions.MaximumArgumentBytes)
            throw Failure("TOOL_ARGUMENTS_TOO_LARGE", "工具参数超过审批服务允许的大小。", ToolApprovalErrorKind.Validation);
        var validation = tool.ValidateArguments(normalizedArguments);
        if (validation.Action != SafetyAction.Allow)
            throw Failure(validation.Code, validation.Message, ToolApprovalErrorKind.Validation);
        var invocationArguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(normalizedArguments.GetRawText()) ?? [];
        var safetyDecision = safety.Review(new ToolInvocationRequest(tool.Descriptor.Name, tool.Descriptor.Risk,
            invocationArguments), requester);
        if (safetyDecision.Action == SafetyAction.Refuse)
            throw Failure(safetyDecision.Code, safetyDecision.Message, ToolApprovalErrorKind.Forbidden);
        if (safetyDecision.Action != SafetyAction.RequireApproval)
            throw Failure("TOOL_APPROVAL_POLICY_MISMATCH", "当前安全策略未要求该工具进入审批流程。", ToolApprovalErrorKind.Conflict);

        var now = timeProvider.GetUtcNow();
        var request = new ToolApprovalRequest(Guid.NewGuid().ToString("N"), requester.TenantId, requester.SubjectId,
            tool.Descriptor.Name, tool.Descriptor.Risk,
            JsonArgumentFingerprint.Create(tool.Descriptor.Name, normalizedArguments, requester),
            normalizedArguments.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray(),
            normalizedJustification, now, now.Add(approvalOptions.ApprovalLifetime), ToolApprovalStatus.Pending);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = $"DELETE FROM dbo.[{TableName}] WHERE Status IN (@rejected,@expired,@consumed) AND COALESCE(ConsumedAt,DecidedAt,ExpiresAt)<=@cutoff;";
            AddInt(prune, "@rejected", (int)ToolApprovalStatus.Rejected);
            AddInt(prune, "@expired", (int)ToolApprovalStatus.Expired);
            AddInt(prune, "@consumed", (int)ToolApprovalStatus.Consumed);
            AddDateTimeOffset(prune, "@cutoff", now.Subtract(approvalOptions.ApprovalLifetime));
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.[{TableName}] WITH (UPDLOCK,HOLDLOCK);";
        if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
            >= approvalOptions.MaximumEntries)
            throw Failure("TOOL_APPROVAL_CAPACITY_EXCEEDED", "审批记录已达到容量上限，请稍后重试。", ToolApprovalErrorKind.Capacity);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"""
            INSERT dbo.[{TableName}]
              (Id,TenantId,RequesterSubjectId,ToolName,Risk,ArgumentsHash,ArgumentNamesJson,Justification,
               CreatedAt,ExpiresAt,Status)
            VALUES
              (@id,@tenant,@requester,@tool,@risk,@hash,@names,@justification,@created,@expires,@status);
            """;
        AddRequestParameters(insert, request);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await AuditAsync("tool.approval.requested", "pending", requester, request, "TOOL_APPROVAL_PENDING", cancellationToken);
        return request;
    }

    /// <inheritdoc />
    public async Task<ToolApprovalRequest> DecideAsync(string approvalId, bool approved, string reason,
        AccessContext approver, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        ValidateAccess(approver);
        var id = RequiredText(approvalId, 128, "TOOL_APPROVAL_ID_INVALID", "审批标识无效。");
        var normalizedReason = RequiredText(reason, 500, "TOOL_APPROVAL_REASON_INVALID", "审批决定理由不能为空且不能超过 500 个字符。");
        if (!approver.Groups.Overlaps(approvalOptions.ApproverGroups))
            throw Failure("TOOL_APPROVER_ROLE_REQUIRED", "当前用户不属于工具审批人组。", ToolApprovalErrorKind.Forbidden);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var current = await ReadForUpdateAsync(connection, transaction, id, cancellationToken);
        if (current is null || !string.Equals(current.TenantId, approver.TenantId, StringComparison.Ordinal))
            throw Failure("TOOL_APPROVAL_NOT_FOUND", "没有找到当前租户可裁决的审批。", ToolApprovalErrorKind.NotFound);
        if (string.Equals(current.RequesterSubjectId, approver.SubjectId, StringComparison.Ordinal))
            throw Failure("TOOL_APPROVAL_SELF_DECISION_DENIED", "申请人与审批人必须分离。", ToolApprovalErrorKind.Forbidden);
        var now = timeProvider.GetUtcNow();
        if (current.ExpiresAt <= now)
        {
            await SetExpiredAsync(connection, transaction, id, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw Failure("TOOL_APPROVAL_EXPIRED", "审批已过有效期，请重新申请。", ToolApprovalErrorKind.Conflict);
        }
        if (current.Status != ToolApprovalStatus.Pending)
            throw Failure("TOOL_APPROVAL_ALREADY_DECIDED", "审批已经裁决，不能重复操作。", ToolApprovalErrorKind.Conflict);
        var decided = current with
        {
            Status = approved ? ToolApprovalStatus.Approved : ToolApprovalStatus.Rejected,
            ApproverSubjectId = approver.SubjectId,
            DecidedAt = now,
            DecisionReason = normalizedReason
        };
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE dbo.[{TableName}] SET Status=@status,ApproverSubjectId=@approver,DecidedAt=@decided,DecisionReason=@reason WHERE Id=@id AND Status=@pending;";
        AddInt(update, "@status", (int)decided.Status); AddString(update, "@approver", 256, approver.SubjectId);
        AddDateTimeOffset(update, "@decided", now); AddString(update, "@reason", 500, normalizedReason);
        AddString(update, "@id", 128, id); AddInt(update, "@pending", (int)ToolApprovalStatus.Pending);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Failure("TOOL_APPROVAL_ALREADY_DECIDED", "审批已经裁决，不能重复操作。", ToolApprovalErrorKind.Conflict);
        await transaction.CommitAsync(cancellationToken);
        await AuditAsync("tool.approval.decided", approved ? "approved" : "rejected", approver, decided,
            approved ? "TOOL_APPROVAL_APPROVED" : "TOOL_APPROVAL_REJECTED", cancellationToken);
        return decided;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolApprovalRequest>> ListAsync(AccessContext access,
        ToolApprovalStatus? status = null, CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        await EnsureInitializedAsync(cancellationToken);
        await ExpireDueAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var canApprove = access.Groups.Overlaps(approvalOptions.ApproverGroups);
        command.CommandText = $"SELECT * FROM dbo.[{TableName}] WHERE TenantId=@tenant AND (@canApprove=1 OR RequesterSubjectId=@subject) AND (@status IS NULL OR Status=@status) ORDER BY CreatedAt DESC;";
        AddString(command, "@tenant", 128, access.TenantId); AddString(command, "@subject", 256, access.SubjectId);
        command.Parameters.Add("@canApprove", SqlDbType.Bit).Value = canApprove;
        command.Parameters.Add("@status", SqlDbType.Int).Value = status is null ? DBNull.Value : (int)status.Value;
        var result = new List<ToolApprovalRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        await traceSink.WriteAsync($"tool-approval-{Guid.NewGuid():N}",
            [new TraceStep("tool.approval.list", "ok", timeProvider.GetUtcNow(), new Dictionary<string, object?>
            {
                ["tenant"] = access.TenantId,
                ["subject"] = access.SubjectId,
                ["status"] = status?.ToString(),
                ["resultCount"] = result.Count
            })], cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task<ToolApprovalRequest> GetAsync(string approvalId, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        var id = RequiredText(approvalId, 128, "TOOL_APPROVAL_ID_INVALID", "审批标识无效。");
        await EnsureInitializedAsync(cancellationToken);
        await ExpireDueAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM dbo.[{TableName}] WHERE Id=@id AND TenantId=@tenant AND (RequesterSubjectId=@subject OR @canApprove=1);";
        AddString(command, "@id", 128, id); AddString(command, "@tenant", 128, access.TenantId);
        AddString(command, "@subject", 256, access.SubjectId);
        command.Parameters.Add("@canApprove", SqlDbType.Bit).Value = access.Groups.Overlaps(approvalOptions.ApproverGroups);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw Failure("TOOL_APPROVAL_NOT_FOUND", "没有找到当前用户可访问的审批。", ToolApprovalErrorKind.NotFound);
        return Read(reader);
    }

    /// <inheritdoc />
    public async Task<ToolApprovalConsumption> ConsumeAsync(string approvalId, string toolName, JsonElement arguments,
        AccessContext requester, CancellationToken cancellationToken = default)
    {
        ValidateAccess(requester);
        var id = approvalId?.Trim() ?? string.Empty;
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var current = id.Length is > 0 and <= 128
            ? await ReadForUpdateAsync(connection, transaction, id, cancellationToken) : null;
        SafetyDecision decision;
        var now = timeProvider.GetUtcNow();
        if (current is null || !string.Equals(current.TenantId, requester.TenantId, StringComparison.Ordinal)
            || !string.Equals(current.RequesterSubjectId, requester.SubjectId, StringComparison.Ordinal))
            decision = Refuse("TOOL_APPROVAL_NOT_FOUND", "没有找到可供当前申请人使用的审批凭据。");
        else if (current.ExpiresAt <= now)
        {
            await SetExpiredAsync(connection, transaction, id, now, cancellationToken);
            decision = Refuse("TOOL_APPROVAL_EXPIRED", "审批凭据已过期。");
        }
        else if (current.Status == ToolApprovalStatus.Consumed)
            decision = Refuse("TOOL_APPROVAL_ALREADY_CONSUMED", "审批凭据已经消费，不能重放。");
        else if (current.Status != ToolApprovalStatus.Approved)
            decision = Refuse("TOOL_APPROVAL_NOT_APPROVED", "审批凭据尚未批准或已被拒绝。");
        else
        {
            var fingerprint = JsonArgumentFingerprint.Create(toolName, NormalizeArguments(arguments), requester);
            if (!string.Equals(current.ToolName, toolName, StringComparison.OrdinalIgnoreCase)
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(current.ArgumentsHash), Convert.FromHexString(fingerprint)))
                decision = Refuse("TOOL_APPROVAL_SCOPE_MISMATCH", "审批凭据与当前工具或参数不匹配。");
            else
            {
                await using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = $"UPDATE dbo.[{TableName}] SET Status=@consumed,ConsumedAt=@now WHERE Id=@id AND Status=@approved;";
                AddInt(update, "@consumed", (int)ToolApprovalStatus.Consumed); AddDateTimeOffset(update, "@now", now);
                AddString(update, "@id", 128, id); AddInt(update, "@approved", (int)ToolApprovalStatus.Approved);
                decision = await update.ExecuteNonQueryAsync(cancellationToken) == 1
                    ? new SafetyDecision(SafetyAction.Allow, "TOOL_APPROVAL_CONSUMED", "审批凭据已完成精确匹配并被一次性消费。")
                    : Refuse("TOOL_APPROVAL_ALREADY_CONSUMED", "审批凭据已经消费，不能重放。");
            }
        }
        await transaction.CommitAsync(cancellationToken);
        if (current is not null)
            await AuditAsync("tool.approval.consumed", decision.Action == SafetyAction.Allow ? "consumed" : "refused",
                requester, current, decision.Code, cancellationToken);
        return new ToolApprovalConsumption(decision.Action == SafetyAction.Allow, decision);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            ValidateConfiguration();
            if (!sqlOptions.InitializeSchema)
            {
                _initialized = true;
                return;
            }
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                DECLARE @lockResult int;
                EXEC @lockResult=sys.sp_getapplock @Resource=N'AiMentor.Workflow.Schema',
                    @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000;
                IF @lockResult < 0 THROW 51000, '无法获取 AiMentor 数据库架构锁。', 1;
                IF OBJECT_ID(N'dbo.{TableName}',N'U') IS NULL
                BEGIN
                  CREATE TABLE dbo.[{TableName}] (
                    Id nvarchar(128) NOT NULL CONSTRAINT PK_{TableName} PRIMARY KEY,TenantId nvarchar(128) NOT NULL,
                    RequesterSubjectId nvarchar(256) NOT NULL,ToolName nvarchar(128) NOT NULL,Risk int NOT NULL,
                    ArgumentsHash char(64) NOT NULL,ArgumentNamesJson nvarchar(max) NOT NULL,Justification nvarchar(500) NOT NULL,
                    CreatedAt datetimeoffset(7) NOT NULL,ExpiresAt datetimeoffset(7) NOT NULL,Status int NOT NULL,
                    ApproverSubjectId nvarchar(256) NULL,DecidedAt datetimeoffset(7) NULL,DecisionReason nvarchar(500) NULL,
                    ConsumedAt datetimeoffset(7) NULL,RowVersion rowversion NOT NULL);
                  CREATE INDEX IX_{TableName}_TenantStatus ON dbo.[{TableName}](TenantId,Status,CreatedAt DESC);
                END;
                COMMIT TRANSACTION;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken); _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private async Task ExpireDueAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE dbo.[{TableName}] SET Status=@expired WHERE Status IN (@pending,@approved) AND ExpiresAt<=@now;";
        AddInt(command, "@expired", (int)ToolApprovalStatus.Expired); AddInt(command, "@pending", (int)ToolApprovalStatus.Pending);
        AddInt(command, "@approved", (int)ToolApprovalStatus.Approved); AddDateTimeOffset(command, "@now", timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ToolApprovalRequest?> ReadForUpdateAsync(SqlConnection connection, SqlTransaction transaction,
        string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT * FROM dbo.[{TableName}] WITH (UPDLOCK,HOLDLOCK) WHERE Id=@id;";
        AddString(command, "@id", 128, id); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task SetExpiredAsync(SqlConnection connection, SqlTransaction transaction, string id,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"UPDATE dbo.[{TableName}] SET Status=@expired WHERE Id=@id AND Status IN (@pending,@approved) AND ExpiresAt<=@now;";
        AddInt(command, "@expired", (int)ToolApprovalStatus.Expired); AddString(command, "@id", 128, id);
        AddInt(command, "@pending", (int)ToolApprovalStatus.Pending); AddInt(command, "@approved", (int)ToolApprovalStatus.Approved);
        AddDateTimeOffset(command, "@now", now); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ToolApprovalRequest Read(SqlDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("Id")), reader.GetString(reader.GetOrdinal("TenantId")),
        reader.GetString(reader.GetOrdinal("RequesterSubjectId")), reader.GetString(reader.GetOrdinal("ToolName")),
        (ToolOperationRisk)reader.GetInt32(reader.GetOrdinal("Risk")), reader.GetString(reader.GetOrdinal("ArgumentsHash")),
        JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("ArgumentNamesJson"))) ?? [],
        reader.GetString(reader.GetOrdinal("Justification")), reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CreatedAt")),
        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("ExpiresAt")), (ToolApprovalStatus)reader.GetInt32(reader.GetOrdinal("Status")),
        OptionalString(reader, "ApproverSubjectId"), OptionalDate(reader, "DecidedAt"), OptionalString(reader, "DecisionReason"),
        OptionalDate(reader, "ConsumedAt"));

    private static string? OptionalString(SqlDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetString(i); }
    private static DateTimeOffset? OptionalDate(SqlDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i); }
    private static void AddRequestParameters(SqlCommand command, ToolApprovalRequest request)
    {
        AddString(command, "@id", 128, request.Id); AddString(command, "@tenant", 128, request.TenantId);
        AddString(command, "@requester", 256, request.RequesterSubjectId); AddString(command, "@tool", 128, request.ToolName);
        AddInt(command, "@risk", (int)request.Risk); AddAnsiString(command, "@hash", 64, request.ArgumentsHash);
        AddString(command, "@names", -1, JsonSerializer.Serialize(request.ArgumentNames)); AddString(command, "@justification", 500, request.Justification);
        AddDateTimeOffset(command, "@created", request.CreatedAt); AddDateTimeOffset(command, "@expires", request.ExpiresAt); AddInt(command, "@status", (int)request.Status);
    }
    private Task AuditAsync(string operation, string outcome, AccessContext actor, ToolApprovalRequest request, string code, CancellationToken ct) =>
        traceSink.WriteAsync($"tool-approval-{request.Id}", [new TraceStep(operation,outcome,timeProvider.GetUtcNow(),new Dictionary<string,object?>
        {{"approvalId",request.Id},{"tenant",actor.TenantId},{"actor",actor.SubjectId},{"requester",request.RequesterSubjectId},{"tool",request.ToolName},{"status",request.Status.ToString()},{"code",code}})], ct);
    private async Task<SqlConnection> OpenAsync(CancellationToken ct) { var connection = new SqlConnection(sqlOptions.ConnectionString); await connection.OpenAsync(ct); return connection; }
    private void ValidateConfiguration() { if (approvalOptions.ApprovalLifetime <= TimeSpan.Zero || approvalOptions.MaximumEntries <= 0 || approvalOptions.MaximumArgumentBytes <= 0 || approvalOptions.ApproverGroups.Count == 0 || string.IsNullOrWhiteSpace(sqlOptions.ConnectionString)) throw new InvalidOperationException("SQL Server 工具审批配置无效。"); }
    private static JsonElement NormalizeArguments(JsonElement value) { var normalized = value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>()) : value.Clone(); return normalized.ValueKind == JsonValueKind.Object ? normalized : throw Failure("TOOL_ARGUMENTS_MUST_BE_OBJECT", "工具参数必须是 JSON 对象。", ToolApprovalErrorKind.Validation); }
    private static void ValidateAccess(AccessContext access) { if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId)) throw Failure("TOOL_APPROVAL_IDENTITY_INVALID", "缺少有效租户或用户身份。", ToolApprovalErrorKind.Validation); }
    private static string RequiredText(string? value, int max, string code, string message) { var normalized = value?.Trim(); return !string.IsNullOrWhiteSpace(normalized) && normalized.Length <= max ? normalized : throw Failure(code, message, ToolApprovalErrorKind.Validation); }
    private static ToolApprovalException Failure(string code, string message, ToolApprovalErrorKind kind) => new(code, message, kind);
    private static SafetyDecision Refuse(string code, string message) => new(SafetyAction.Refuse, code, message);
    private static string ValidateTableName(string value) => value.All(c => char.IsLetterOrDigit(c) || c == '_') ? value : throw new InvalidOperationException("SQL Server 表名无效。");
    private static void AddString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;
    private static void AddAnsiString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.Char, size).Value = value;
    private static void AddInt(SqlCommand command, string name, int value) => command.Parameters.Add(name, SqlDbType.Int).Value = value;
    private static void AddDateTimeOffset(SqlCommand command, string name, DateTimeOffset value) => command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;

    /// <summary>释放仅用于一次性架构初始化的同步资源。</summary>
    public void Dispose() => _initializationGate.Dispose();
}
