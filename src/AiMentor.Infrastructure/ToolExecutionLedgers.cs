using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text.Json;

namespace AiMentor.Infrastructure;

/// <summary>提供与 SQL 账本相同状态机的进程内实现，用于开发和快速回归测试。</summary>
public sealed class InMemoryToolExecutionLedger(TimeProvider timeProvider) : IToolExecutionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReviewCase> _reviews = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IdempotencyAcquireResult> TryAcquireAsync(ToolExecutionLedgerRequest request,
        TimeSpan leaseDuration, TimeSpan retention, int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var stale in _entries.Where(pair => pair.Value.Status == LedgerStatus.Completed
                    && pair.Value.UpdatedAt.Add(retention) <= now).ToArray())
                _entries.Remove(stale.Key);
            if (!_entries.TryGetValue(request.ExecutionKey, out var entry))
            {
                if (_entries.Count >= maximumEntries)
                    return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Capacity));
                var token = Guid.NewGuid().ToString("N");
                _entries.Add(request.ExecutionKey, new Entry(request.RequestFingerprint, request.RunId,
                    request.TenantId, request.SubjectId, request.ToolName, LedgerStatus.Reserved, token,
                    now.Add(leaseDuration), now, now));
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, token));
            }
            if (!string.Equals(entry.RequestFingerprint, request.RequestFingerprint, StringComparison.Ordinal))
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.FingerprintMismatch));
            if (entry.Status == LedgerStatus.Completed)
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Replay,
                    ReplayResult: entry.Result));
            if (entry.Status == LedgerStatus.OutcomeUnknown)
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown));
            if (entry.Status == LedgerStatus.ReconciledApplied)
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.ReconciledApplied));
            if (entry.Status == LedgerStatus.RetryAuthorized)
            {
                var retryToken = Guid.NewGuid().ToString("N");
                _entries[request.ExecutionKey] = entry with
                {
                    Status = LedgerStatus.RetryReserved,
                    RunId = request.RunId,
                    LeaseToken = retryToken,
                    LeaseExpiresAt = now.Add(leaseDuration),
                    UpdatedAt = now
                };
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, retryToken));
            }
            if (entry.LeaseExpiresAt > now)
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.InProgress));
            if (entry.Status == LedgerStatus.Executing)
            {
                _entries[request.ExecutionKey] = entry with { Status = LedgerStatus.OutcomeUnknown, UpdatedAt = now };
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown));
            }
            var renewedToken = Guid.NewGuid().ToString("N");
            _entries[request.ExecutionKey] = entry with
            {
                RunId = request.RunId,
                LeaseToken = renewedToken,
                LeaseExpiresAt = now.Add(leaseDuration),
                UpdatedAt = now
            };
            return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, renewedToken));
        }
    }

    /// <inheritdoc />
    public Task MarkExecutingAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default) => MarkExecutingCoreAsync(executionKey, leaseToken,
            cancellationToken);

    /// <inheritdoc />
    public Task CompleteAsync(string executionKey, string leaseToken, ToolExecutionResult result,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, LedgerStatus.Executing, LedgerStatus.Completed, result, cancellationToken);

    /// <inheritdoc />
    public Task MarkOutcomeUnknownAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, LedgerStatus.Executing, LedgerStatus.OutcomeUnknown, null,
            cancellationToken);

    /// <inheritdoc />
    public Task AbandonAsync(string executionKey, string leaseToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_entries.TryGetValue(executionKey, out var entry) && FixedEquals(entry.LeaseToken, leaseToken))
            {
                if (entry.Status == LedgerStatus.Reserved) _entries.Remove(executionKey);
                else if (entry.Status == LedgerStatus.RetryReserved)
                    _entries[executionKey] = entry with
                    {
                        Status = LedgerStatus.RetryAuthorized,
                        UpdatedAt = timeProvider.GetUtcNow()
                    };
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(string tenantId, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            IReadOnlyList<OutcomeUnknownToolExecution> result = _entries
                .Where(pair => pair.Value.Status == LedgerStatus.OutcomeUnknown
                    && string.Equals(pair.Value.TenantId, tenantId, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Value.UpdatedAt)
                .Take(limit)
                .Select(pair => new OutcomeUnknownToolExecution(pair.Key, pair.Value.TenantId,
                    pair.Value.SubjectId, pair.Value.ToolName, pair.Value.RunId,
                    pair.Value.CreatedAt, pair.Value.UpdatedAt))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<OutcomeUnknownToolExecutionDetail?> GetOutcomeUnknownAsync(string tenantId, string executionKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionKey);
        lock (_gate)
        {
            if (!_entries.TryGetValue(executionKey, out var entry)
                || entry.Status != LedgerStatus.OutcomeUnknown
                || !string.Equals(entry.TenantId, tenantId, StringComparison.Ordinal))
                return Task.FromResult<OutcomeUnknownToolExecutionDetail?>(null);
            var summary = new OutcomeUnknownToolExecution(executionKey, entry.TenantId, entry.SubjectId,
                entry.ToolName, entry.RunId, entry.CreatedAt, entry.UpdatedAt);
            return Task.FromResult<OutcomeUnknownToolExecutionDetail?>(
                new OutcomeUnknownToolExecutionDetail(summary, entry.RequestFingerprint));
        }
    }

    /// <inheritdoc />
    public Task<ToolReconciliationReviewResult> SubmitReconciliationReviewAsync(ToolReconciliationReview review,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (!_entries.TryGetValue(review.ExecutionKey, out var entry)
                || entry.Status != LedgerStatus.OutcomeUnknown
                || !string.Equals(entry.TenantId, review.TenantId, StringComparison.Ordinal))
                return Task.FromResult(ReviewResult(review, ToolReconciliationReviewStatus.NotFound));
            _reviews.TryGetValue(review.ExecutionKey, out var current);
            if (current is null || current.Status != ReviewCaseStatus.AwaitingSecond || current.EvidenceExpiresAt <= now)
            {
                if (review.EvidenceExpiresAt <= now)
                    return Task.FromResult(ReviewResult(review, ToolReconciliationReviewStatus.EvidenceExpired));
                if (!review.Confirmed)
                {
                    _reviews[review.ExecutionKey] = ReviewCase.FromFirst(review, ReviewCaseStatus.Rejected, now);
                    return Task.FromResult(ReviewResult(review, ToolReconciliationReviewStatus.Rejected));
                }
                _reviews[review.ExecutionKey] = ReviewCase.FromFirst(review, ReviewCaseStatus.AwaitingSecond, now);
                return Task.FromResult(ReviewResult(review,
                    ToolReconciliationReviewStatus.AwaitingSecondReviewer));
            }
            if (string.Equals(current.FirstReviewerSubjectId, review.ReviewerSubjectId, StringComparison.Ordinal))
                return Task.FromResult(ReviewResult(review, ToolReconciliationReviewStatus.ReviewerMustDiffer));
            if (current.EvidenceExpiresAt <= now || review.EvidenceObservedAt > current.EvidenceExpiresAt)
                return Task.FromResult(ReviewResult(review, ToolReconciliationReviewStatus.EvidenceExpired));
            if (current.EvidenceState != review.EvidenceState
                || !string.Equals(current.EvidenceCode, review.EvidenceCode, StringComparison.Ordinal))
                return Task.FromResult(ReviewResult(review, ToolReconciliationReviewStatus.EvidenceChanged));
            if (!review.Confirmed)
            {
                _reviews[review.ExecutionKey] = current.Complete(review, ReviewCaseStatus.Rejected, now);
                return Task.FromResult(ReviewResult(review, ToolReconciliationReviewStatus.Rejected));
            }
            var status = review.EvidenceState == ToolOutcomeProbeState.Applied
                ? ToolReconciliationReviewStatus.ResolvedApplied
                : ToolReconciliationReviewStatus.RetryAuthorized;
            var nextLedger = review.EvidenceState == ToolOutcomeProbeState.Applied
                ? LedgerStatus.ReconciledApplied
                : LedgerStatus.RetryAuthorized;
            _entries[review.ExecutionKey] = entry with { Status = nextLedger, UpdatedAt = now };
            _reviews[review.ExecutionKey] = current.Complete(review, ReviewCaseStatus.Resolved, now);
            return Task.FromResult(ReviewResult(review, status));
        }
    }

    private Task MarkExecutingCoreAsync(string executionKey, string leaseToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(executionKey, out var entry)
                || entry.Status is not (LedgerStatus.Reserved or LedgerStatus.RetryReserved)
                || !FixedEquals(entry.LeaseToken, leaseToken))
                throw new InvalidOperationException("幂等执行账本状态或租约不匹配。");
            _entries[executionKey] = entry with
            {
                Status = LedgerStatus.Executing,
                UpdatedAt = timeProvider.GetUtcNow()
            };
        }
        return Task.CompletedTask;
    }

    private static ToolReconciliationReviewResult ReviewResult(ToolReconciliationReview review,
        ToolReconciliationReviewStatus status) => new(review.ExecutionKey, status, review.EvidenceState,
            review.EvidenceExpiresAt);

    private Task TransitionAsync(string executionKey, string leaseToken, LedgerStatus expected, LedgerStatus next,
        ToolExecutionResult? result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(executionKey, out var entry) || entry.Status != expected
                || !FixedEquals(entry.LeaseToken, leaseToken))
                throw new InvalidOperationException("幂等执行账本状态或租约不匹配。");
            _entries[executionKey] = entry with { Status = next, Result = result, UpdatedAt = timeProvider.GetUtcNow() };
        }
        return Task.CompletedTask;
    }

    private static bool FixedEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    private static void ValidateRequest(ToolExecutionLedgerRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolName);
    }

    private enum LedgerStatus
    {
        Reserved, Executing, Completed, OutcomeUnknown, RetryAuthorized, RetryReserved, ReconciledApplied
    }

    private enum ReviewCaseStatus { AwaitingSecond, Rejected, Resolved }

    private sealed record ReviewCase(ToolOutcomeProbeState EvidenceState, string EvidenceCode,
        DateTimeOffset EvidenceExpiresAt, string FirstReviewerSubjectId, string FirstReasonHash,
        ReviewCaseStatus Status, string? SecondReviewerSubjectId, string? SecondReasonHash,
        DateTimeOffset UpdatedAt)
    {
        public static ReviewCase FromFirst(ToolReconciliationReview review, ReviewCaseStatus status,
            DateTimeOffset now) => new(review.EvidenceState, review.EvidenceCode, review.EvidenceExpiresAt,
                review.ReviewerSubjectId, review.ReasonHash, status, null, null, now);
        public ReviewCase Complete(ToolReconciliationReview review, ReviewCaseStatus status, DateTimeOffset now) =>
            this with
            {
                Status = status,
                SecondReviewerSubjectId = review.ReviewerSubjectId,
                SecondReasonHash = review.ReasonHash,
                UpdatedAt = now
            };
    }

    private sealed record Entry(string RequestFingerprint, string RunId, string TenantId, string SubjectId,
        string ToolName, LedgerStatus Status, string LeaseToken, DateTimeOffset LeaseExpiresAt,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ToolExecutionResult? Result = null);
}

