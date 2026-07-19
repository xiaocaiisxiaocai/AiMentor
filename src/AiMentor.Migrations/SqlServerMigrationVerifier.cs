using System.Data;
using Microsoft.Data.SqlClient;

namespace AiMentor.Migrations;

public sealed record MigrationVerificationResult(int DiscoveredCount, int AppliedCount);

public sealed class SqlServerMigrationVerifier
{
    private readonly string _connectionString;
    private readonly int _commandTimeoutSeconds;

    public SqlServerMigrationVerifier(string connectionString, int commandTimeoutSeconds = 30)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new MigrationException("MIGRATION_CONNECTION_MISSING", "必须提供工作流 SQL Server 连接串。");
        if (commandTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds), "验证命令超时必须大于零。");

        _connectionString = connectionString;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public async Task<MigrationVerificationResult> VerifyAsync(
        IReadOnlyList<MigrationScript> scripts,
        CancellationToken cancellationToken = default)
    {
        MigrationLedgerValidator.ValidateScripts(scripts);
        await using var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            if (!await LedgerExistsAsync(connection, cancellationToken))
                throw new MigrationException("MIGRATION_LEDGER_MISSING", "数据库迁移账本不存在。");

            var applied = await ReadLedgerAsync(connection, cancellationToken);
            var appliedCount = MigrationLedgerValidator.Validate(scripts, applied);
            if (appliedCount != scripts.Count)
                throw new MigrationException("MIGRATION_LEDGER_BEHIND", "数据库迁移账本落后于当前发布包。");
            await VerifySchemaContractAsync(connection, cancellationToken);

            return new MigrationVerificationResult(scripts.Count, appliedCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 探针只暴露稳定状态码，驱动错误和连接信息保留在内部异常链中。
            throw new MigrationException("MIGRATION_LEDGER_UNREADABLE", "无法只读验证迁移账本。", exception);
        }
    }

    private async Task<bool> LedgerExistsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _commandTimeoutSeconds;
        command.CommandText = """
            SELECT CASE WHEN OBJECT_ID(N'dbo.AiMentorSchemaMigrations', N'U') IS NULL
                THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END;
            """;
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<AppliedMigration>> ReadLedgerAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _commandTimeoutSeconds;
        command.CommandText = """
            SELECT MigrationVersion, MigrationName, Checksum, ReleaseId, AppliedAt
            FROM dbo.AiMentorSchemaMigrations
            ORDER BY MigrationVersion;
            """;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var applied = new List<AppliedMigration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(new AppliedMigration(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDateTimeOffset(4)));
        }

        return applied;
    }

    private async Task VerifySchemaContractAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _commandTimeoutSeconds;
        command.CommandText = """
            SELECT tableName.name, columnName.name, typeName.name, columnName.max_length,
                columnName.is_nullable, columnName.collation_name
            FROM sys.tables AS tableName
            INNER JOIN sys.schemas AS schemaName ON schemaName.schema_id = tableName.schema_id
            INNER JOIN sys.columns AS columnName ON columnName.object_id = tableName.object_id
            INNER JOIN sys.types AS typeName ON typeName.user_type_id = columnName.user_type_id
            WHERE schemaName.name = N'dbo';

            SELECT tableName.name, indexName.name, indexName.is_unique, indexName.is_primary_key,
                indexName.is_disabled, indexName.is_hypothetical, indexName.filter_definition, columnName.name,
                indexColumn.key_ordinal, indexColumn.is_descending_key
            FROM sys.indexes AS indexName
            INNER JOIN sys.tables AS tableName ON tableName.object_id = indexName.object_id
            INNER JOIN sys.schemas AS schemaName ON schemaName.schema_id = tableName.schema_id
            INNER JOIN sys.index_columns AS indexColumn
                ON indexColumn.object_id = indexName.object_id AND indexColumn.index_id = indexName.index_id
            INNER JOIN sys.columns AS columnName
                ON columnName.object_id = indexColumn.object_id AND columnName.column_id = indexColumn.column_id
            WHERE schemaName.name = N'dbo' AND indexName.name IS NOT NULL AND indexColumn.key_ordinal > 0
            ORDER BY tableName.name, indexName.name, indexColumn.key_ordinal;

            SELECT foreignKey.name, childTable.name, parentTable.name, childColumn.name, parentColumn.name,
                foreignKeyColumn.constraint_column_id, foreignKey.delete_referential_action,
                foreignKey.is_disabled, foreignKey.is_not_trusted
            FROM sys.foreign_keys AS foreignKey
            INNER JOIN sys.tables AS childTable ON childTable.object_id = foreignKey.parent_object_id
            INNER JOIN sys.schemas AS childSchema ON childSchema.schema_id = childTable.schema_id
            INNER JOIN sys.tables AS parentTable ON parentTable.object_id = foreignKey.referenced_object_id
            INNER JOIN sys.schemas AS parentSchema ON parentSchema.schema_id = parentTable.schema_id
            INNER JOIN sys.foreign_key_columns AS foreignKeyColumn
                ON foreignKeyColumn.constraint_object_id = foreignKey.object_id
            INNER JOIN sys.columns AS childColumn
                ON childColumn.object_id = foreignKeyColumn.parent_object_id
                AND childColumn.column_id = foreignKeyColumn.parent_column_id
            INNER JOIN sys.columns AS parentColumn
                ON parentColumn.object_id = foreignKeyColumn.referenced_object_id
                AND parentColumn.column_id = foreignKeyColumn.referenced_column_id
            WHERE childSchema.name = N'dbo' AND parentSchema.name = N'dbo'
            ORDER BY foreignKey.name, foreignKeyColumn.constraint_column_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var actualColumns = new Dictionary<(string Table, string Column), ActualColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            actualColumns[(reader.GetString(0), reader.GetString(1))] = new ActualColumn(
                reader.GetString(2), reader.GetInt16(3), reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }

        await reader.NextResultAsync(cancellationToken);
        var actualIndexes = new Dictionary<(string Table, string Name), ActualIndex>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!actualIndexes.TryGetValue(key, out var index))
            {
                index = new ActualIndex(
                    reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), []);
                actualIndexes.Add(key, index);
            }
            index.KeyColumns.Add(new ActualIndexColumn(reader.GetString(7), reader.GetBoolean(9)));
        }

        await reader.NextResultAsync(cancellationToken);
        var actualForeignKeys = new Dictionary<string, ActualForeignKey>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var childTable = reader.GetString(1);
            var parentTable = reader.GetString(2);
            var childColumn = reader.GetString(3);
            var parentColumn = reader.GetString(4);
            var deleteAction = reader.GetByte(6);
            var isDisabled = reader.GetBoolean(7);
            var isNotTrusted = reader.GetBoolean(8);
            if (!actualForeignKeys.TryGetValue(name, out var foreignKey))
            {
                foreignKey = new ActualForeignKey(
                    childTable, parentTable, deleteAction, isDisabled, isNotTrusted, []);
                actualForeignKeys.Add(name, foreignKey);
            }
            foreignKey.Columns.Add(new ActualForeignKeyColumn(childColumn, parentColumn));
        }

        var columnDrift = RequiredColumns.Any(expected =>
            !actualColumns.TryGetValue((expected.Table, expected.Column), out var actual)
            || !string.Equals(actual.Type, expected.Type, StringComparison.Ordinal)
            || actual.MaximumLength != expected.MaximumLength
            || actual.IsNullable != expected.IsNullable
            || expected.RequiresBinaryCollation
                && !string.Equals(actual.Collation, "Latin1_General_100_BIN2", StringComparison.Ordinal));
        var indexDrift = RequiredIndexes.Any(expected =>
            !actualIndexes.TryGetValue((expected.Table, expected.Name), out var actual)
            || actual.IsDisabled
            || actual.IsHypothetical
            || actual.IsUnique != expected.IsUnique
            || actual.IsPrimaryKey != expected.IsPrimaryKey
            || !string.Equals(NormalizeFilter(actual.Filter), NormalizeFilter(expected.Filter),
                StringComparison.Ordinal)
            || actual.KeyColumns.Count != expected.KeyColumns.Length
            || actual.KeyColumns.Where((column, index) =>
                    !string.Equals(column.Name, expected.KeyColumns[index].Name, StringComparison.Ordinal)
                    || column.Descending != expected.KeyColumns[index].Descending)
                .Any());
        var foreignKeyDrift = RequiredForeignKeys.Any(expected =>
            !actualForeignKeys.TryGetValue(expected.Name, out var actual)
            || !string.Equals(actual.ChildTable, expected.ChildTable, StringComparison.Ordinal)
            || !string.Equals(actual.ParentTable, expected.ParentTable, StringComparison.Ordinal)
            || actual.DeleteAction != expected.DeleteAction
            || actual.IsDisabled
            || actual.IsNotTrusted
            || actual.Columns.Count != expected.Columns.Length
            || actual.Columns.Where((column, index) =>
                    !string.Equals(column.ChildColumn, expected.Columns[index].ChildColumn,
                        StringComparison.Ordinal)
                    || !string.Equals(column.ParentColumn, expected.Columns[index].ParentColumn,
                        StringComparison.Ordinal))
                .Any());
        if (columnDrift || indexDrift || foreignKeyDrift)
        {
            // 账本只能证明迁移曾被登记；名称相同但落错表、键序或过滤条件被改同样不能视为健康。
            throw new MigrationException("MIGRATION_SCHEMA_DRIFT", "数据库物理架构与当前迁移契约不一致。");
        }
    }

    private static string NormalizeFilter(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Concat(value.Where(character => !char.IsWhiteSpace(character)
            && character is not '[' and not ']' and not '(' and not ')')).ToUpperInvariant();

    private sealed record RequiredColumn(string Table, string Column, string Type, short MaximumLength,
        bool IsNullable, bool RequiresBinaryCollation = false);
    private sealed record ActualColumn(string Type, short MaximumLength, bool IsNullable, string? Collation);
    private sealed record RequiredIndex(string Table, string Name, bool IsUnique,
        RequiredIndexColumn[] KeyColumns, string? Filter = null, bool IsPrimaryKey = false);
    private sealed record RequiredIndexColumn(string Name, bool Descending = false);
    private sealed record ActualIndex(bool IsUnique, bool IsPrimaryKey, bool IsDisabled, bool IsHypothetical,
        string? Filter, List<ActualIndexColumn> KeyColumns);
    private sealed record ActualIndexColumn(string Name, bool Descending);
    private sealed record RequiredForeignKey(string Name, string ChildTable, string ParentTable,
        RequiredForeignKeyColumn[] Columns, byte DeleteAction);
    private sealed record RequiredForeignKeyColumn(string ChildColumn, string ParentColumn);
    private sealed record ActualForeignKey(string ChildTable, string ParentTable, byte DeleteAction,
        bool IsDisabled, bool IsNotTrusted, List<ActualForeignKeyColumn> Columns);
    private sealed record ActualForeignKeyColumn(string ChildColumn, string ParentColumn);

    private static readonly RequiredColumn[] RequiredColumns =
    [
        new("AiMentorToolApprovals", "Id", "nvarchar", 256, false, true),
        new("AiMentorToolApprovals", "TenantId", "nvarchar", 256, false, true),
        new("AiMentorToolApprovals", "Status", "int", 4, false),
        new("AiMentorAgentRuns", "RunId", "nvarchar", 256, false, true),
        new("AiMentorAgentRuns", "PayloadCipher", "nvarchar", -1, false),
        new("AiMentorAgentRuns", "KeyVersion", "nvarchar", 128, true),
        new("AiMentorAgentRuns", "CancelRequestedAt", "datetimeoffset", 10, true),
        new("AiMentorToolExecutions", "ExecutionKey", "char", 64, false, true),
        new("AiMentorToolExecutions", "TenantId", "nvarchar", 256, true, true),
        new("AiMentorToolExecutions", "SubjectId", "nvarchar", 512, true, true),
        new("AiMentorToolExecutions", "ToolName", "nvarchar", 256, true),
        new("AiMentorToolExecutions", "RequestFingerprint", "char", 64, false),
        new("AiMentorToolReconciliations", "ExecutionKey", "char", 64, false, true),
        new("AiMentorToolCompensations", "Id", "nvarchar", 128, false, true),
        new("AiMentorToolCompensations", "SnapshotCipher", "nvarchar", -1, false),
        new("AiMentorToolCompensationReconciliations", "CompensationId", "nvarchar", 128, false, true),
        new("AiMentorAtlasIncidentRuns", "RunId", "nvarchar", 256, false, true),
        new("AiMentorAtlasIncidentRuns", "PayloadCipher", "nvarchar", -1, false),
        new("AiMentorAtlasIncidentRuns", "Version", "bigint", 8, false),
        new("AiMentorOperationsActions", "Id", "nvarchar", 128, false, true),
        new("AiMentorOperationsActions", "TenantId", "nvarchar", 256, false, true),
        new("AiMentorOperationsActions", "TargetId", "nvarchar", 256, false, true),
        new("AiMentorMemoryProposals", "KeyVersion", "nvarchar", 128, false, true),
        new("AiMentorMemories", "KeyVersion", "nvarchar", 128, false, true),
        new("AiMentorMemoryRetentionState", "Id", "tinyint", 1, false),
        new("AiMentorMemoryRetentionState", "LastCompletedAt", "datetimeoffset", 10, true),
        new("AiMentorMemoryRetentionState", "MonitoringStartedAt", "datetimeoffset", 10, false)
    ];

    private static readonly RequiredIndex[] RequiredIndexes =
    [
        new("AiMentorToolApprovals", "IX_AiMentorToolApprovals_TenantStatus", false,
            [new("TenantId"), new("Status"), new("CreatedAt", true)]),
        new("AiMentorAgentRuns", "IX_AiMentorAgentRuns_OwnerStatus", false,
            [new("TenantId"), new("SubjectId"), new("Status")]),
        new("AiMentorAgentRuns", "IX_AiMentorAgentRuns_LeaseExpiry", false,
            [new("Status"), new("LeaseExpiresAt")]),
        new("AiMentorToolExecutions", "IX_AiMentorToolExecutions_TenantStatusUpdated", false,
            [new("TenantId"), new("Status"), new("UpdatedAt", true)]),
        new("AiMentorToolReconciliations", "IX_AiMentorToolReconciliations_TenantStatusExpiry", false,
            [new("TenantId"), new("Status"), new("EvidenceExpiresAt")]),
        new("AiMentorToolCompensations", "IX_AiMentorToolCompensations_TenantStatusCreated", false,
            [new("TenantId"), new("Status"), new("CreatedAt", true)]),
        new("AiMentorToolCompensations", "UX_AiMentorToolCompensations_Approval", true,
            [new("ApprovalId")], "ApprovalId IS NOT NULL"),
        new("AiMentorToolCompensationReconciliations",
            "IX_AiMentorToolCompensationReconciliations_TenantStatusExpiry", false,
            [new("TenantId"), new("Status"), new("EvidenceExpiresAt")]),
        new("AiMentorAtlasIncidentRuns", "IX_AiMentorAtlasIncidentRuns_OwnerStatus", false,
            [new("TenantId"), new("SubjectId"), new("Status")]),
        new("AiMentorOperationsActions", "UX_AiMentorOperationsActions_Idempotency", true,
            [new("TenantId"), new("IdempotencyHash")]),
        new("AiMentorOperationsActions", "IX_AiMentorOperationsActions_Queue", false,
            [new("TenantId"), new("Status"), new("ExpiresAt")]),
        new("AiMentorMemoryProposals", "IX_AiMentorMemoryProposals_Owner", false,
            [new("TenantId"), new("SubjectId"), new("Status"), new("ApprovalExpiresAt")]),
        new("AiMentorMemories", "IX_AiMentorMemories_OwnerKey", false,
            [new("TenantId"), new("SubjectId"), new("Scope"), new("SessionId"),
                new("KeyFingerprint"), new("ExpiresAt")]),
        new("AiMentorMemoryRetentionState", "PK_AiMentorMemoryRetentionState", true,
            [new("Id")], IsPrimaryKey: true)
    ];

    private static readonly RequiredForeignKey[] RequiredForeignKeys =
    [
        new("FK_AiMentorToolReconciliations_Execution", "AiMentorToolReconciliations",
            "AiMentorToolExecutions", [new("ExecutionKey", "ExecutionKey")], 1),
        new("FK_AiMentorToolCompensationReconciliations_Compensation",
            "AiMentorToolCompensationReconciliations", "AiMentorToolCompensations",
            [new("CompensationId", "Id")], 1)
    ];
}
