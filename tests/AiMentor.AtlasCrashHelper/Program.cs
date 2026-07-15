using AiMentor.Domain;
using AiMentor.Infrastructure;

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"缺少强杀验收参数 {name}。");

try
{
    var connectionString = Required("AIMENTOR_CRASH_SQL");
    var runId = Required("AIMENTOR_CRASH_RUN_ID");
    var tenantId = Required("AIMENTOR_CRASH_TENANT");
    var subjectId = Required("AIMENTOR_CRASH_SUBJECT");
    var readyPath = Required("AIMENTOR_CRASH_READY_PATH");
    var key = Convert.FromBase64String(Required("AIMENTOR_CRASH_KEY"));
    var cipher = new AesGcmWorkflowStateCipher("v1", new Dictionary<string, byte[]> { ["v1"] = key });
    var options = new SqlServerWorkflowOptions { ConnectionString = connectionString, InitializeSchema = false };
    using var store = new SqlServerAtlasIncidentStore(options, cipher, TimeProvider.System);
    var lease = await store.TryAcquireAsync(runId, AccessContext.Create(tenantId, subjectId, []), 1,
        TimeSpan.FromSeconds(3));
    if (!lease.Acquired) return 3;

    // 信号不含连接串、密文或租约令牌；父进程看到 READY 后会对本进程执行强杀。
    await File.WriteAllTextAsync(readyPath, "READY");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
catch
{
    // 辅助进程只通过稳定退出码报告失败，避免异常文本泄漏连接或加密上下文。
    return 4;
}
