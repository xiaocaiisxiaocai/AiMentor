using System.Text.Json;

namespace AiMentor.Migrations;

public static class MigrationCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length != 0)
                throw new MigrationException("MIGRATION_ARGUMENTS_UNSUPPORTED", "迁移程序不接受命令行参数。");

            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__WorkflowSqlServer");
            var migrationsRoot = Environment.GetEnvironmentVariable("AIMENTOR_MIGRATIONS_ROOT");
            var releaseId = Environment.GetEnvironmentVariable("AIMENTOR_RELEASE_ID");
            var verifyOnlyValue = Environment.GetEnvironmentVariable("AIMENTOR_MIGRATIONS_VERIFY_ONLY");
            if (!string.IsNullOrWhiteSpace(verifyOnlyValue) && !bool.TryParse(verifyOnlyValue, out _))
                throw new MigrationException("MIGRATION_VERIFY_MODE_INVALID", "只读验证模式必须是布尔值。");
            var verifyOnly = bool.TryParse(verifyOnlyValue, out var parsedVerifyOnly) && parsedVerifyOnly;
            var scripts = MigrationDiscovery.Discover(migrationsRoot ?? string.Empty);
            if (verifyOnly)
            {
                var verification = await new SqlServerMigrationVerifier(connectionString ?? string.Empty)
                    .VerifyAsync(scripts, cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "Verified",
                    verification.DiscoveredCount,
                    verification.AppliedCount
                }));
                return 0;
            }
            var runner = new SqlServerMigrationRunner(new MigrationRunnerOptions(
                connectionString ?? string.Empty,
                releaseId ?? string.Empty));
            var result = await runner.RunAsync(scripts, cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "Succeeded",
                result.ReleaseId,
                result.DiscoveredCount,
                result.PreviouslyAppliedCount,
                result.AppliedCount
            }));
            return 0;
        }
        catch (OperationCanceledException)
        {
            WriteFailure("MIGRATION_CANCELLED", nameof(OperationCanceledException));
            return 2;
        }
        catch (MigrationException exception)
        {
            WriteFailure(exception.Code, exception.GetType().Name);
            return 2;
        }
        catch (Exception exception)
        {
            // CLI 不回显异常消息，避免驱动错误把连接信息带入部署日志。
            WriteFailure("MIGRATION_FAILED", exception.GetType().Name);
            return 2;
        }
    }

    private static void WriteFailure(string code, string errorType)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            status = "Failed",
            code,
            errorType
        }));
    }
}
