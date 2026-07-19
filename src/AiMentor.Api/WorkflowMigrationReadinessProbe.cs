using AiMentor.Application;
using AiMentor.Migrations;

namespace AiMentor.Api;

/// <summary>只读核对生产数据库迁移账本；任何漂移都以稳定代码使 readiness 失败关闭。</summary>
public sealed class WorkflowMigrationReadinessProbe
{
    private readonly SqlServerMigrationVerifier? _verifier;
    private readonly IReadOnlyList<MigrationScript> _scripts;

    public WorkflowMigrationReadinessProbe(SqlServerMigrationVerifier? verifier,
        IReadOnlyList<MigrationScript>? scripts = null)
    {
        _verifier = verifier;
        _scripts = scripts ?? [];
    }

    public async Task<SystemCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_verifier is null)
            return new SystemCheckResult("workflow.migrations", SystemCheckSeverity.Information,
                SystemCheckStatus.Passed, "MIGRATION_PROBE_NOT_REQUIRED", "当前环境不要求生产迁移账本探针。");

        try
        {
            await _verifier.VerifyAsync(_scripts, cancellationToken);
            return new SystemCheckResult("workflow.migrations", SystemCheckSeverity.Critical,
                SystemCheckStatus.Passed, "MIGRATION_LEDGER_CURRENT", "数据库迁移账本与当前发布包一致。");
        }
        catch (MigrationException exception)
        {
            // 驱动异常可能包含连接信息；健康响应只允许暴露迁移器定义的稳定代码。
            return new SystemCheckResult("workflow.migrations", SystemCheckSeverity.Critical,
                SystemCheckStatus.Failed, exception.Code, "数据库迁移账本验证失败。");
        }
    }
}
