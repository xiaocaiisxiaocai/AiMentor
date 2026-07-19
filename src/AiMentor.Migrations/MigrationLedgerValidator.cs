namespace AiMentor.Migrations;

public sealed record AppliedMigration(
    int Version,
    string FileName,
    string Sha256,
    string ReleaseId,
    DateTimeOffset AppliedAt);

public static class MigrationLedgerValidator
{
    public static void ValidateScripts(IReadOnlyList<MigrationScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        if (scripts.Count == 0)
            throw new MigrationException("MIGRATION_SET_EMPTY", "没有可验证的迁移脚本。");

        var duplicate = scripts.GroupBy(script => script.Version).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new MigrationException("MIGRATION_VERSION_DUPLICATE", $"迁移版本 {duplicate.Key:000} 不唯一。");

        for (var index = 0; index < scripts.Count; index++)
        {
            if (scripts[index].Version != index + 1)
                throw new MigrationException("MIGRATION_VERSION_GAP", "迁移脚本不是从 001 开始的连续有序集合。");
        }
    }

    public static int Validate(
        IReadOnlyList<MigrationScript> scripts,
        IReadOnlyList<AppliedMigration> appliedMigrations)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(appliedMigrations);
        ValidateScripts(scripts);

        var ordered = appliedMigrations.OrderBy(item => item.Version).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var applied = ordered[index];
            var expectedVersion = index + 1;
            if (applied.Version > scripts.Count)
                throw new MigrationException("MIGRATION_LEDGER_AHEAD", "数据库迁移账本超前于当前发布包。");
            if (applied.Version != expectedVersion)
                throw new MigrationException("MIGRATION_LEDGER_GAP", $"数据库迁移账本缺少版本 {expectedVersion:000}。");

            var script = scripts[index];
            if (!string.Equals(applied.FileName, script.FileName, StringComparison.Ordinal))
                throw new MigrationException("MIGRATION_NAME_MISMATCH", $"迁移 {applied.Version:000} 的名称与账本不一致。");
            if (!string.Equals(applied.Sha256, script.Sha256, StringComparison.Ordinal))
                throw new MigrationException("MIGRATION_CHECKSUM_MISMATCH", $"迁移 {applied.Version:000} 的校验和与账本不一致。");
        }

        return ordered.Length;
    }
}