/// <summary>
/// 使用 SQL Server 条件事务持久化幂等占位和加密结果；执行租约过期后把已开始副作用的记录冻结为结果不确定。
/// </summary>
public sealed class SqlServerToolExecutionLedger(
    SqlServerWorkflowOptions options,
    IWorkflowStateCipher cipher,
    TimeProvider timeProvider) : IToolExecutionLedger, IDisposable
{
    private const string TableName = "AiMentorToolExecutions";
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    /// <inheritdoc />
    public async Task<IdempotencyAcquireResult> TryAcquireAsync(ToolExecutionLedgerRequest request,
        TimeSpan leaseDuration, TimeSpan retention, int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = $"DELETE FROM dbo.{TableName} WHERE Status=2 AND UpdatedAt<=@cutoff;";
            AddDateTimeOffset(prune, "@cutoff", now.Subtract(retention));
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        var entry = await ReadForUpdateAsync(connection, transaction, request.ExecutionKey, cancellationToken);
        if (entry is null)
        {
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK);";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
                >= maximumEntries)
                return await CommitAsync(transaction, new IdempotencyAcquireResult(IdempotencyAcquireStatus.Capacity),
                    cancellationToken);
            var token = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT dbo.{TableName}
                  (ExecutionKey,RequestFingerprint,RunId,TenantId,SubjectId,ToolName,Status,LeaseToken,LeaseExpiresAt,CreatedAt,UpdatedAt)
                VALUES (@key,@fingerprint,@runId,@tenantId,@subjectId,@toolName,0,@token,@leaseExpires,@now,@now);
                """;
            AddAnsiString(insert, "@key", 64, request.ExecutionKey);
            AddAnsiString(insert, "@fingerprint", 64, request.RequestFingerprint);
            AddString(insert, "@runId", 128, request.RunId); AddString(insert, "@tenantId", 128, request.TenantId);
            AddString(insert, "@subjectId", 256, request.SubjectId); AddString(insert, "@toolName", 128, request.ToolName);
            AddString(insert, "@token", 64, token);
            AddDateTimeOffset(insert, "@leaseExpires", now.Add(leaseDuration)); AddDateTimeOffset(insert, "@now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, token), cancellationToken);
        }
        if (!string.Equals(entry.RequestFingerprint, request.RequestFingerprint, StringComparison.Ordinal))
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.FingerprintMismatch), cancellationToken);
        if (entry.Status == 2)
        {
            if (entry.ResultCipher is null)
                throw new InvalidOperationException("已完成的幂等记录缺少结果密文。");
            var json = cipher.Unprotect(entry.KeyVersion, entry.ResultCipher, Context(request.ExecutionKey));
            var result = JsonSerializer.Deserialize<ToolExecutionResult>(json)
                ?? throw new InvalidOperationException("幂等执行结果密文无效。");
            if (cipher.RequiresReencryption(entry.KeyVersion))
            {
                var reencrypted = cipher.Protect(json, Context(request.ExecutionKey));
                await using var rotate = connection.CreateCommand();
                rotate.Transaction = transaction;
                rotate.CommandText = $"UPDATE dbo.{TableName} SET KeyVersion=@version,ResultCipher=@result,UpdatedAt=@now WHERE ExecutionKey=@key AND Status=2;";
                AddString(rotate, "@version", 64, reencrypted.KeyVersion);
                AddString(rotate, "@result", -1, reencrypted.Ciphertext);
                AddDateTimeOffset(rotate, "@now", now); AddAnsiString(rotate, "@key", 64, request.ExecutionKey);
                await rotate.ExecuteNonQueryAsync(cancellationToken);
            }
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.Replay, ReplayResult: result), cancellationToken);
        }
        if (entry.Status == 3)
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown), cancellationToken);
        if (entry.Status == 6)
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.ReconciledApplied), cancellationToken);
        if (entry.Status == 4)
        {
            var retryToken = Guid.NewGuid().ToString("N");
            await using var retry = connection.CreateCommand();
            retry.Transaction = transaction;
            retry.CommandText = $"""
                UPDATE dbo.{TableName} SET Status=5,RunId=@runId,LeaseToken=@token,
                    LeaseExpiresAt=@expires,UpdatedAt=@now WHERE ExecutionKey=@key AND Status=4;
                """;
            AddString(retry, "@runId", 128, request.RunId); AddString(retry, "@token", 64, retryToken);
            AddDateTimeOffset(retry, "@expires", now.Add(leaseDuration)); AddDateTimeOffset(retry, "@now", now);
            AddAnsiString(retry, "@key", 64, request.ExecutionKey); await retry.ExecuteNonQueryAsync(cancellationToken);
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, retryToken), cancellationToken);
        }
        if (entry.LeaseExpiresAt > now)
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.InProgress), cancellationToken);
        if (entry.Status == 1)
        {
            await UpdateStatusAsync(connection, transaction, request.ExecutionKey, entry.LeaseToken, 1, 3, now,
                cancellationToken);
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown), cancellationToken);
        }
        var renewed = Guid.NewGuid().ToString("N");
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"UPDATE dbo.{TableName} SET RunId=@runId,LeaseToken=@token,LeaseExpiresAt=@expires,UpdatedAt=@now WHERE ExecutionKey=@key AND Status IN (0,5);";
            AddString(update, "@runId", 128, request.RunId); AddString(update, "@token", 64, renewed);
            AddDateTimeOffset(update, "@expires", now.Add(leaseDuration)); AddDateTimeOffset(update, "@now", now);
            AddAnsiString(update, "@key", 64, request.ExecutionKey); await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return await CommitAsync(transaction,
            new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, renewed), cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkExecutingAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default) => MarkExecutingCoreAsync(executionKey, leaseToken,
            cancellationToken);

    /// <inheritdoc />
    public Task CompleteAsync(string executionKey, string leaseToken, ToolExecutionResult result,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, 1, 2, result, cancellationToken);

    /// <inheritdoc />
    public Task MarkOutcomeUnknownAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, 1, 3, null, cancellationToken);

    /// <inheritdoc />
    public async Task AbandonAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM dbo.{TableName} WHERE ExecutionKey=@key AND Status=0 AND LeaseToken=@token;
            UPDATE dbo.{TableName} SET Status=4,UpdatedAt=@now
              WHERE ExecutionKey=@key AND Status=5 AND LeaseToken=@token;
            """;
        AddAnsiString(command, "@key", 64, executionKey); AddString(command, "@token", 64, leaseToken);
        AddDateTimeOffset(command, "@now", timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkExecutingCoreAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=1,UpdatedAt=@now
            WHERE ExecutionKey=@key AND Status IN (0,5) AND LeaseToken=@token;
            """;
        AddDateTimeOffset(command, "@now", timeProvider.GetUtcNow());
        AddAnsiString(command, "@key", 64, executionKey); AddString(command, "@token", 64, leaseToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("幂等执行账本状态或租约不匹配。");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(string tenantId, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (@limit) ExecutionKey,TenantId,SubjectId,ToolName,RunId,CreatedAt,UpdatedAt
            FROM dbo.{TableName}
            WHERE TenantId COLLATE Latin1_General_100_BIN2=@tenantId AND Status=3
            ORDER BY UpdatedAt DESC;
            """;
        AddInt(command, "@limit", limit);
        AddString(command, "@tenantId", 128, tenantId);
        var records = new List<OutcomeUnknownToolExecution>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new OutcomeUnknownToolExecution(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        return records;
    }

    /// <inheritdoc />
    public async Task<OutcomeUnknownToolExecutionDetail?> GetOutcomeUnknownAsync(string tenantId,
        string executionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionKey);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ExecutionKey,TenantId,SubjectId,ToolName,RunId,CreatedAt,UpdatedAt,RequestFingerprint
            FROM dbo.{TableName}
            WHERE ExecutionKey COLLATE Latin1_General_100_BIN2=@key
              AND TenantId COLLATE Latin1_General_100_BIN2=@tenantId AND Status=3;
            """;
        AddAnsiString(command, "@key", 64, executionKey);
        AddString(command, "@tenantId", 128, tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var summary = new OutcomeUnknownToolExecution(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6));
        return new OutcomeUnknownToolExecutionDetail(summary, reader.GetString(7));
    }

    /// <inheritdoc />
    public async Task<ToolReconciliationReviewResult> SubmitReconciliationReviewAsync(
        ToolReconciliationReview review, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await using (var ledger = connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText = $"SELECT Status FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE ExecutionKey COLLATE Latin1_General_100_BIN2=@key " +
                "AND TenantId COLLATE Latin1_General_100_BIN2=@tenant;";
            AddAnsiString(ledger, "@key", 64, review.ExecutionKey); AddString(ledger, "@tenant", 128, review.TenantId);
            var statusValue = await ledger.ExecuteScalarAsync(cancellationToken);
            if (statusValue is null || Convert.ToByte(statusValue, CultureInfo.InvariantCulture) != 3)
                return await CommitReviewAsync(transaction, review, ToolReconciliationReviewStatus.NotFound,
                    cancellationToken);
        }

        ReviewRow? current;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT EvidenceState,EvidenceCode,EvidenceExpiresAt,FirstReviewerSubjectId,Status
                FROM dbo.AiMentorToolReconciliations WITH (UPDLOCK,HOLDLOCK) WHERE ExecutionKey=@key;
                """;
            AddAnsiString(read, "@key", 64, review.ExecutionKey);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            current = await reader.ReadAsync(cancellationToken)
                ? new ReviewRow((ToolOutcomeProbeState)reader.GetByte(0), reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3), reader.GetByte(4))
                : null;
        }

        if (current is null || current.Status != 0 || current.EvidenceExpiresAt <= now)
        {
            if (review.EvidenceExpiresAt <= now)
                return await CommitReviewAsync(transaction, review, ToolReconciliationReviewStatus.EvidenceExpired,
                    cancellationToken);
            await using var replace = connection.CreateCommand();
            replace.Transaction = transaction;
            replace.CommandText = """
                DELETE dbo.AiMentorToolReconciliations WHERE ExecutionKey=@key;
                INSERT dbo.AiMentorToolReconciliations
                  (ExecutionKey,TenantId,EvidenceState,EvidenceCode,EvidenceObservedAt,EvidenceExpiresAt,
                   FirstReviewerSubjectId,FirstConfirmed,FirstReasonHash,FirstReviewedAt,Status,UpdatedAt)
                VALUES(@key,@tenant,@state,@code,@observed,@expires,@reviewer,@confirmed,@reason,@now,@status,@now);
                """;
            AddAnsiString(replace, "@key", 64, review.ExecutionKey);
            AddString(replace, "@tenant", 128, review.TenantId); AddInt(replace, "@state", (int)review.EvidenceState);
            AddString(replace, "@code", 128, review.EvidenceCode);
            AddDateTimeOffset(replace, "@observed", review.EvidenceObservedAt);
            AddDateTimeOffset(replace, "@expires", review.EvidenceExpiresAt);
            AddString(replace, "@reviewer", 256, review.ReviewerSubjectId);
            AddBool(replace, "@confirmed", review.Confirmed); AddAnsiString(replace, "@reason", 64, review.ReasonHash);
            AddDateTimeOffset(replace, "@now", now); AddInt(replace, "@status", review.Confirmed ? 0 : 1);
            await replace.ExecuteNonQueryAsync(cancellationToken);
            return await CommitReviewAsync(transaction, review, review.Confirmed
                ? ToolReconciliationReviewStatus.AwaitingSecondReviewer
                : ToolReconciliationReviewStatus.Rejected, cancellationToken);
        }
        if (string.Equals(current.FirstReviewerSubjectId, review.ReviewerSubjectId, StringComparison.Ordinal))
            return await CommitReviewAsync(transaction, review, ToolReconciliationReviewStatus.ReviewerMustDiffer,
                cancellationToken);
        if (current.EvidenceExpiresAt <= now || review.EvidenceObservedAt > current.EvidenceExpiresAt)
            return await CommitReviewAsync(transaction, review, ToolReconciliationReviewStatus.EvidenceExpired,
                cancellationToken);
        if (current.EvidenceState != review.EvidenceState
            || !string.Equals(current.EvidenceCode, review.EvidenceCode, StringComparison.Ordinal))
            return await CommitReviewAsync(transaction, review, ToolReconciliationReviewStatus.EvidenceChanged,
                cancellationToken);

        var resultStatus = !review.Confirmed ? ToolReconciliationReviewStatus.Rejected
            : review.EvidenceState == ToolOutcomeProbeState.Applied
                ? ToolReconciliationReviewStatus.ResolvedApplied
                : ToolReconciliationReviewStatus.RetryAuthorized;
        await using (var finish = connection.CreateCommand())
        {
            finish.Transaction = transaction;
            finish.CommandText = """
                UPDATE dbo.AiMentorToolReconciliations SET SecondReviewerSubjectId=@reviewer,
                  SecondConfirmed=@confirmed,SecondReasonHash=@reason,SecondReviewedAt=@now,
                  Status=@status,UpdatedAt=@now WHERE ExecutionKey=@key AND Status=0;
                """;
            AddString(finish, "@reviewer", 256, review.ReviewerSubjectId);
            AddBool(finish, "@confirmed", review.Confirmed); AddAnsiString(finish, "@reason", 64, review.ReasonHash);
            AddDateTimeOffset(finish, "@now", now); AddInt(finish, "@status", review.Confirmed ? 2 : 1);
            AddAnsiString(finish, "@key", 64, review.ExecutionKey); await finish.ExecuteNonQueryAsync(cancellationToken);
        }
        if (review.Confirmed)
        {
            await using var resolve = connection.CreateCommand(); resolve.Transaction = transaction;
            resolve.CommandText = $"UPDATE dbo.{TableName} SET Status=@status,UpdatedAt=@now WHERE ExecutionKey=@key AND Status=3;";
            AddInt(resolve, "@status", review.EvidenceState == ToolOutcomeProbeState.Applied ? 6 : 4);
            AddDateTimeOffset(resolve, "@now", now); AddAnsiString(resolve, "@key", 64, review.ExecutionKey);
            if (await resolve.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("工具执行账本裁决状态发生并发变化。");
        }
        return await CommitReviewAsync(transaction, review, resultStatus, cancellationToken);
    }

    private static async Task<ToolReconciliationReviewResult> CommitReviewAsync(SqlTransaction transaction,
        ToolReconciliationReview review, ToolReconciliationReviewStatus status, CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        return new ToolReconciliationReviewResult(review.ExecutionKey, status, review.EvidenceState,
            review.EvidenceExpiresAt);
    }

    private async Task TransitionAsync(string executionKey, string leaseToken, int expected, int next,
        ToolExecutionResult? result, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var protectedResult = result is null ? null : cipher.Protect(JsonSerializer.Serialize(result), Context(executionKey));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@next,KeyVersion=@version,ResultCipher=@result,UpdatedAt=@now
            WHERE ExecutionKey=@key AND Status=@expected AND LeaseToken=@token;
            """;
        AddInt(command, "@next", next); AddNullableString(command, "@version", 64, protectedResult?.KeyVersion);
        AddNullableString(command, "@result", -1, protectedResult?.Ciphertext);
        AddDateTimeOffset(command, "@now", timeProvider.GetUtcNow()); AddAnsiString(command, "@key", 64, executionKey);
        AddInt(command, "@expected", expected); AddString(command, "@token", 64, leaseToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("幂等执行账本状态或租约不匹配。");
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (!options.InitializeSchema) { _initialized = true; return; }
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF OBJECT_ID(N'dbo.{TableName}',N'U') IS NULL
                BEGIN
                  CREATE TABLE dbo.{TableName}(
                    ExecutionKey char(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                      CONSTRAINT PK_{TableName} PRIMARY KEY,
                    RequestFingerprint char(64) NOT NULL,
                    RunId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,Status tinyint NOT NULL,
                    TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
                    SubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,ToolName nvarchar(128) NULL,
                    LeaseToken nvarchar(64) NOT NULL,LeaseExpiresAt datetimeoffset(7) NOT NULL,
                    KeyVersion nvarchar(64) NULL,ResultCipher nvarchar(max) NULL,
                    CreatedAt datetimeoffset(7) NOT NULL,UpdatedAt datetimeoffset(7) NOT NULL,RowVersion rowversion NOT NULL);
                  CREATE INDEX IX_{TableName}_StatusUpdated ON dbo.{TableName}(Status,UpdatedAt);
                END;
                IF COL_LENGTH(N'dbo.{TableName}',N'TenantId') IS NULL
                    ALTER TABLE dbo.{TableName} ADD TenantId nvarchar(128)
                      COLLATE Latin1_General_100_BIN2 NULL;
                IF COL_LENGTH(N'dbo.{TableName}',N'SubjectId') IS NULL
                    ALTER TABLE dbo.{TableName} ADD SubjectId nvarchar(256)
                      COLLATE Latin1_General_100_BIN2 NULL;
                IF COL_LENGTH(N'dbo.{TableName}',N'ToolName') IS NULL
                    ALTER TABLE dbo.{TableName} ADD ToolName nvarchar(128) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.{TableName}')
                    AND name=N'IX_{TableName}_TenantStatusUpdated')
                    CREATE INDEX IX_{TableName}_TenantStatusUpdated
                        ON dbo.{TableName}(TenantId,Status,UpdatedAt DESC);
                IF OBJECT_ID(N'dbo.AiMentorToolReconciliations',N'U') IS NULL
                BEGIN
                  CREATE TABLE dbo.AiMentorToolReconciliations(
                    ExecutionKey char(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                      CONSTRAINT PK_AiMentorToolReconciliations PRIMARY KEY,
                    TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,EvidenceState tinyint NOT NULL,
                    EvidenceCode nvarchar(128) NOT NULL,EvidenceObservedAt datetimeoffset(7) NOT NULL,
                    EvidenceExpiresAt datetimeoffset(7) NOT NULL,
                    FirstReviewerSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    FirstConfirmed bit NOT NULL,FirstReasonHash char(64) NOT NULL,
                    FirstReviewedAt datetimeoffset(7) NOT NULL,
                    SecondReviewerSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
                    SecondConfirmed bit NULL,SecondReasonHash char(64) NULL,SecondReviewedAt datetimeoffset(7) NULL,
                    Status tinyint NOT NULL,UpdatedAt datetimeoffset(7) NOT NULL,RowVersion rowversion NOT NULL,
                    CONSTRAINT FK_AiMentorToolReconciliations_Execution FOREIGN KEY(ExecutionKey)
                      REFERENCES dbo.{TableName}(ExecutionKey) ON DELETE CASCADE);
                  CREATE INDEX IX_AiMentorToolReconciliations_TenantStatusExpiry
                    ON dbo.AiMentorToolReconciliations(TenantId,Status,EvidenceExpiresAt);
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken); _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private static async Task<Entry?> ReadForUpdateAsync(SqlConnection connection, SqlTransaction transaction,
        string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT RequestFingerprint,Status,LeaseToken,LeaseExpiresAt,KeyVersion,ResultCipher FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) WHERE ExecutionKey=@key;";
        AddAnsiString(command, "@key", 64, key); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new Entry(reader.GetString(0), reader.GetByte(1), reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static async Task UpdateStatusAsync(SqlConnection connection, SqlTransaction transaction, string key,
        string token, int expected, int next, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"UPDATE dbo.{TableName} SET Status=@next,UpdatedAt=@now WHERE ExecutionKey=@key AND Status=@expected AND LeaseToken=@token;";
        AddInt(command, "@next", next); AddDateTimeOffset(command, "@now", now); AddAnsiString(command, "@key", 64, key);
        AddInt(command, "@expected", expected); AddString(command, "@token", 64, token);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IdempotencyAcquireResult> CommitAsync(SqlTransaction transaction,
        IdempotencyAcquireResult result, CancellationToken cancellationToken)
    { await transaction.CommitAsync(cancellationToken); return result; }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    { var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync(cancellationToken); return connection; }
    private static string Context(string key) => $"tool-execution:{key}";
    private static void ValidateRequest(ToolExecutionLedgerRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolName);
    }
    private static void AddString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;
    private static void AddNullableString(SqlCommand command, string name, int size, string? value) => command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value is null ? DBNull.Value : value;
    private static void AddAnsiString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.Char, size).Value = value;
    private static void AddInt(SqlCommand command, string name, int value) => command.Parameters.Add(name, SqlDbType.Int).Value = value;
    private static void AddBool(SqlCommand command, string name, bool value) => command.Parameters.Add(name, SqlDbType.Bit).Value = value;
    private static void AddDateTimeOffset(SqlCommand command, string name, DateTimeOffset value) => command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;
    private sealed record Entry(string RequestFingerprint, byte Status, string LeaseToken,
        DateTimeOffset LeaseExpiresAt, string? KeyVersion, string? ResultCipher);
    private sealed record ReviewRow(ToolOutcomeProbeState EvidenceState, string EvidenceCode,
        DateTimeOffset EvidenceExpiresAt, string FirstReviewerSubjectId, byte Status);

    /// <summary>释放架构初始化同步资源。</summary>
    public void Dispose() => _initializationGate.Dispose();
}
