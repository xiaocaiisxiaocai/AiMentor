using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Evaluation;

public sealed class TrustedQuestionEvaluationTarget(ITrustedQuestionService service) : IEvaluationTarget
{
    public async Task<EvaluationObservation> ExecuteAsync(EvaluationInput input,
        CancellationToken cancellationToken = default)
    {
        var answer = await service.AskAsync(new TrustedQuestion(input.Case.Input, input.Access, input.Case.CaseId),
            cancellationToken);
        var terminalCode = answer.Trace.Reverse()
            .Select(step => step.Details.TryGetValue("code", out var code) ? code as string : null)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code)) ?? answer.Safety.Code;
        return new EvaluationObservation(Classify(answer), answer.Decision, answer.Safety.Action, answer.Safety.Code,
            terminalCode, answer.Answer, answer.Citations, answer.Trace);
    }

    private static ObservedAction Classify(TrustedAnswer answer)
    {
        if (answer.Decision == AnswerDecision.Failed) return ObservedAction.Failed;
        // 策略拒绝只能证明策略已运行，不能冒充 Workflow、记忆或冲突处置已经完成。
        if (answer.Safety.Action != SafetyAction.Allow) return ObservedAction.PolicyDecision;
        return answer.Decision switch
        {
            AnswerDecision.Answered => ObservedAction.Answer,
            AnswerDecision.Refused or AnswerDecision.InsufficientEvidence => ObservedAction.ClarifyOrRefuse,
            _ => ObservedAction.Failed
        };
    }
}
