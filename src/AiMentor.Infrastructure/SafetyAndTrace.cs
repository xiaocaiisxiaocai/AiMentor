using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

public sealed partial class RuleBasedInputSafetyService : IInputSafetyService
{
    public string PolicyVersion => SafetyPolicyVersions.Current;

    public SafetyDecision Review(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new(SafetyAction.Refuse, "EMPTY_INPUT", "问题不能为空。");
        if (input.Length > 4_000) return new(SafetyAction.Refuse, "INPUT_TOO_LONG", "问题长度超过安全限制。");
        if (SecretPattern().IsMatch(input)) return new(SafetyAction.Refuse, "SECRET_REQUEST", "不能处理索取、展示或推断密码、令牌、密钥等敏感凭证的请求。");
        if (InjectionPattern().IsMatch(input)) return new(SafetyAction.Refuse, "PROMPT_INJECTION", "检测到试图绕过系统规则或访问隐藏指令的内容，已拒绝处理。");
        if (PersonalDataPattern().IsMatch(input)) return new(SafetyAction.Refuse, "SENSITIVE_PERSONAL_DATA", "请求涉及高风险个人敏感信息，已拒绝处理。");
        if (MemoryMutationPattern().IsMatch(input)) return new(SafetyAction.RequireApproval, "MEMORY_OPERATION_REQUIRES_WORKFLOW", "记忆新增、修改或删除必须进入独立的授权工作流，不能作为普通知识问答处理。");
        return SafetyDecision.Allowed;
    }

    [GeneratedRegex(@"(?is)^(?=.{0,4000}$)(?=.*(密码|口令|access\s*token|refresh\s*token|api[_ -]?key|secret|私钥|jwt))(?=.*(显示|告诉|导出|泄露|获取|是什么|明文|查询|请)).*$")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?i)(忽略|绕过|无视|覆盖).{0,20}(系统|安全|之前|以上|指令|规则)|system\s*prompt|developer\s*message|隐藏指令")]
    private static partial Regex InjectionPattern();

    [GeneratedRegex(@"(?is)^(?=.{0,4000}$)(?=.*(身份证号|银行卡号|完整病历|个人征信))(?=.*(查询|导出|显示|告诉|获取|请)).*$")]
    private static partial Regex PersonalDataPattern();

    [GeneratedRegex(@"(?i)(以后.{0,20}(记住|默认)|帮我记住|保存.{0,12}偏好|删除.{0,12}偏好|写入.{0,12}(长期|记忆)|Agent.{0,12}错误总结|用户\s*[AB].{0,20}偏好)")]
    private static partial Regex MemoryMutationPattern();
}

/// <summary>供开发和测试读取运行轨迹的进程内实现。</summary>
public sealed class InMemoryTraceSink : ITraceSink
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<TraceStep>> _traces = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<TraceStep>> Traces => _traces;
    public Task WriteAsync(string runId, IReadOnlyList<TraceStep> trace, CancellationToken cancellationToken = default)
    {
        _traces[runId] = trace.ToArray();
        return Task.CompletedTask;
    }
}
