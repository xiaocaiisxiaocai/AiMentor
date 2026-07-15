namespace AiMentor.Application;

/// <summary>诊断项严重级别；Critical 失败会使服务不可就绪。</summary>
public enum SystemCheckSeverity { Information, Warning, Critical }

/// <summary>诊断项状态；Warning 表示可运行但不适合生产，Failed 表示不满足安全不变量。</summary>
public enum SystemCheckStatus { Passed, Warning, Failed }

/// <summary>不携带密钥、连接串和正文的结构化系统检查结果。</summary>
public sealed record SystemCheckResult(
    string Name,
    SystemCheckSeverity Severity,
    SystemCheckStatus Status,
    string Code,
    string Message);

/// <summary>汇总纯配置检查；只有不存在失败项时才可接收流量。</summary>
public sealed record SystemDiagnosticReport(
    bool IsReady,
    DateTimeOffset CheckedAt,
    IReadOnlyList<SystemCheckResult> Checks);

/// <summary>执行无外部副作用的系统配置诊断。</summary>
public interface ISystemDoctor
{
    Task<SystemDiagnosticReport> RunAsync(CancellationToken cancellationToken = default);
}
